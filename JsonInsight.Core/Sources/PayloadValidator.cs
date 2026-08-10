using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Sources;

/// <summary>
/// The one payload check every write path shares, regardless of where it is headed: serialize the
/// tree, re-parse the result, and refuse unless the re-parse holds precisely the keys the tree does.
/// It is what catches a serializer that dropped or invented a key on the way out, before that payload
/// reaches Vault or a file rather than being discovered in tomorrow's diff.
///
/// <para>
/// Originally <c>VaultPusher.Payload</c>, which still exists and still passes its own tests — it now
/// delegates here so a local-file save gets the identical check rather than a second copy of it.
/// </para>
/// </summary>
public static class PayloadValidator
{
    public static (string? Text, string? Problem) Validate(Flattener flattener, string tierId, JsonNode updated)
    {
        FlatConfig expected;
        string text;
        try
        {
            expected = flattener.Flatten(tierId, updated);
            text = OrdinalJsonWriter.SerializeToText(updated);
        }
        catch (Exception ex)
        {
            return (null, $"Could not serialize {tierId}: {ex.Message}");
        }

        FlatConfig reparsed;
        try
        {
            reparsed = flattener.Flatten(tierId, OrdinalJsonWriter.Parse(text));
        }
        catch (Exception ex)
        {
            return (null, $"The payload did not re-parse: {ex.Message}");
        }

        var before = expected.Paths.ToHashSet(StringComparer.Ordinal);
        var after = reparsed.Paths.ToHashSet(StringComparer.Ordinal);

        var lost = before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var gained = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        if (lost.Length == 0 && gained.Length == 0)
        {
            return (text, null);
        }

        var parts = new List<string>();
        if (lost.Length > 0)
        {
            parts.Add($"{lost.Length} key(s) would be lost: {string.Join(", ", lost.Take(5))}");
        }

        if (gained.Length > 0)
        {
            parts.Add($"{gained.Length} unexpected key(s): {string.Join(", ", gained.Take(5))}");
        }

        return (null, "Refusing to write — serializing this document does not reproduce it. " +
                      string.Join("; ", parts));
    }
}
