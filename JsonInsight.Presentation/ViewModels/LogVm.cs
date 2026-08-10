using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JsonInsight.ViewModels;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>One thing that happened, when it happened, and how much it matters.</summary>
public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Text)
{
    /// <summary>Local time to the second. A config tool's log is read in the same sitting it is written.</summary>
    public string Time => At.ToLocalTime().ToString("HH:mm:ss");

    public string LevelLabel => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warning => "WARN",
        _ => "INFO",
    };

    /// <summary>The CSS/style suffix both front ends key their colour off.</summary>
    public string LevelKey => Level switch
    {
        LogLevel.Error => "error",
        LogLevel.Warning => "warn",
        _ => "info",
    };
}

/// <summary>
/// Everything the app has said, in one place.
///
/// <para>
/// These used to be a banner above the tabs. A banner is the right shape for one urgent sentence and
/// the wrong one for this: a single undeclared array produces a line per tier, so the thing that was
/// meant to draw the eye instead took the top third of the window and pushed the grid — the reason
/// the app is open — below the fold. Worse, it could only be dismissed, so the choice was between
/// losing the screen and losing the findings.
/// </para>
///
/// <para>
/// A tab keeps both. Nothing is thrown away to get the room back, the entries carry a time so the
/// order of events survives, and <see cref="ClearCommand"/> is a deliberate act rather than the price
/// of seeing the grid.
/// </para>
/// </summary>
public sealed partial class LogVm : ObservableObject
{
    /// <summary>
    /// Oldest entries are dropped past this. A session that pulls all day would otherwise grow this
    /// list without bound, and nobody scrolls to the ten-thousandth line.
    /// </summary>
    public const int Capacity = 1000;

    /// <summary>
    /// Newest first. Held that way rather than reversed by each view: the reason the tab gets opened
    /// is almost always the last thing that happened, and a list-box bound to an
    /// <see cref="ObservableCollection{T}"/> cannot be reversed in the view without giving up the
    /// incremental updates that make appending cheap.
    /// </summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>How many entries are worth acting on — what the tab badge counts.</summary>
    public int ProblemCount => Entries.Count(e => e.Level != LogLevel.Info);

    public bool HasProblems => ProblemCount > 0;

    /// <summary>The badge on the tab, or empty when there is nothing to say.</summary>
    public string Badge => ProblemCount switch
    {
        0 => string.Empty,
        > 99 => "99+",
        var n => n.ToString(),
    };

    public string Summary => Entries.Count switch
    {
        0 => "Nothing logged yet.",
        1 => "1 entry.",
        var n when ProblemCount == 0 => $"{n} entries, none of them problems.",
        var n => $"{n} entries, {ProblemCount} of them problems.",
    };

    public void Info(string text) => Add(LogLevel.Info, text);

    public void Warn(string text) => Add(LogLevel.Warning, text);

    public void Error(string text) => Add(LogLevel.Error, text);

    public void Add(LogLevel level, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Entries.Insert(0, new LogEntry(DateTimeOffset.Now, level, text.Trim()));

        while (Entries.Count > Capacity)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        Changed();
    }

    /// <summary>
    /// Empties the log. Safe to press: what is in here is a record of what already happened, not the
    /// app's state — a reload or a pull says all of it again.
    /// </summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(Summary));
    }
}
