using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FocusGuard.Core.Security;

/// <summary>
/// Abstraction over a typed persistent store for config/state objects. Allows tests to swap
/// in an in-memory implementation without touching DPAPI.
/// </summary>
public interface IObjectStore<T>
{
    bool Exists();
    T? Load();
    void Save(T value);
}

/// <summary>
/// DPAPI-encrypted JSON object store, machine scope. Atomic writes via temp + replace.
/// Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiStore<T> : IObjectStore<T>
{
    private readonly string _path;
    private readonly byte[]? _entropy;
    private readonly JsonSerializerOptions _json;

    public DpapiStore(string path, byte[]? entropy = null, JsonSerializerOptions? json = null)
    {
        _path = path;
        _entropy = entropy;
        _json = json ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
    }

    public bool Exists() => File.Exists(_path);

    public T? Load()
    {
        if (!File.Exists(_path)) return default;
        var encrypted = File.ReadAllBytes(_path);
        var plaintext = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.LocalMachine);
        var json = Encoding.UTF8.GetString(plaintext);
        return JsonSerializer.Deserialize<T>(json, _json);
    }

    public void Save(T value)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.SerializeToUtf8Bytes(value, _json);
        var encrypted = ProtectedData.Protect(json, _entropy, DataProtectionScope.LocalMachine);

        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        if (File.Exists(_path))
            File.Replace(temp, _path, destinationBackupFileName: null);
        else
            File.Move(temp, _path);
    }
}

/// <summary>In-memory store for tests.</summary>
public sealed class InMemoryStore<T> : IObjectStore<T>
{
    private bool _has;
    private T? _value;

    public bool Exists() => _has;
    public T? Load() => _value;

    public void Save(T value)
    {
        _value = value;
        _has = true;
    }
}
