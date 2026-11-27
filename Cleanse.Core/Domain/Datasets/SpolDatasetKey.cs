using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Cleanse.Core.Domain.Datasets;

/// <summary>
/// The secret that lets the connector load one dataset. It is shown once, when the dataset is
/// created. Only its SHA-256 is kept, and keys are compared in constant time.
/// </summary>
public static class SpolDatasetKey
{
    /// <summary>Fixed, so that secret scanners can recognise a key that has leaked.</summary>
    public const string Prefix = "cleanse_ds_";

    private const int RandomBytes = 32;

    /// <summary>Longer than any key this class makes, so nothing longer is worth hashing.</summary>
    private const int MaxLength = 128;

    /// <summary>Stands in for an unknown dataset's hash, so that it takes as long to refuse.</summary>
    private static readonly string Unmatchable = Hash(Prefix);

    public static (string Key, string Hash) Generate()
    {
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));
        return (key, Hash(key));
    }

    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the key whose hash is <paramref name="keyHash"/>.
    /// Pass a null hash for a dataset that does not exist: the answer is the same, and as slow.
    /// </summary>
    public static bool Matches(string? keyHash, string? candidate)
    {
        var expected = Convert.FromHexString(keyHash ?? Unmatchable);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(
            candidate is { Length: > 0 and <= MaxLength } ? candidate : string.Empty));

        return CryptographicOperations.FixedTimeEquals(expected, actual) &&
               keyHash is not null &&
               candidate is { Length: > 0 and <= MaxLength };
    }
}
