using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using JsonInsight.Diff;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Classify;

/// <summary>
/// Decides whether a value is a secret, deployment infrastructure, or a business constant.
///
/// It picks the promote default (copy a bank terminal id verbatim; never copy a stage URL into
/// beta), and it is what stops the grid drowning in false drift: the tiers are separate deployments,
/// so every URL and credential is *supposed* to differ - only business constants are expected to
/// match, and a mismatch there is a real finding.
/// </summary>
public sealed class Classifier
{
    private sealed record Rule(
        string Match,
        string Pattern,
        ValueClass Class,
        Regex? ValueRegex,
        IReadOnlyList<string> Except);

    private readonly List<Rule> _rules;
    private readonly ValueClass _default;

    private Classifier(List<Rule> rules, ValueClass defaultClass)
    {
        _rules = rules;
        _default = defaultClass;
    }

    public static Classifier Load(string? file = null)
    {
        file ??= AppPaths.ConfigFile("classify.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file), OrdinalJsonWriter.DocumentOptions);

        var root = document.RootElement;
        var defaultClass = root.TryGetProperty("default", out var d)
            ? Parse(d.GetString())
            : ValueClass.Business;

        var rules = new List<Rule>();
        if (root.TryGetProperty("rules", out var list))
        {
            foreach (var element in list.EnumerateArray())
            {
                var match = element.GetProperty("match").GetString() ?? "path";
                var pattern = element.GetProperty("pattern").GetString() ?? string.Empty;
                var valueClass = Parse(element.TryGetProperty("class", out var c) ? c.GetString() : null);

                Regex? valueRegex = null;
                if (match.Equals("value", StringComparison.OrdinalIgnoreCase))
                {
                    valueRegex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
                }

                // Vetoes let a broad rule stay broad. Precedence is by class - secret always wins -
                // so a narrower business rule could never override a secret match on its own.
                var except = new List<string>();
                if (element.TryGetProperty("except", out var exceptions))
                {
                    except.AddRange(exceptions.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                }

                rules.Add(new Rule(match, pattern, valueClass, valueRegex, except));
            }
        }

        return new Classifier(rules, defaultClass);
    }

    /// <summary>
    /// Precedence is secret &gt; infra &gt; business regardless of rule order, so a key that looks like
    /// both a URL and a password is treated as a password. Within one class the most specific
    /// pattern wins.
    /// </summary>
    public ValueClass Classify(string path, string? value)
    {
        ValueClass? best = null;
        var bestSpecificity = -1;

        foreach (var rule in _rules)
        {
            var matched = rule.Match.Equals("value", StringComparison.OrdinalIgnoreCase)
                ? value is not null && rule.ValueRegex!.IsMatch(value)
                : PathGlob.IsMatch(path, rule.Pattern);

            if (!matched)
            {
                continue;
            }

            if (rule.Except.Any(pattern => PathGlob.IsMatch(path, pattern)))
            {
                continue;
            }

            var specificity = PathGlob.Specificity(rule.Pattern);
            if (best is null || Rank(rule.Class) > Rank(best.Value) ||
                (rule.Class == best.Value && specificity > bestSpecificity))
            {
                best = rule.Class;
                bestSpecificity = specificity;
            }
        }

        return best ?? _default;
    }

    private static int Rank(ValueClass valueClass) => valueClass switch
    {
        ValueClass.Secret => 3,
        ValueClass.Infra => 2,
        _ => 1,
    };

    private static ValueClass Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "secret" => ValueClass.Secret,
        "infra" => ValueClass.Infra,
        _ => ValueClass.Business,
    };
}
