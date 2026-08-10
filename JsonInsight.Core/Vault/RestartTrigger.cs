using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonInsight.Vault;

/// <summary>What a restart call did. Never carries a credential.</summary>
public sealed record RestartResult(bool Ok, int? StatusCode, string Message, string? Body)
{
    /// <summary>
    /// A restart is the one call here whose failure mode includes "it worked and then the connection
    /// died", because the thing being restarted is the thing answering. True when the request went out
    /// and the answer never came, which is a likely success rather than a failure.
    /// </summary>
    public bool Inconclusive { get; init; }
}

/// <summary>
/// Calls the endpoint that restarts whatever reads a source's configuration.
///
/// <para>
/// Uploading to Vault changes nothing on its own: the secret is materialised into a configuration
/// source that can never reload, and nearly every consumer binds <c>IOptions&lt;T&gt;</c> once. A
/// restart is the only thing that re-reads it. The endpoint already exists — this is a client for it,
/// not a mechanism this app implements.
/// </para>
///
/// <para>
/// Built like <see cref="VaultClient"/>: the request body and the response envelope are produced by
/// static methods that never open a socket, so the fences can be tested without one. The only method
/// that reaches the network is <see cref="CallAsync"/>.
/// </para>
/// </summary>
public sealed class RestartTrigger : IDisposable
{
    /// <summary>
    /// Deliberately short. A restart endpoint that drains for 15 seconds answers in about that, and
    /// the failure worth reporting quickly is "nothing is listening" rather than "it is still
    /// thinking".
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;

    public RestartTrigger(VaultConnection connection)
    {
        var handler = new HttpClientHandler();

        if (connection.RestartAllowInsecureTls)
        {
            // Opt-in per source, and separate from the Vault row's own TLS setting: this is an
            // application API on a different host with a different certificate.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    /// <summary>
    /// Why this restart cannot be attempted at all, or null when it can. Checked before anything is
    /// sent, so a misconfigured row says what is missing rather than failing at the socket.
    /// </summary>
    public static string? Blocked(VaultConnection connection, string token)
    {
        if (string.IsNullOrWhiteSpace(connection.RestartUrl))
        {
            return "No restart endpoint is configured for this source. Press Restart config to set one.";
        }

        if (!Uri.TryCreate(connection.RestartUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{connection.RestartUrl}' is not an absolute http or https URL.";
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return "A bearer token is needed. It is never saved, so it is typed for each call.";
        }

        return BodyProblem(connection.RestartBody);
    }

    /// <summary>
    /// Why the configured body is unusable, or null when it is fine — including when it is empty,
    /// which is the normal case and means no body is sent.
    /// </summary>
    public static string? BodyProblem(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            JsonNode.Parse(body);
            return null;
        }
        catch (JsonException ex)
        {
            return $"The request body is not valid JSON: {ex.Message}";
        }
    }

    /// <summary>
    /// The request, built and inspectable without sending it. POST, bearer auth, and a JSON body only
    /// when one was configured — an empty body is no body rather than <c>""</c>, which some endpoints
    /// reject as a malformed payload.
    /// </summary>
    public static HttpRequestMessage BuildRequest(VaultConnection connection, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, connection.RestartUrl.Trim());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        if (!string.IsNullOrWhiteSpace(connection.RestartBody))
        {
            request.Content = new StringContent(connection.RestartBody.Trim(), Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// Reads the answer. Any 2xx is a success; everything else is reported with its status, because
    /// "401" and "503" send you to different places and a generic failure sends you to neither.
    /// </summary>
    public static RestartResult Read(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        var trimmed = string.IsNullOrWhiteSpace(body) ? null : body.Trim();

        if (code is >= 200 and < 300)
        {
            return new RestartResult(true, code, $"Restart accepted — HTTP {code}.", trimmed);
        }

        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " The bearer token was rejected.",
            HttpStatusCode.Forbidden => " The token is valid but lacks the restart permission.",
            HttpStatusCode.NotFound => " Nothing is served at that URL — check the path and any query string.",
            _ => string.Empty,
        };

        return new RestartResult(false, code, $"Restart refused — HTTP {code}.{hint}", trimmed);
    }

    /// <summary>
    /// Sends it. The token is a parameter rather than a property of the connection because it is
    /// never stored: it arrives from the dialog and leaves with the request.
    /// </summary>
    public async Task<RestartResult> CallAsync(
        VaultConnection connection,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (Blocked(connection, token) is { } blocked)
        {
            return new RestartResult(false, null, blocked, null);
        }

        using var request = BuildRequest(connection, token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Read(response.StatusCode, body);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The thing being restarted is the thing answering, so a dropped or timed-out response is
            // at least as likely to mean it worked as to mean it did not. Saying "failed" would send
            // somebody to press it again against an environment already on its way down.
            return new RestartResult(
                false,
                null,
                $"No answer within {Timeout.TotalSeconds:0}s. A restart often drops the connection that " +
                "would have carried the reply, so this may well have worked — check the environment " +
                "rather than pressing it again.",
                null)
            {
                Inconclusive = true,
            };
        }
        catch (HttpRequestException ex)
        {
            return new RestartResult(false, null, $"Could not reach it: {ex.Message}", null);
        }
    }

    public void Dispose() => _http.Dispose();
}
