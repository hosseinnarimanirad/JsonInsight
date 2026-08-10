using Bunit;
using Microsoft.Extensions.DependencyInjection;
using JsonInsight.ViewModels;
using WebJsonInsight.Components.Tabs;

namespace WebJsonInsight.Tests;

/// <summary>
/// The Logs tab, and the accumulating record behind it that replaced the dismissable banner.
/// </summary>
public sealed class LogTests : TestContext
{
    [Fact]
    public void Entries_are_newest_first()
    {
        var log = new LogVm();

        log.Info("first");
        log.Warn("second");
        log.Error("third");

        Assert.Equal(["third", "second", "first"], log.Entries.Select(e => e.Text));
    }

    /// <summary>
    /// Info is activity; the badge is for things worth acting on. Counting every status line would
    /// leave the tab permanently wearing a number, which is the same as wearing none.
    /// </summary>
    [Fact]
    public void The_badge_counts_only_warnings_and_errors()
    {
        var log = new LogVm();

        log.Info("read every source");
        Assert.False(log.HasProblems);
        Assert.Equal(string.Empty, log.Badge);

        log.Warn("dev: 26 arrays have no declared strategy");
        log.Error("stage could not be read");

        Assert.True(log.HasProblems);
        Assert.Equal("2", log.Badge);
    }

    [Fact]
    public void The_badge_stops_counting_past_ninety_nine()
    {
        var log = new LogVm();

        for (var i = 0; i < 120; i++)
        {
            log.Warn($"warning {i}");
        }

        Assert.Equal("99+", log.Badge);
    }

    /// <summary>
    /// A session that pulls all day must not grow this without bound. The oldest go, not the newest:
    /// what just happened is the reason the tab gets opened.
    /// </summary>
    [Fact]
    public void The_log_is_capped_and_drops_the_oldest()
    {
        var log = new LogVm();

        for (var i = 0; i < LogVm.Capacity + 50; i++)
        {
            log.Info($"line {i}");
        }

        Assert.Equal(LogVm.Capacity, log.Entries.Count);
        Assert.Equal($"line {LogVm.Capacity + 49}", log.Entries[0].Text);
        Assert.DoesNotContain(log.Entries, e => e.Text == "line 0");
    }

    [Fact]
    public void Blank_text_is_not_logged()
    {
        var log = new LogVm();

        log.Info(string.Empty);
        log.Info("   ");

        Assert.True(log.IsEmpty);
    }

    [Fact]
    public void Clear_empties_it()
    {
        var log = new LogVm();
        log.Warn("something");

        log.ClearCommand.Execute(null);

        Assert.True(log.IsEmpty);
        Assert.False(log.HasProblems);
    }

    [Fact]
    public void The_tab_renders_every_entry_with_its_level()
    {
        var log = new LogVm();
        log.Info("read every source");
        log.Error("stage could not be read");

        var page = RenderComponent<LogsTab>(p => p.Add(c => c.Vm, log));

        Assert.Equal(2, page.FindAll(".log-row").Count);
        Assert.Single(page.FindAll(".log-row.log-error"));
        Assert.Contains("stage could not be read", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clear_button_is_disabled_while_there_is_nothing_to_clear()
    {
        var log = new LogVm();
        var page = RenderComponent<LogsTab>(p => p.Add(c => c.Vm, log));

        Assert.True(page.Find(".card-head button").HasAttribute("disabled"));

        log.Warn("something");
        page.Render();

        Assert.False(page.Find(".card-head button").HasAttribute("disabled"));
    }

    /// <summary>
    /// The status bar holds one line and is overwritten by the next thing that happens. "What did it
    /// say before I pressed that?" is the question this tab exists to answer.
    /// </summary>
    [Fact]
    public void Status_lines_reach_the_log()
    {
        var main = Fixtures.NewMain();

        main.Status = "Pulled 3 tiers.";

        Assert.Contains(main.Log.Entries, e => e.Text == "Pulled 3 tiers.");
    }
}
