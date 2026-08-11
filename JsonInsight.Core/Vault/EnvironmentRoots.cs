namespace JsonInsight.Vault;

/// <summary>
/// Works out where a tier's secrets live when nobody has said.
///
/// <para>
/// These deployments name their environment secrets after the environments:
/// <c>kv/app/stage</c>, <c>…/beta</c>, <c>…/prod</c>. When every root that <em>is</em>
/// known follows that shape and agrees on the prefix, the one that is missing is not a mystery — it
/// is the same prefix with a different last segment, and asking someone to type it out is asking
/// them to repeat what the other three rows already say.
/// </para>
///
/// <para>
/// It infers only from unanimity. One root that does not fit the pattern, or two prefixes that
/// disagree, and it returns nothing rather than guessing: a wrong path would send a read somewhere
/// nobody chose, and "I could not work it out" is a fine answer when the alternative is a confident
/// mistake.
/// </para>
/// </summary>
public static class EnvironmentRoots
{
    /// <param name="known">Tier id to environment root, for every tier that has one.</param>
    /// <returns>The root <paramref name="tierId"/> would have under the same scheme, or null.</returns>
    public static string? Infer(string tierId, IReadOnlyDictionary<string, string> known)
    {
        if (string.IsNullOrWhiteSpace(tierId))
        {
            return null;
        }

        string? prefix = null;

        foreach (var (id, root) in known)
        {
            var trimmed = root.Trim().Trim('/');
            var suffix = "/" + id;

            if (id.Equals(tierId, StringComparison.OrdinalIgnoreCase) ||
                !trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = trimmed[..^suffix.Length];

            if (candidate.Length == 0)
            {
                return null;
            }

            if (prefix is null)
            {
                prefix = candidate;
            }
            else if (!prefix.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                // Two schemes in one deployment. Which one a fourth tier would follow is a guess,
                // and this returns answers rather than guesses.
                return null;
            }
        }

        return prefix is null ? null : $"{prefix}/{tierId}";
    }
}
