using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace FocusGuard.Core.Security;

public sealed record Argon2idParams(int MemoryKb, int Iterations, int Parallelism, int HashLengthBytes, int SaltLengthBytes)
{
    public static Argon2idParams Default { get; } = new(MemoryKb: 65536, Iterations: 3, Parallelism: 1, HashLengthBytes: 32, SaltLengthBytes: 16);
}

/// <summary>
/// Argon2id wrapper producing PHC-style strings: <c>$argon2id$v=19$m=...,t=...,p=...$&lt;salt&gt;$&lt;hash&gt;</c>
/// where salt and hash are unpadded base64.
/// </summary>
public sealed class PasswordHasher
{
    private readonly Argon2idParams _params;

    public PasswordHasher() : this(Argon2idParams.Default) { }

    public PasswordHasher(Argon2idParams parameters)
    {
        _params = parameters;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(_params.SaltLengthBytes);
        byte[] hash = ComputeHash(Encoding.UTF8.GetBytes(password), salt, _params);
        return Format(_params, salt, hash);
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(encodedHash);
        if (!TryParse(encodedHash, out var p, out var salt, out var expected))
            return false;

        byte[] actual = ComputeHash(Encoding.UTF8.GetBytes(password), salt, p with { HashLengthBytes = expected.Length });
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeHash(byte[] password, byte[] salt, Argon2idParams p)
    {
        using var argon = new Argon2id(password)
        {
            Salt = salt,
            DegreeOfParallelism = p.Parallelism,
            Iterations = p.Iterations,
            MemorySize = p.MemoryKb,
        };
        return argon.GetBytes(p.HashLengthBytes);
    }

    private static string Format(Argon2idParams p, byte[] salt, byte[] hash)
    {
        return $"$argon2id$v=19$m={p.MemoryKb},t={p.Iterations},p={p.Parallelism}${B64(salt)}${B64(hash)}";
    }

    internal static bool TryParse(string encoded, out Argon2idParams parameters, out byte[] salt, out byte[] hash)
    {
        parameters = Argon2idParams.Default;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        // Format: $argon2id$v=19$m=65536,t=3,p=1$<saltB64>$<hashB64>
        // string.Split with leading '$' yields an empty first element.
        var parts = encoded.Split('$');
        if (parts.Length != 6) return false;
        if (parts[0].Length != 0) return false;
        if (!string.Equals(parts[1], "argon2id", StringComparison.Ordinal)) return false;
        if (!string.Equals(parts[2], "v=19", StringComparison.Ordinal)) return false;

        int? memKb = null, iter = null, par = null;
        foreach (var kv in parts[3].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2) return false;
            if (!int.TryParse(pair[1], out var v)) return false;
            switch (pair[0])
            {
                case "m": memKb = v; break;
                case "t": iter = v; break;
                case "p": par = v; break;
                default: return false;
            }
        }
        if (memKb is null || iter is null || par is null) return false;

        try
        {
            salt = FromB64(parts[4]);
            hash = FromB64(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        parameters = new Argon2idParams(memKb.Value, iter.Value, par.Value, hash.Length, salt.Length);
        return true;
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static byte[] FromB64(string s)
    {
        // Re-pad to a multiple of 4.
        int pad = (4 - (s.Length % 4)) % 4;
        return Convert.FromBase64String(s + new string('=', pad));
    }
}
