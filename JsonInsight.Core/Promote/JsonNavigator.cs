using System.Text.Json.Nodes;
using JsonInsight.Diff;

namespace JsonInsight.Promote;

/// <summary>
/// Resolves canonical configuration paths against a JsonNode tree, including the
/// <c>Segment[Field=Value]</c> form used for keyed array elements.
/// </summary>
public static class JsonNavigator
{
    /// <summary>Returns the node at <paramref name="path"/>, or null if any segment is absent.</summary>
    public static JsonNode? Find(JsonNode root, string path)
    {
        if (path.Length == 0)
        {
            return root;
        }

        JsonNode? current = root;
        foreach (var segment in ConfigPath.Split(path))
        {
            if (current is null)
            {
                return null;
            }

            current = Step(current, segment);
        }

        return current;
    }

    private static JsonNode? Step(JsonNode current, string segment)
    {
        var (name, identityField, identityValue, index) = ParseSegment(segment);

        // A segment with no name in front of its brackets - "[0]", "[code=bundle-a]" - is an element
        // of the node itself rather than of one of its keys. That is what the first segment of a
        // path looks like when the document's root is an array, and what every segment after an
        // element looks like in an array of arrays.
        JsonNode? container = current;

        if (name.Length > 0)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out var child))
            {
                return null;
            }

            if (identityField is null && index is null)
            {
                return child;
            }

            container = child;
        }
        else if (identityField is null && index is null)
        {
            // Not a name and not an element: nothing this could resolve to.
            return null;
        }

        if (container is not JsonArray array)
        {
            return null;
        }

        if (index is { } i)
        {
            return i >= 0 && i < array.Count ? array[i] : null;
        }

        var found = IndexOfIdentity(array, identityField!, identityValue);
        return found < 0 ? null : array[found];
    }

    /// <summary>
    /// The position of the element whose <paramref name="identityField"/> holds
    /// <paramref name="identityValue"/>, or -1 when no element does.
    ///
    /// <para>
    /// The single place the identity match is spelled out. There were two — <see cref="Step"/> wants
    /// the element and <c>DocumentEditor.ElementSlot</c> wants the slot to assign into — and with
    /// them two copies of the rule that only a <em>string</em>-valued identity field counts, so
    /// <c>WriteTo[Name=Seq]</c> matches the sink whose Name is the string "Seq" and never a number, a
    /// boolean, or an object that happens to stringify to it. Navigating to an element and replacing
    /// that same element have to pick the same one or an edit lands somewhere other than where the
    /// tree showed it.
    /// </para>
    /// </summary>
    internal static int IndexOfIdentity(JsonArray array, string identityField, string? identityValue)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject item &&
                item[identityField] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                string.Equals(text, identityValue, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Ensures the object at <paramref name="path"/> exists, creating intermediate objects, and
    /// returns it.
    ///
    /// <para>
    /// An array element on the way through is <em>walked</em> when it is already there and refused
    /// when it is not: stepping into <c>Serilog:WriteTo[Name=Seq]</c> to reach a key inside it is an
    /// ordinary navigation, while creating that element would mean inventing a position in an
    /// ordered list, which no caller here has the information to do. Reverting a key inside an
    /// element is the case that needs the first half; promote needs neither and asks for neither.
    /// </para>
    /// </summary>
    public static JsonObject EnsureObject(JsonNode root, string path)
    {
        if (path.Length == 0)
        {
            return root as JsonObject
                   ?? throw new InvalidOperationException("Document root is not a JSON object.");
        }

        JsonNode current = root;

        foreach (var segment in ConfigPath.Split(path))
        {
            var (name, identityField, _, index) = ParseSegment(segment);

            if (identityField is not null || index is not null)
            {
                current = Step(current, segment)
                          ?? throw new InvalidOperationException(
                              $"Cannot create the array element '{segment}' — an element has a position, " +
                              "and inventing one would put it somewhere nobody chose.");
                continue;
            }

            if (current is not JsonObject holder)
            {
                throw new InvalidOperationException(
                    $"'{path}' passes through something that is not an object in the destination.");
            }

            if (!holder.TryGetPropertyValue(name, out var child) || child is null)
            {
                var created = new JsonObject();
                holder[name] = created;
                current = created;
                continue;
            }

            current = child;
        }

        return current as JsonObject
               ?? throw new InvalidOperationException(
                   $"'{path}' is not an object in the destination.");
    }

    public static (string Name, string? IdentityField, string? IdentityValue, int? Index) ParseSegment(string segment)
    {
        var open = segment.IndexOf('[');
        if (open < 0 || !segment.EndsWith(']'))
        {
            return (segment, null, null, null);
        }

        var name = segment[..open];
        var inner = segment[(open + 1)..^1];

        var equals = inner.IndexOf('=');
        if (equals > 0)
        {
            return (name, inner[..equals], inner[(equals + 1)..], null);
        }

        return int.TryParse(inner, out var index)
            ? (name, null, null, index)
            : (segment, null, null, null);
    }
}
