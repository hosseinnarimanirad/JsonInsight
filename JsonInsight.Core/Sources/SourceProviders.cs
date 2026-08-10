using JsonInsight.Loading;
using JsonInsight.Model;

namespace JsonInsight.Sources;

/// <summary>
/// The one place that knows which <see cref="ISourceProvider"/> serves which <see cref="SourceKind"/>.
///
/// <para>
/// It exists because there used to be two answers. The read path dispatched through a registry — so a
/// local-file tier loaded correctly from the moment <see cref="LocalFileSourceProvider"/> was written —
/// while every write path called <c>VaultPusher</c> by name, which meant a local-file tier could be
/// compared and edited but not saved: the push refused it for naming no <c>vaultPath</c>, having never
/// been asked what kind of source it was. Both paths resolve here now, so adding a third kind is a
/// provider and one line rather than a search for everywhere the second kind was assumed.
/// </para>
/// </summary>
public static class SourceProviders
{
    public static IReadOnlyDictionary<SourceKind, ISourceProvider> Default(Flattener flattener) =>
        new ISourceProvider[]
        {
            new VaultSourceProvider(flattener),
            new LocalFileSourceProvider(flattener),
        }.ToDictionary(p => p.Kind);

    /// <summary>
    /// The provider for one tier's kind. Falls back to Vault, which is what every tier was before
    /// <see cref="SourceKind"/> existed and what a <see cref="TierDefinition"/> with no kind still
    /// defaults to — a null here would turn a stale definition into a crash on the push button.
    /// </summary>
    public static ISourceProvider For(SourceKind kind, Flattener flattener)
    {
        var providers = Default(flattener);

        return providers.TryGetValue(kind, out var provider)
            ? provider
            : providers[SourceKind.Vault];
    }

    public static ISourceProvider For(TierDocument? tier, Flattener flattener) =>
        For(tier?.Definition.Kind ?? SourceKind.Vault, flattener);
}
