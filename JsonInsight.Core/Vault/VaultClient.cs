using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace JsonInsight.Vault;

/// <summary>A successful read: the configuration JSON exactly as Vault holds it, plus its metadata.</summary>
public sealed record VaultReadResult(
    string Json,
    int Version,
    DateTimeOffset? CreatedTime,
    string SecretPath,
    string Address);

/// <summary>A successful write: the version Vault created, and where it created it.</summary>
public sealed record VaultWriteResult(
    int Version,
    DateTimeOffset? CreatedTime,
    string SecretPath,
    string Address);

/// <summary>
/// A write Vault refused because the secret had moved since it was read.
///
/// <para>
/// Its own type because it is the one failure that is not a fault: somebody else uploaded between
/// the preflight read and the push, and the answer is to look at the new version rather than to
/// retry. Everything else surfaces as a plain <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
public sealed class VaultVersionConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads an application's configuration out of Vault KV v2.
///
/// <para>
/// This deliberately mirrors the read path in the consuming application's own
/// Vault configuration provider - same <c>v1/{mount}/data/{path}</c> endpoint, same
/// <c>X-Vault-Token</c> header, same envelope - so that what this tab shows is what that application would
/// load.
/// </para>
///
/// <para>
/// There is now one write - <see cref="WriteAsync"/> - and it is deliberately the narrowest one
/// that can exist: it takes a check-and-set version, so Vault itself refuses the write if the
/// secret moved after it was read. Everything that decides <em>whether</em> to call it lives in
/// <see cref="VaultPusher"/>, which carries the same shape of fences the snapshot writer has.
/// </para>
///
/// <para>
/// Flat mode only. The consuming application stores the whole appsettings root directly as <c>data.data</c> with no
/// wrapper key, which is why the snapshot files are root-shaped.
/// </para>
/// </summary>
public sealed class VaultClient : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;
    private readonly string _address;

    public VaultClient(VaultConnection connection)
    {
        _address = connection.Address.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(_address))
        {
            throw new InvalidOperationException("Vault address is not set.");
        }

        if (!Uri.TryCreate(_address + "/", UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Vault address is not a valid absolute URL: {_address}");
        }

        _handler = new HttpClientHandler();
        if (connection.AllowInsecureTls)
        {
            // Opt-in per connection, for an internal Vault behind a self-signed certificate.
            _handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _client = new HttpClient(_handler) { BaseAddress = baseUri, Timeout = Timeout };
        _client.DefaultRequestHeaders.Add("X-Vault-Token", connection.Token.Trim());

        if (!string.IsNullOrWhiteSpace(connection.Namespace))
        {
            _client.DefaultRequestHeaders.Add("X-Vault-Namespace", connection.Namespace.Trim());
        }
    }

    /// <summary>
    /// Splits a combined path like <c>kv/app/stage</c> into the KV v2 mount and the
    /// secret path beneath it. Same rule as the API's loader, so a path that works there works here.
    /// </summary>
    public static (string Mount, string SecretPath) ParseMountAndPath(string fullPath)
    {
        var (mount, path) = ParseMountAndOptionalPath(fullPath);

        if (path.Length == 0)
        {
            throw new InvalidOperationException(
                $"Vault path \"{fullPath}\" must be \"{{mount}}/{{secret-path}}\", e.g. \"kv/app/stage\".");
        }

        return (mount, path);
    }

    /// <summary>
    /// The same split, but accepting a bare mount with nothing under it — which is what listing the
    /// top of a mount is. Reading still uses the strict form: a mount root holds no secret.
    /// </summary>
    public static (string Mount, string SecretPath) ParseMountAndOptionalPath(string fullPath)
    {
        var normalized = fullPath.Trim().Trim('/');

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("A Vault path needs at least a mount, e.g. \"kv\".");
        }

        var slash = normalized.IndexOf('/');
        return slash < 0
            ? (normalized, string.Empty)
            : (normalized[..slash], normalized[(slash + 1)..]);
    }

    /// <summary>
    /// The KV v2 mounts this token can see, via the endpoint Vault's own web UI uses. Returns null
    /// rather than throwing when the token may not read it: that is a normal permission answer for an
    /// application token, and the caller has a perfectly good fallback in the mounts already named by
    /// the configured paths.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListMountsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client
            .GetAsync("v1/sys/internal/ui/mounts", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        // Vault nests the secret engines under "secret"; a narrower response can put them straight
        // in data. Reading both costs nothing and avoids depending on which shape answered.
        var engines = data.TryGetProperty("secret", out var nested) ? nested : data;

        if (engines.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return engines.EnumerateObject()
            .Where(m => m.Value.ValueKind == JsonValueKind.Object && IsKvV2(m.Value))
            .Select(m => m.Name.Trim('/'))
            .ToArray();
    }

    /// <summary>
    /// KV version 2 only. A v1 mount has no <c>metadata</c> endpoint and no versions, so nothing else
    /// in this app can read one — offering it in a picker would be offering a dead end.
    /// </summary>
    private static bool IsKvV2(JsonElement mount) =>
        mount.TryGetProperty("type", out var type) &&
        string.Equals(type.GetString(), "kv", StringComparison.OrdinalIgnoreCase) &&
        mount.TryGetProperty("options", out var options) &&
        options.ValueKind == JsonValueKind.Object &&
        options.TryGetProperty("version", out var version) &&
        version.GetString() == "2";

    /// <summary>Reads the secret and returns its configuration JSON.</summary>
    public async Task<VaultReadResult> ReadAsync(string secretPath, CancellationToken cancellationToken = default)
    {
        var (mount, path) = ParseMountAndPath(secretPath);

        using var response = await _client
            .GetAsync($"v1/{mount}/data/{path}", cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"No secret at \"{secretPath}\" on {_address}. Vault returns 404 both for a path that " +
                "does not exist and for one this token may not read.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Vault read failed at {_address} ({(int)response.StatusCode}): {Describe(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var (data, version, created) = ParseEnvelope(document.RootElement, _address);

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Vault \"data.data\" at {secretPath} is {data.ValueKind}, not a JSON object. The consuming application stores " +
                "the appsettings root directly, so anything else is a differently-shaped secret.");
        }

        return new VaultReadResult(data.GetRawText(), version, created, secretPath, _address);
    }

    /// <summary>
    /// Writes a new version of the secret, and only if it is still at <paramref name="expectedVersion"/>.
    ///
    /// <para>
    /// The check-and-set option is not optional here, and that is the whole design of this method.
    /// A blind write would silently replace whatever somebody else uploaded in the seconds between
    /// the preflight read and the button press; with <c>cas</c> set, Vault rejects exactly that case
    /// and nothing local has to be trusted to notice it. Passing 0 means "create it, and only if it
    /// does not exist yet".
    /// </para>
    ///
    /// <para>
    /// <paramref name="payloadJson"/> is embedded verbatim rather than re-serialized, so what
    /// reaches Vault is the same text the preview showed. It must be a JSON object: The consuming application stores the
    /// configuration root directly as <c>data.data</c>, and a secret of any other shape is one
    /// nothing downstream can read back.
    /// </para>
    /// </summary>
    public async Task<VaultWriteResult> WriteAsync(
        string secretPath,
        string payloadJson,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var (mount, path) = ParseMountAndPath(secretPath);
        var body = BuildWriteBody(payloadJson, expectedVersion, secretPath);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client
            .PostAsync($"v1/{mount}/data/{path}", content, cancellationToken)
            .ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Describe(response.StatusCode, responseBody, secretPath, expectedVersion);
        }

        using var document = JsonDocument.Parse(responseBody);
        var (version, created) = ParseWriteEnvelope(document.RootElement, secretPath);

        return new VaultWriteResult(version, created, secretPath, _address);
    }

    /// <summary>
    /// The request body for a write: the payload embedded verbatim under <c>data</c>, and the
    /// check-and-set version under <c>options</c>.
    ///
    /// <para>
    /// Verbatim rather than re-serialized, so what reaches Vault is the same text the preview
    /// showed - a second serialization is a second chance to differ. The payload is parsed first
    /// all the same, both to prove it is well-formed before it is spliced into a larger document and
    /// to refuse anything that is not an object: The consuming application stores the configuration root directly, so a
    /// secret of any other shape is one nothing downstream can read back.
    /// </para>
    /// </summary>
    internal static string BuildWriteBody(string payloadJson, int expectedVersion, string secretPath)
    {
        using var payload = JsonDocument.Parse(payloadJson, ParseOptions);

        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Refusing to write a {payload.RootElement.ValueKind} to {secretPath}. The consuming application stores the " +
                "configuration root directly, so the payload has to be a JSON object.");
        }

        return $"{{\"options\":{{\"cas\":{expectedVersion}}},\"data\":{payloadJson}}}";
    }

    /// <summary>
    /// Whether a refusal is Vault saying the secret moved, as opposed to anything else going wrong.
    /// It is the one failure that is not a fault, and the only one with an obvious next step.
    /// </summary>
    internal static bool IsVersionConflict(HttpStatusCode status, string body) =>
        status == HttpStatusCode.BadRequest &&
        body.Contains("check-and-set", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the metadata a write answers with.
    ///
    /// <para>
    /// Deliberately not <see cref="ParseEnvelope"/> with a level removed: a write returns the
    /// metadata alone - <c>data.version</c>, no <c>data.data</c> - so the two envelopes are
    /// different shapes rather than one shape read two ways, and a reader that accepted either
    /// would accept a read response as proof that a write landed.
    /// </para>
    /// </summary>
    internal static (int Version, DateTimeOffset? Created) ParseWriteEnvelope(JsonElement root, string secretPath)
    {
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"Vault returned errors writing {secretPath}: {errors.GetRawText()}");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException(
                $"Vault accepted the write to {secretPath} but did not report a version. It may not be a KV v2 mount.");
        }

        DateTimeOffset? created = null;
        if (data.TryGetProperty("created_time", out var createdTime) &&
            createdTime.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(createdTime.GetString(), out var parsed))
        {
            created = parsed;
        }

        return (version.GetInt32(), created);
    }

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 128 };

    /// <summary>
    /// Turns a refused write into the sentence that says what to do about it. The three that have
    /// their own wording are the three that actually happen: the secret moved, the token may read
    /// but not write, and the mount is not there.
    /// </summary>
    private InvalidOperationException Describe(
        HttpStatusCode status,
        string body,
        string secretPath,
        int expectedVersion)
    {
        if (IsVersionConflict(status, body))
        {
            return new VaultVersionConflictException(
                $"Vault refused the write: {secretPath} is no longer at version {expectedVersion}. " +
                "Somebody uploaded a new version after this one was read. Check Vault again to see " +
                "what it holds now, and review the difference before pushing over it.");
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new InvalidOperationException(
                $"This token may not write {secretPath} on {_address} (403). Reading and writing are " +
                $"separate capabilities in Vault - a token that pulls this secret is not necessarily " +
                $"allowed to update it. {Describe(body)}");
        }

        if (status == HttpStatusCode.NotFound)
        {
            return new InvalidOperationException(
                $"No KV v2 mount answering for \"{secretPath}\" on {_address}. Vault returns 404 both for a " +
                "mount that does not exist and for one this token may not see.");
        }

        return new InvalidOperationException(
            $"Vault write failed at {_address} ({(int)status}): {Describe(body)}");
    }

    /// <summary>
    /// Lists what sits directly under a path, as KV v2 reports it: a name ending in <c>/</c> is a
    /// folder, anything else is a secret. A path can legitimately be both, which is exactly the case
    /// here — <c>app-dotnet/stage</c> holds the appsettings root <em>and</em> contains
    /// <c>resources/</c>.
    ///
    /// <para>
    /// An empty list is returned for a path with nothing beneath it, since Vault answers 404 both for
    /// "no children" and "no such folder" and the difference does not change what a caller walking a
    /// tree should do. A token without <c>list</c> capability throws, because that is a permission
    /// answer rather than an empty folder and reporting it as emptiness would be a lie.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAsync(string secretPath, CancellationToken cancellationToken = default)
    {
        // Optional, so the top of a mount can be listed — that is where browsing a whole Vault starts.
        var (mount, path) = ParseMountAndOptionalPath(secretPath);

        using var request = new HttpRequestMessage(new HttpMethod("LIST"), $"v1/{mount}/metadata/{path}");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Vault list failed at {secretPath} ({(int)response.StatusCode}): {Describe(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return keys.EnumerateArray()
            .Where(k => k.ValueKind == JsonValueKind.String)
            .Select(k => k.GetString()!)
            .ToArray();
    }

    /// <summary>Validates the KV v2 envelope and returns data.data plus its version and creation time.</summary>
    internal static (JsonElement Data, int Version, DateTimeOffset? Created) ParseEnvelope(
        JsonElement root,
        string address)
    {
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"Vault returned errors at {address}: {errors.GetRawText()}");
        }

        if (!root.TryGetProperty("data", out var outer) || !outer.TryGetProperty("data", out var inner))
        {
            throw new InvalidOperationException(
                $"Vault response is missing \"data.data\" at {address}. This reader expects a KV v2 mount.");
        }

        if (!outer.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("version", out var version))
        {
            throw new InvalidOperationException(
                $"Vault KV v2 response is missing data.metadata.version at {address}.");
        }

        DateTimeOffset? created = null;
        if (metadata.TryGetProperty("created_time", out var createdTime) &&
            createdTime.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(createdTime.GetString(), out var parsed))
        {
            created = parsed;
        }

        return (inner, version.GetInt32(), created);
    }

    /// <summary>
    /// Vault error bodies are short JSON, but a proxy or WAF in front of it can return a page of HTML.
    /// Truncating keeps that out of the UI.
    /// </summary>
    private static string Describe(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : string.Concat(trimmed.AsSpan(0, 400), "…");
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
