using System.Security.Cryptography;
using System.Text;
using JsonInsight.Model;

namespace JsonInsight.Classify;

/// <summary>
/// Renders secrets without revealing them.
///
/// The fingerprint is the point: two tiers showing the same short hash hold the same secret, which
/// is the question you actually need answered when comparing environments, and it is answerable
/// without the value ever reaching the screen, the clipboard, or a log.
/// </summary>
public static class SecretMasker
{
    public static string Describe(Leaf leaf) =>
        leaf.Class == ValueClass.Secret ? Describe(leaf.ComparableValue) : leaf.ComparableValue;

    public static string Describe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return $"{Leaf.SecretPlaceholder}  len {value.Length}  {Fingerprint(value)}";
    }

    /// <summary>First 6 hex characters of the SHA-256 of the value.</summary>
    public static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..6].ToLowerInvariant();
    }
}
