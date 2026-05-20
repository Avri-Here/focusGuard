using FocusGuard.Core.Security;

namespace FocusGuard.Core.Tests.Security;

public class PasswordHasherTests
{
    private static PasswordHasher FastHasher()
        => new(new Argon2idParams(MemoryKb: 1024, Iterations: 1, Parallelism: 1, HashLengthBytes: 32, SaltLengthBytes: 16));

    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var hasher = FastHasher();
        var hash = hasher.Hash("hunter2");
        Assert.True(hasher.Verify("hunter2", hash));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hasher = FastHasher();
        var hash = hasher.Hash("hunter2");
        Assert.False(hasher.Verify("hunter3", hash));
    }

    [Fact]
    public void Hash_ProducesPhcFormat()
    {
        var hasher = FastHasher();
        var hash = hasher.Hash("x");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash);
        // Six segments when split by '$' (leading empty + 5 parts).
        Assert.Equal(6, hash.Split('$').Length);
    }

    [Fact]
    public void Hash_ProducesDifferentHashesForSamePassword()
    {
        var hasher = FastHasher();
        Assert.NotEqual(hasher.Hash("same"), hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$YWFh$YmJi")] // wrong algo
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$bad-base64!$YmJi")]
    public void Verify_ReturnsFalseForMalformedHash(string malformed)
    {
        var hasher = FastHasher();
        Assert.False(hasher.Verify("anything", malformed));
    }

    [Fact]
    public void Verify_HandlesUnicodePasswords()
    {
        var hasher = FastHasher();
        var pwd = "סִיסְמָה-Ω-😀";
        var hash = hasher.Hash(pwd);
        Assert.True(hasher.Verify(pwd, hash));
        Assert.False(hasher.Verify(pwd + " ", hash));
    }
}
