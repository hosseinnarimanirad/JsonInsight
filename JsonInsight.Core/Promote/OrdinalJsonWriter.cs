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

    /// <summary>
    /// How every JSON this app reads is parsed: the compared documents, the four hand-authored rule
    /// files under <c>config/</c>, and this app's own appsettings.json and secrets.json.
    ///
    /// <para>
    /// One object rather than the six inline copies there used to be. Each was spelled the same way,
    /// which is the problem: "did the reader that produced this tolerate a trailing comma" is not a
    /// question anyone should have to answer per call site, and six copies is six chances for the
    /// answer to stop being the same one.
    /// </para>
    /// </summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        // dev/appsettings.json carries 119 whole-line // comments and the rule files under config/
        // are commented throughout. They are skipped on read, which is safe because nothing that is
        // read with comments in it is ever written back from the parsed tree: this app's own
        // appsettings.json is rewritten, but it carries its notes as a "// note" *key* rather than
        // as comments, precisely so that round trip loses nothing.
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
    /// exact byte format every write leaves in, so it stays immutable. This is display only -
    /// nothing compact is ever written to disk.
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

}
