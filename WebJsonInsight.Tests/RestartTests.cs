using System.Net;
using System.Text.Json;
using Bunit;
using JsonInsight.Sources;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Dialogs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// The restart trigger: a client for the endpoint that makes a pushed configuration take effect.
///
/// <para>
/// Nothing here opens a socket. <see cref="RestartTrigger"/> builds its request and reads its
/// envelope through static methods for exactly that reason, and <see cref="RestartVm"/> takes the
/// same <c>live</c> switch MainVm and PushVm take for their reads. A test that quietly restarted a
/// production environment would be the worst one in this repository.
/// </para>
/// </summary>
public sealed class RestartTests : TestContext
{
    public RestartTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static VaultConnection Configured(string? body = null) => new()
    {
        SecretPath = "kv/app/stage",
        Address = "https://vault.test",
        RestartUrl = "https://api.test/api/v1/dev/admin/restart?drainSeconds=15",
        RestartBody = body ?? string.Empty,
    };

    // ------------------------------------------------------------- the fences

    [Fact]
    public void An_unconfigured_source_is_blocked_with_the_reason()
    {
        var blocked = RestartTrigger.Blocked(new VaultConnection(), "token");

        Assert.NotNull(blocked);
        Assert.Contains("Restart config", blocked!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/api/v1/restart")]
    [InlineData("ftp://api.test/restart")]
    public void A_url_that_is_not_absolute_http_is_refused(string url)
    {
        var connection = Configured();
        connection.RestartUrl = url;

        Assert.Contains("absolute http", RestartTrigger.Blocked(connection, "token")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// No token, no call. This is the confirmation step: the token is never stored, so it cannot be
    /// supplied by anything except somebody deliberately typing it.
    /// </summary>
    [Fact]
    public void A_missing_token_is_refused_and_says_why_it_is_not_remembered()
    {
        var blocked = RestartTrigger.Blocked(Configured(), "   ");

        Assert.NotNull(blocked);
        Assert.Contains("never saved", blocked!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_is_not_json_is_refused_before_anything_is_sent()
    {
        var blocked = RestartTrigger.Blocked(Configured("{ not json"), "token");

        Assert.NotNull(blocked);
        Assert.Contains("not valid JSON", blocked!, StringComparison.Ordinal);
    }

    /// <summary>Empty is the normal case and means no body, not an empty one.</summary>
    [Fact]
    public void An_empty_body_is_fine_and_sends_nothing()
    {
        Assert.Null(RestartTrigger.BodyProblem(string.Empty));

        using var request = RestartTrigger.BuildRequest(Configured(), "token");
        Assert.Null(request.Content);
    }

    // ------------------------------------------------------------ the request

    [Fact]
    public void The_request_is_a_post_with_bearer_auth_and_the_configured_body()
    {
        using var request = RestartTrigger.BuildRequest(Configured("""{"drain":5}"""), "  abc123  ");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.test/api/v1/dev/admin/restart?drainSeconds=15", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("abc123", request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    // ----------------------------------------------------------- the response

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public void Any_2xx_is_a_success(HttpStatusCode status) =>
        Assert.True(RestartTrigger.Read(status, null).Ok);

    /// <summary>
    /// The status is reported rather than folded into a generic failure: 401 and 503 send you to
    /// different places, and a generic failure sends you to neither.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "token was rejected")]
    [InlineData(HttpStatusCode.Forbidden, "lacks the restart permission")]
    [InlineData(HttpStatusCode.NotFound, "Nothing is served at that URL")]
    public void A_failure_carries_its_status_and_a_hint(HttpStatusCode status, string hint)
    {
        var result = RestartTrigger.Read(status, null);

        Assert.False(result.Ok);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Contains(hint, result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the row

    /// <summary>Saved with the rest of the row. The token is not part of what a row hands over.</summary>
    [Fact]
    public void The_endpoint_round_trips_through_the_row_and_the_token_never_does()
    {
        var main = Fixtures.NewMain();
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        row.RestartUrl = "https://api.test/restart";
        row.RestartBody = """{"drain":5}""";
        row.RestartAllowInsecureTls = true;

        var connection = row.ToConnection();

        Assert.Equal("https://api.test/restart", connection.RestartUrl);
        Assert.Equal("""{"drain":5}""", connection.RestartBody);
        Assert.True(connection.RestartAllowInsecureTls);
        Assert.Empty(connection.RestartToken);
    }

    /// <summary>
    /// Structurally unwritable, not merely unwritten: the serializer that produces appsettings.json
    /// cannot emit the token even if this class changes later. Same fence VaultConnection.Token has.
    /// </summary>
    [Fact]
    public void The_token_cannot_reach_appsettings_json()
    {
        var connection = Configured();
        connection.RestartToken = "super-secret-admin-token";
        connection.Token = "hvs.vault-token";

        var json = JsonSerializer.Serialize(connection);

        Assert.DoesNotContain("super-secret-admin-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hvs.vault-token", json, StringComparison.Ordinal);
        Assert.Contains("RestartUrl", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row with only a restart endpoint is not empty. It used to be, which would have dropped the
    /// endpoint on the next save for any environment whose source was not filled in yet.
    /// </summary>
    [Fact]
    public void A_row_with_only_a_restart_endpoint_is_still_saved()
    {
        var main = Fixtures.NewMain();
        var vm = main.Vault!;
        var row = vm.Connections.First(c => c.Environment == SourceEnvironment.TestQa);

        row.SecretPath = string.Empty;
        row.Token = string.Empty;
        Assert.True(row.IsEmpty);

        row.RestartUrl = "https://api.test/restart";
        Assert.False(row.IsEmpty);

        Assert.Contains(row.TierId, vm.BuildSettings().Connections.Keys);
    }

    // ------------------------------------------------------------ the screens

    [Fact]
    public void Calling_with_nothing_configured_is_refused_before_the_dialog_opens()
    {
        var main = Fixtures.NewMain();
        var dialogs = new DialogService(main);
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);
        row.RestartUrl = string.Empty;

        dialogs.OpenRestart(row);

        Assert.Null(dialogs.Restart);
        Assert.Contains("No restart endpoint is configured", dialogs.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>The call screen names the environment and the URL, and will not fire without a token.</summary>
    [Fact]
    public void The_call_screen_names_the_destination_and_gates_on_the_token()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);
        row.RestartUrl = "https://api.test/api/v1/dev/admin/restart?drainSeconds=15";

        var vm = new RestartVm(row, live: false);
        var page = RenderComponent<RestartCallDialog>(p => p
            .Add(c => c.Vm, vm)
            .Add(c => c.OnClose, () => { }));

        Assert.Contains("api.test/api/v1/dev/admin/restart", page.Markup, StringComparison.Ordinal);
        Assert.Contains(row.Label, page.Markup, StringComparison.Ordinal);
        Assert.False(vm.CanCall);

        var button = page.FindAll("button").First(b => b.TextContent.Contains("Call restart", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));

        vm.Token = "admin-token";
        Assert.True(vm.CanCall);
    }

    /// <summary>The token field is a password field, so it is not left legible on screen.</summary>
    [Fact]
    public void The_token_is_not_rendered_in_clear()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);
        row.RestartUrl = "https://api.test/restart";

        var page = RenderComponent<RestartCallDialog>(p => p
            .Add(c => c.Vm, new RestartVm(row, live: false))
            .Add(c => c.OnClose, () => { }));

        Assert.Equal("password", page.Find(".restart-call input[type=password]").GetAttribute("type"));
    }

    /// <summary>
    /// Only a Vault row has an overflow menu at all. Everything left in it is a Vault concern — the
    /// TLS toggle and the restart pair — since Test came out onto the row itself; a local-file row
    /// has no certificate to trust and nothing running behind it to restart, so it would be opening
    /// an empty box.
    /// </summary>
    [Fact]
    public void Only_a_vault_row_has_a_menu_and_it_holds_the_vault_items()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));

        var page = RenderComponent<WebJsonInsight.Components.Tabs.SourcesTab>(p => p.Add(c => c.Vm, main.Vault));

        var vaultRows = main.Vault!.Connections.Count(c => c.Kind == SourceKind.Vault);
        Assert.Equal(vaultRows, page.FindAll(".rowmenu > button").Count);

        // Closed until asked for: the row is the wide part of this tab, and these are occasional.
        Assert.DoesNotContain("Call restart", page.Markup, StringComparison.Ordinal);

        page.FindAll(".rowmenu > button")[0].Click();

        var menu = page.Find(".rowmenu-pop");
        Assert.Contains("Insecure TLS", menu.TextContent, StringComparison.Ordinal);
        Assert.Contains("Call restart", menu.TextContent, StringComparison.Ordinal);

        foreach (var row in main.Vault.Connections)
        {
            row.Kind = SourceKind.LocalFile;
        }

        page.Render();
        Assert.Empty(page.FindAll(".rowmenu"));
    }

    /// <summary>
    /// Opening the menu is what puts the three occasional controls on screen. All three live
    /// together because none of them is about where the row reads from.
    /// </summary>
    [Fact]
    public void Opening_the_menu_reveals_tls_restart_config_and_call_restart()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));

        var page = RenderComponent<WebJsonInsight.Components.Tabs.SourcesTab>(p => p.Add(c => c.Vm, main.Vault));

        page.FindAll(".rowmenu > button")[0].Click();

        var menu = page.Find(".rowmenu-pop");
        Assert.Contains("Insecure TLS", menu.TextContent, StringComparison.Ordinal);
        Assert.Contains("Restart config", menu.TextContent, StringComparison.Ordinal);
        Assert.Contains("Call restart", menu.TextContent, StringComparison.Ordinal);

        // One menu at a time: opening a second row's must close the first, or a list of environments
        // ends up with several open at once and no way to tell which one a click will act on.
        page.FindAll(".rowmenu > button")[1].Click();
        Assert.Single(page.FindAll(".rowmenu-pop"));
    }

    /// <summary>
    /// Call restart is disabled until an endpoint exists. Enabled-but-failing would be a button that
    /// looks like it restarts production and does not say why it did not.
    /// </summary>
    [Fact]
    public void Call_restart_is_disabled_until_an_endpoint_is_configured()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));

        var page = RenderComponent<WebJsonInsight.Components.Tabs.SourcesTab>(p => p.Add(c => c.Vm, main.Vault));
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        page.FindAll(".rowmenu > button")[0].Click();
        var call = page.FindAll(".rowmenu-item")
            .Single(i => i.TextContent.Contains("Call restart", StringComparison.Ordinal));
        Assert.True(call.HasAttribute("disabled"));

        row.RestartUrl = "https://api.test/restart";
        page.Render();

        call = page.FindAll(".rowmenu-item")
            .Single(i => i.TextContent.Contains("Call restart", StringComparison.Ordinal));
        Assert.False(call.HasAttribute("disabled"));
    }

    /// <summary>The config screen edits the row directly, so Save settings picks it up with the rest.</summary>
    [Fact]
    public void The_config_screen_writes_straight_onto_the_row()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        var page = RenderComponent<RestartConfigDialog>(p => p
            .Add(c => c.Row, row)
            .Add(c => c.OnClose, () => { }));

        page.Find(".restart-config input[type=text]").Input("https://api.test/restart");

        Assert.Equal("https://api.test/restart", row.RestartUrl);
        Assert.True(row.HasRestart);
    }

    /// <summary>A body that does not parse is reported as it is typed, not at the moment of restarting.</summary>
    [Fact]
    public void The_config_screen_checks_the_body_as_it_is_typed()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        var page = RenderComponent<RestartConfigDialog>(p => p
            .Add(c => c.Row, row)
            .Add(c => c.OnClose, () => { }));

        page.Find(".restart-body").Input("{ not json");

        Assert.Contains("not valid JSON", page.Markup, StringComparison.Ordinal);
    }
}
