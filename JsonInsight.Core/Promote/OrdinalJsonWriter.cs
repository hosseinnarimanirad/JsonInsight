using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonInsight.Promote;

/// <summary>
/// Reproduces the exact byte format of the Vault snapshot files.
///
/// The contract, measured from stage.v26.json and beta.json: 2-space indent, CRLF line endings,
/// no BOM, no trailing newline, raw UTF-8 (Persian text appears literally, never as \uXXXX),
/// and every object key ordinal-sorted at every depth. Array element order is as-authored and
/// is never touched.
///
/// Each of those choices is load-bearing:
///  - The default JavaScriptEncoder escapes non-ASCII, '&amp;' and '+', which would rewrite every
///    Persian line plus 5 other spots in the files.
///  - Ordinal sorting (not culture-aware, not OrdinalIgnoreCase) is what puts lowercase 'otp'
///    last in Modules and ConnectTimeoutMs before ConnectionString in Redis.
///  - JsonNode preserves numbers as raw tokens, so 5000 stays 5000 rather than becoming 5000.0.
/// </summary>
public static class OrdinalJsonWriter
{
    /// <summary>
    /// The indent and newline are not set here, because on .NET 8 they cannot be: <c>IndentCharacter</c>,
    /// <c>IndentSize</c> and <c>NewLine</c> arrived in .NET 9. They were only ever restating the
    /// defaults — an indented <see cref="Utf8JsonWriter"/> writes two spaces and
    /// <see cref="Environment.NewLine"/> on both runtimes — so the bytes are unchanged.
    ///
    /// <para>
    /// One guarantee is weaker than it looks, though: CRLF now comes from the operating system
    /// rather than from this file. This is a <c>net8.0-windows</c> WPF app, so it is CRLF in every
    /// context it can run in, and <c>Line_endings_are_crlf_only</c> is the test that says so out
    /// loud. Anything that ever makes this code run off Windows has to pin the newline again.
    /// </para>
    /// </summary>
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        // dev/appsettings.json carries 119 whole-line // comments. We skip them on read and
        // never write that file back, which is why skipping is safe here.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128,
    };

    /// <summary>Parses JSON (tolerating comments) into a mutable node tree.</summary>
    public static JsonNode Parse(string text) =>
        ParseAllowingNull(text) ?? throw new InvalidDataException("Document parsed to null.");

    /// <summary>
    /// The same parse, but returning null for the JSON literal <c>null</c> rather than rejecting it.
    ///
    /// <para>
    /// A whole document may not be null and <see cref="Parse"/> is right to refuse it, but a
    /// <em>value</em> may: a key holding null is a real configuration state that this app compares,
    /// classifies and writes like any other. Callers replacing one node use this; callers parsing a
    /// document use the other.
    /// </para>
    /// </summary>
    public static JsonNode? ParseAllowingNull(string text) =>
        JsonNode.Parse(text, nodeOptions: null, DocumentOptions);

    public static JsonNode ParseFile(string path) => Parse(ReadText(path));

    /// <summary>Reads a file as UTF-8, stripping a BOM if one is present.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeUtf8(bytes);
    }

    public static string DecodeUtf8(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Returns a deep copy with every object's keys ordinal-sorted at every depth.
    /// Rebuilds rather than trusting insertion order, so a promoted key lands in its sorted slot.
    /// </summary>
    public static JsonNode SortedClone(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var key in obj.Select(p => p.Key).Order(StringComparer.Ordinal))
                {
                    var child = obj[key];
                    result[key] = child is null ? null : SortedClone(child);
                }

                return result;
            }

            case JsonArray array:
            {
                // Element order is as-authored and semantically meaningful (Serilog sink order).
                var result = new JsonArray();
                foreach (var element in array)
                {
                    result.Add(element is null ? null : SortedClone(element));
                }

                return result;
            }

            default:
                return node.DeepClone();
        }
    }

    /// <summary>Serializes to the exact byte format of the Vault snapshots.</summary>
    public static byte[] Serialize(JsonNode node, bool sort = true)
    {
        var toWrite = sort ? SortedClone(node) : node;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            toWrite.WriteTo(writer);
        }

        return stream.ToArray();
    }

    public static string SerializeToText(JsonNode node, bool sort = true) =>
        Encoding.UTF8.GetString(Serialize(node, sort));

    /// <summary>
    /// The same content on one line, for reading or pasting a node without the indentation.
    ///
    /// <para>
    /// A separate options object rather than a flag on <see cref="Options"/>: that one defines the
    /// exact byte format of the snapshot files and is what the round-trip guard measures against, so
    /// it stays immutable. This is display only - nothing compact is ever written to disk.
    /// </para>
    /// </summary>
    public static string SerializeCompactToText(JsonNode node, bool sort = true)
    {
        var toWrite = sort ? SortedClone(node) : node;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CompactOptions))
        {
            toWrite.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly JsonWriterOptions CompactOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// Re-serializes a file's own content and compares bytes. This is the guard that runs before
    /// any write: if the writer cannot reproduce the file it was given, it has no business
    /// producing the file's replacement, and the app stays read-only.
    /// </summary>
    public static RoundTripResult VerifyRoundTrip(string path)
    {
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return RoundTripResult.Failed(path, 0, 0, $"Could not read file: {ex.Message}");
        }

        byte[] produced;
        try
        {
            produced = Serialize(Parse(DecodeUtf8(original)));
        }
        catch (Exception ex)
        {
            return RoundTripResult.Failed(path, original.Length, 0, $"Could not re-serialize: {ex.Message}");
        }

        if (original.AsSpan().SequenceEqual(produced))
        {
            return new RoundTripResult(path, true, original.Length, produced.Length, null);
        }

        return RoundTripResult.Failed(path, original.Length, produced.Length,
            DescribeFirstDifference(original, produced));
    }

    /// <summary>Locates the first differing line so a failure is actionable rather than "bytes differ".</summary>
    private static string DescribeFirstDifference(byte[] expected, byte[] actual)
    {
        var expectedLines = SplitLines(DecodeUtf8(expected));
        var actualLines = SplitLines(DecodeUtf8(actual));

        var shared = Math.Min(expectedLines.Length, actualLines.Length);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(expectedLines[i], actualLines[i], StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:\r\n" +
                       $"  on disk : {Truncate(expectedLines[i])}\r\n" +
                       $"  produced: {Truncate(actualLines[i])}";
            }
        }

        if (expectedLines.Length != actualLines.Length)
        {
            return $"Line count differs: on disk {expectedLines.Length}, produced {actualLines.Length}.";
        }

        // Lines match as text but bytes differ: line endings, BOM, or a trailing newline.
        var expectedCrLf = CountOccurrences(expected, "\r\n"u8);
        var actualCrLf = CountOccurrences(actual, "\r\n"u8);
        return $"Text matches but bytes differ (CRLF on disk {expectedCrLf}, produced {actualCrLf}) " +
               "- check BOM, line endings, or a trailing newline.";
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Truncate(string value, int max = 120) =>
        value.Length <= max ? value : value[..max] + "…";

    private static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle);
        while (index >= 0)
        {
            count++;
            haystack = haystack[(index + needle.Length)..];
            index = haystack.IndexOf(needle);
        }

        return count;
    }
}

public sealed record RoundTripResult(
    string Path,
    bool Passed,
    int OriginalBytes,
    int ProducedBytes,
    string? Detail)
{
    public static RoundTripResult Failed(string path, int original, int produced, string detail) =>
        new(path, false, original, produced, detail);

    public string Summary => Passed
        ? $"PASS  {System.IO.Path.GetFileName(Path)}  ({OriginalBytes:N0} bytes)"
        : $"FAIL  {System.IO.Path.GetFileName(Path)}  (on disk {OriginalBytes:N0} bytes, produced {ProducedBytes:N0})";
}
