using JsonInsight.Classify;
using JsonInsight.Diff;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight;

/// <summary>
/// Headless summary of every comparison the app performs. Exists so the whole pipeline can be
/// exercised and eyeballed without launching the UI.
///
/// <para>
/// It reads Vault, because that is where a tier is. There is nothing on disk to check against, and a
/// headless mode that quietly checked something else would be the one thing worse than not having one.
/// </para>
/// </summary>
public static class CheckRunner
{
    public static int Run(bool verbose)
    {
        var aliases = AliasSet.Load();
        var flattener = new Flattener(ArrayStrategies.Load(), Classifier.Load());

        // The project that was open last, since a headless check has nobody to ask which one to run.
        var (workspace, settingsProblems) = VaultSettingsStore.LoadWorkspace();
        var settings = workspace.SettingsFor(workspace.ActiveProject);

        var (catalog, catalogProblems) = SourceCatalog.Build(settings, TiersConfig.Load());
        var (tiers, documentProblems) = DocumentTiers.For(catalog, settings, ConfigDocument.Root);

        Console.WriteLine();
        Console.WriteLine($"Content root : {AppPaths.ContentRoot}");
        Console.WriteLine($"Config       : {AppPaths.ConfigDirectory}");
        Console.WriteLine($"Project      : {(workspace.ActiveProject.Length == 0 ? "(none)" : workspace.ActiveProject)}");
        Console.WriteLine();

        foreach (var problem in settingsProblems.Concat(catalogProblems).Concat(documentProblems))
        {
            Console.WriteLine($"  ! {problem}");
        }

        var report = new TierRefresher(flattener).RefreshAsync(tiers, settings).GetAwaiter().GetResult();
        var documents = report.Documents;

        Console.WriteLine("TIERS");
        foreach (var line in report.Lines)
        {
            Console.WriteLine($"  {line.Text}");
        }

        foreach (var document in documents)
        {
            Console.WriteLine(
                $"  {document.Id,-6} {document.Flat.Count,5} leaves   " +
                $"{document.Flat.Sections.Count,3} sections   " +
                $"{(document.Writable ? "writable" : "READ-ONLY"),-9} {document.SourceLine}");

            foreach (var warning in document.Warnings)
            {
                Console.WriteLine($"         ! {warning}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("PAIRWISE  (raw = aliases off, so the alias machinery can be seen working)");
        var differ = new TierDiffer(aliases);
        var rawDiffer = new TierDiffer(AliasSet.Empty());

        for (var i = 0; i < documents.Count; i++)
        {
            for (var j = i + 1; j < documents.Count; j++)
            {
                var left = documents[i];
                var right = documents[j];

                var raw = rawDiffer.Compare(left.Flat, right.Flat);
                var aliased = differ.Compare(left.Flat, right.Flat);

                Console.WriteLine(
                    $"  {left.Id} -> {right.Id,-6}  " +
                    $"raw {raw.OnlyInLeft,3} only-{left.Id} / {raw.OnlyInRight,3} only-{right.Id}   " +
                    $"aliased {aliased.OnlyInLeft,3} / {aliased.OnlyInRight,3}   " +
                    $"values {aliased.ValueDifferences,3}   shape {aliased.ShapeDifferences,2}   " +
                    $"meaningful {aliased.Meaningful,3}   expected-infra {aliased.Expected,3}");

                foreach (var alias in aliased.AppliedAliases)
                {
                    Console.WriteLine($"        alias '{alias.Id}': {alias.DisplayPath}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("SIDE-BY-SIDE");
        var multi = MultiDiff.Build(documents.Select(d => d.Flat).ToArray(), aliases);
        Console.WriteLine(
            $"  {multi.Rows.Count} paths   missing {multi.MissingCount}   differs {multi.DifferingCount}   " +
            $"meaningful {multi.MeaningfulCount}   expected-infra {multi.ExpectedCount}");

        var tree = DiffNode.Build(multi);
        var rollups = CollectRollups(tree).ToArray();
        Console.WriteLine($"  {rollups.Length} rolled-up subtrees (each is one promote unit):");
        foreach (var node in rollups.OrderByDescending(n => n.LeafCount).Take(verbose ? 100 : 12))
        {
            Console.WriteLine(
                $"    {node.Path,-62} {node.LeafCount,3} keys   missing from: " +
                string.Join(", ", node.UniformlyMissingFrom!));
        }

        Console.WriteLine();

        // A tier that could not be read is a failure of the check, not a footnote: every count above
        // it was computed without that tier, and a green exit code would say they were not.
        return report.UnavailableCount == 0 ? 0 : 1;
    }

    /// <summary>Highest nodes whose entire subtree is missing from the same tiers.</summary>
    public static IEnumerable<DiffNode> CollectRollups(DiffNode root)
    {
        foreach (var child in root.Children)
        {
            if (child.IsUniformlyMissing)
            {
                yield return child;
                continue;
            }

            foreach (var node in CollectRollups(child))
            {
                yield return node;
            }
        }
    }
}
