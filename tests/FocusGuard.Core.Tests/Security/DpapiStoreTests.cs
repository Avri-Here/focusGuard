using System.Runtime.InteropServices;
using FocusGuard.Core;
using FocusGuard.Core.Ipc;
using FocusGuard.Core.Security;

namespace FocusGuard.Core.Tests.Security;

public class DpapiStoreTests : IDisposable
{
    private readonly string _dir;

    public DpapiStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "FocusGuardTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    [SkippableFact]
    public void RoundTripsEncryptedConfig()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var path = Path.Combine(_dir, "config.dat");
        var store = new DpapiStore<FocusGuardConfig>(path);
        Assert.False(store.Exists());

        store.Save(new FocusGuardConfig
        {
            PasswordHash = "$argon2id$v=19$m=1024,t=1,p=1$YWFh$YmJi",
            Whitelist = { "example.com", "wikipedia.org" },
            AdminPauseDefaultMinutes = 45,
        });

        Assert.True(store.Exists());
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(45, loaded!.AdminPauseDefaultMinutes);
        Assert.Equal(new[] { "example.com", "wikipedia.org" }, loaded.Whitelist);
    }

    [SkippableFact]
    public void EncryptedFileIsNotPlaintextJson()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var path = Path.Combine(_dir, "state.dat");
        var store = new DpapiStore<FocusGuardState>(path);
        store.Save(new FocusGuardState { State = FocusState.Browsing, MinutesRemaining = 12.5 });
        var bytes = File.ReadAllBytes(path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("Browsing", asText);
        Assert.DoesNotContain("minutesRemaining", asText);
    }

    [SkippableFact]
    public void EntropyMismatchFailsToDecrypt()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var path = Path.Combine(_dir, "config.dat");
        var entropyA = new byte[] { 1, 2, 3, 4 };
        var entropyB = new byte[] { 9, 9, 9, 9 };
        var saver = new DpapiStore<FocusGuardConfig>(path, entropyA);
        saver.Save(new FocusGuardConfig { PasswordHash = "x" });

        var loader = new DpapiStore<FocusGuardConfig>(path, entropyB);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => loader.Load());
    }

    [Fact]
    public void InMemoryStore_RoundTrips()
    {
        var store = new InMemoryStore<FocusGuardConfig>();
        Assert.False(store.Exists());
        store.Save(new FocusGuardConfig { PasswordHash = "h" });
        Assert.True(store.Exists());
        Assert.Equal("h", store.Load()!.PasswordHash);
    }
}
