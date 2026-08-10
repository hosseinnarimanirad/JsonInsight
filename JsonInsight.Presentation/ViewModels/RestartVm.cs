using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>
/// Calling one source's restart endpoint.
///
/// <para>
/// Its own screen rather than a button that just fires, for one reason: the bearer token is never
/// saved, so there has to be somewhere to type it — and that requirement is doing double duty as the
/// confirmation. A restart drops connections on a live environment and cannot be taken back, and a
/// stored credential would turn it into a one-click action sitting next to Test on a row of
/// environments that all look alike.
/// </para>
/// </summary>
public sealed partial class RestartVm : ObservableObject
{
    private readonly VaultConnectionVm _row;

    /// <summary>Set false by the test harness, the same way MainVm and PushVm take their reads.</summary>
    private readonly bool _live;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _completed;

    [ObservableProperty]
    private string _problem = string.Empty;

    /// <summary>One line per step, so a call that failed after being accepted says which half went wrong.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public RestartVm(VaultConnectionVm row, bool live = true)
    {
        _row = row;
        _live = live;
        Problem = RestartTrigger.BodyProblem(row.RestartBody) ?? string.Empty;
    }

    public string Environment => _row.Label;

    public string Url => _row.RestartUrl;

    public string Body => _row.RestartBody;

    public bool HasBody => !string.IsNullOrWhiteSpace(_row.RestartBody);

    public bool AllowsInsecureTls => _row.RestartAllowInsecureTls;

    /// <summary>
    /// The token, and nothing else. There is no typed-name confirmation on top of it because the token
    /// already is one: it cannot be remembered by the app, so reaching this button by accident and
    /// completing the call by accident are different events.
    /// </summary>
    public bool CanCall => !Busy && !Completed && Token.Trim().Length > 0 &&
                           RestartTrigger.Blocked(_row.ToConnection(), Token.Trim()) is null;

    partial void OnTokenChanged(string value)
    {
        OnPropertyChanged(nameof(CanCall));
        Problem = string.Empty;
    }

    [RelayCommand]
    public async Task CallAsync()
    {
        if (!CanCall)
        {
            return;
        }

        Busy = true;
        Problem = string.Empty;
        Notes.Clear();
        Notes.Add($"POST {Url}");
        OnPropertyChanged(nameof(CanCall));

        try
        {
            var connection = _row.ToConnection();

            var result = _live
                ? await Call(connection, Token.Trim()).ConfigureAwait(true)
                : new RestartResult(true, 202, "Restart accepted — HTTP 202. (test harness, nothing was sent)", null);

            Notes.Add(result.Message);

            if (!string.IsNullOrWhiteSpace(result.Body))
            {
                Notes.Add(result.Body!);
            }

            // Inconclusive is not a failure. Saying otherwise sends somebody to press it again against
            // an environment already on its way down.
            Completed = result.Ok || result.Inconclusive;
            Problem = result.Ok || result.Inconclusive ? string.Empty : result.Message;

            _row.Status = result.Message;
        }
        catch (Exception ex)
        {
            Problem = $"Restart failed: {ex.Message}";
            Notes.Add(Problem);
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(CanCall));
        }
    }

    /// <summary>
    /// The token is passed rather than read off the connection: <see cref="VaultConnectionVm.ToConnection"/>
    /// deliberately does not carry it, because it is never persisted and must not be round-tripped
    /// through anything that is.
    /// </summary>
    private static async Task<RestartResult> Call(VaultConnection connection, string token)
    {
        using var trigger = new RestartTrigger(connection);
        return await trigger.CallAsync(connection, token).ConfigureAwait(true);
    }
}
