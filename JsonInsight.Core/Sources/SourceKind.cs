namespace JsonInsight.Sources;

/// <summary>
/// Where a source's content lives and how it is read and written.
///
/// <para>
/// Every <see cref="JsonInsight.Loading.TierDefinition"/> names one of these. Today every configured
/// tier is <see cref="Vault"/>; <see cref="LocalFile"/> is what lets a tier be a plain JSON file on
/// disk instead, with its own load and save path rather than a Vault read and a Vault push. Adding a
/// third kind later is a new <see cref="ISourceProvider"/>, not a change to this list's two existing
/// members' meaning.
/// </para>
/// </summary>
public enum SourceKind
{
    Vault,
    LocalFile,
}
