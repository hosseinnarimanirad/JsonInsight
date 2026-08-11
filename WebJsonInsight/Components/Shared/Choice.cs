using Microsoft.AspNetCore.Components;

namespace WebJsonInsight.Components.Shared;

/// <summary>
/// Reading a <c>&lt;select&gt;</c> back into the enum its options were built from.
///
/// <para>
/// A change event carries a string, so every one of these dropdowns had the same four lines: parse it,
/// and assign only if it parsed. The guard is not ceremony — an unparseable value has to leave the
/// property alone rather than throw or write a default, because the one thing worse than a dropdown
/// that ignores a bad value is one that silently sets the row to the enum's zero.
/// </para>
/// </summary>
public static class Choice
{
    public static void Set<T>(ChangeEventArgs e, Action<T> assign) where T : struct, Enum
    {
        if (Enum.TryParse<T>(e.Value?.ToString(), out var value))
        {
            assign(value);
        }
    }
}
