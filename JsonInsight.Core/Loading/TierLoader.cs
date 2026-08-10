using System.IO;
using JsonInsight.Classify;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Loading;

/// <summary>
/// Reads a JSON file from disk into a <see cref="TierDocument"/>.
///
/// <para>
/// Not for tiers — those come from Vault, and only from Vault. This is what the Compare files tab
/// browses with: any two JSON files, loaded through the same flattener, array strategies and secret
/// classification as a tier so the comparison means the same thing, and marked read-only on the way
/// in so nothing downstream can mistake one for something this app may write.
/// </para>
/// </summary>
public sealed class TierLoader
{
    private readonly Flattener _flattener;

    public TierLoader(ArrayStrategies arrays, Classifier classifier)
    {
        _flattener = new Flattener(arrays, classifier);
    }

    public static TierLoader Default() =>
        new(ArrayStrategies.Load(), Classifier.Load());

    /// <summary>Loads a file under a given id, which is what the flattener labels its leaves with.</summary>
    public TierDocument LoadFile(string path, string? id = null, string? label = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file at {path}.", path);
        }

        var name = Path.GetFileName(path);
        var text = OrdinalJsonWriter.DecodeUtf8(File.ReadAllBytes(path));

        // Comments are skipped on the way in. Nothing read here is ever written back, so a file
        // carrying them loses nothing by being read.
        var root = OrdinalJsonWriter.Parse(text);
        var definition = new TierDefinition
        {
            Id = id ?? name,
            Label = label ?? name,

            // The only place this is ever false. A configured source defaults to writable; this is
            // an arbitrary JSON somebody browsed to in order to read it, and the push and save paths
            // both check this flag rather than trusting where the document came from.
            Writable = false,
        };

        return new TierDocument
        {
            Definition = definition,
            Root = root,
            Flat = _flattener.Flatten(definition.Id, root),
            Origin = TierOrigin.LocalFile,
            FilePath = Path.GetFullPath(path),
        };
    }
}
