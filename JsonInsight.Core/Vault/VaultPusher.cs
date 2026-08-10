using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Sources;

namespace JsonInsight.Vault;

/// <summary>
/// Everything a push needs to know, worked out before anything is sent.
///
/// <para>
/// It carries the payload as text rather than as a tree, and the live version it was checked
/// against, so what the dialog previewed and what reaches Vault cannot drift apart between the two.
/// </para>
/// </summary>
public sealed record PushPlan(
    string TierId,
    string Address,
    string SecretPath,
    int LiveVersion,
    DateTimeOffset? LiveCreated,
    string LiveText,
    string PayloadText,
    int? BaseVersion,
    string What,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Which kind of source this plan targets. Defaults to <see cref="SourceKind.Vault"/> — every
    /// <see cref="PushPlan"/> built before this field existed was one — so <see cref="VaultPusher"/>
    /// need not be touched to keep producing them.
    ///
    /// <para>
    /// <see cref="LiveVersion"/>/<see cref="BaseVersion"/> mean a KV version number only for
    /// <see cref="SourceKind.Vault"/>. A <see cref="SourceKind.LocalFile"/> plan repurposes them as a
    /// 0/1 "did the file on disk change since this was loaded" flag — there is no version number to
    /// show, which is exactly why <see cref="Destination"/> below branches on this rather than
    /// printing a "v00 → v01" that would lie about a file having versions it does not.
    /// </para>
    /// </summary>
    public SourceKind Kind { get; init; } = SourceKind.Vault;

    /// <summary>True when the source already holds exactly this, so there is nothing worth writing.</summary>
    public bool Identical => string.Equals(LiveText, PayloadText, StringComparison.Ordinal);

    /// <summary>
    /// Whether the version (or, for a local file, the content) this was built from still matches what
    /// the source holds now.
    ///
    /// <para>
    /// False means something changed the source after this copy was read. The write would still
    /// succeed — the concurrency check is against the source's current state, which is current by
    /// construction — so this is not a gate but a statement: what you are about to replace is not
    /// what you started from, and the diff beside it is where that shows up.
    /// </para>
    /// </summary>
    public bool BaseMatchesLive => BaseVersion is null || BaseVersion == LiveVersion;

    /// <summary>The one line naming where this goes, shown before the confirmation is typed.</summary>
    public string Destination => Kind switch
    {
        SourceKind.LocalFile => $"{SecretPath}" +
            (LiveCreated is { } modified ? $"  ·  modified {modified.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty) +
            "  ·  local file, no version history",
        _ => $"{Address}  ·  {SecretPath}  ·  v{LiveVersion:00} → v{LiveVersion + 1:00}",
    };
}

public sealed record PushPreflight(PushPlan? Plan, string? Problem)
{
    public bool Ok => Plan is not null;

    public static PushPreflight Failed(string problem) => new(null, problem);
}

public sealed record PushResult(
    bool Succeeded,
    int? Version,
    string? Error,
    IReadOnlyList<string> Notes);

/// <summary>
/// The only code in the app that writes anything anywhere.
///
/// <para>
/// Every mutation this app performs — a promoted subtree, a batch of key edits, a retyped document —
/// ends here, as a whole document sent to a Vault secret. There is no second write path and no local
/// one: the fences below are the fences, and a mutation that reimplemented them is how one of them
/// would eventually be dropped. In order of how much damage skipping one would do:
/// </para>
///
/// <list type="bullet">
/// <item>A tier marked <c>"writable": false</c> is refused.</item>
/// <item>The payload is re-parsed and re-flattened before it leaves, and refused if it does not hold
/// precisely the keys it is supposed to — a serializer that dropped or invented a key is caught here
/// rather than discovered in tomorrow's diff.</item>
/// <item>What Vault holds is read immediately beforehand, so the comparison being confirmed is
/// against the current version rather than against whatever was on screen.</item>
/// <item>The write carries that version as a check-and-set, so a secret that moved in between is
/// refused by Vault rather than clobbered.</item>
/// <item>The result is read back and compared, because "the POST returned 200" and "Vault holds what
/// I sent" are different claims.</item>
/// </list>
///
/// <para>
/// The typed tier name is the UI's gate rather than this class's, matching every other write this
/// app has ever performed.
/// </para>
/// </summary>
public sealed class VaultPusher
{
    private readonly Flattener _flattener;

    public VaultPusher(Flattener flattener)
    {
        _flattener = flattener;
    }

    /// <summary>
    /// Why this tier could not be pushed at all, or null when it could be attempted.
    ///
    /// <para>
    /// Separate from the preflight because it answers without a network: it is what decides whether
    /// the button is offered, and a button that has to contact a server before it can grey itself
    /// out is one that greys itself out too late to help.
    /// </para>
    /// </summary>
    public static string? Blocked(TierDocument? tier, VaultSettings settings)
    {
        if (tier is null)
        {
            return "No tier selected.";
        }

        // Only a file browsed on the Compare files tab reaches here read-only; every configured
        // source is writable. Kept as a fence rather than an assertion because the push path must
        // refuse a document it was handed, not trust that it was handed the right one.
        if (!tier.Writable)
        {
            return $"{tier.Id} is read-only, so this app never writes it.";
        }

        if (string.IsNullOrWhiteSpace(tier.Definition.VaultPath))
        {
            return $"{tier.Id} names no vaultPath, so there is no secret to push it to.";
        }

        var missing = settings.Unreachable(tier.Id);
        return missing.Count > 0
            ? $"{tier.Id} has no usable Vault connection — missing {string.Join(", ", missing)}. Fill it in on the Sources tab."
            : null;
    }

    /// <summary>
    /// Reads what Vault holds now and works out what would replace it. Sends nothing.
    /// </summary>
    /// <param name="updated">
    /// The document to push — the tier's own tree for an unedited push, or the tree a promote, a
    /// change set or the editor produced. It is always a whole document: this app has one unit of
    /// change against Vault, and it is the secret.
    /// </param>
    /// <param name="what">One phrase naming the change, for the dialog and the report.</param>
    public async Task<PushPreflight> PreflightAsync(
        TierDocument tier,
        JsonNode updated,
        string what,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Blocked(tier, settings) is { } blocked)
        {
            return PushPreflight.Failed(blocked);
        }

        var (payload, payloadProblem) = Payload(tier.Id, updated);
        if (payload is null)
        {
            return PushPreflight.Failed(payloadProblem!);
        }

        var secretPath = tier.Definition.VaultPath!;
        var connection = settings.Resolve(tier.Id);

        VaultReadResult live;
        try
        {
            using var client = new VaultClient(connection);
            live = await client.ReadAsync(secretPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PushPreflight.Failed(
                $"Reading {secretPath} timed out after {VaultClient.Timeout.TotalSeconds:0}s. Nothing was sent.");
        }
        catch (Exception ex)
        {
            return PushPreflight.Failed(
                $"Could not read {secretPath} to compare against: {ex.Message} Nothing was sent.");
        }

        string liveText;
        try
        {
            liveText = OrdinalJsonWriter.SerializeToText(OrdinalJsonWriter.Parse(live.Json));
        }
        catch (Exception ex)
        {
            return PushPreflight.Failed(
                $"Vault holds something at {secretPath} that will not parse as JSON: {ex.Message}");
        }

        var warnings = new List<string>();

        if (tier.VaultVersion is { } baseVersion && baseVersion != live.Version)
        {
            warnings.Add(
                $"This was built from v{baseVersion:00}, and Vault is now at v{live.Version:00} — somebody " +
                $"uploaded in between. What the push replaces is v{live.Version:00}, which is what the diff shows.");
        }

        return new PushPreflight(
            new PushPlan(
                tier.Id,
                live.Address,
                secretPath,
                live.Version,
                live.CreatedTime,
                liveText,
                payload,
                tier.VaultVersion,
                what,
                warnings),
            null);
    }

    /// <summary>
    /// Sends the plan's payload, then reads it back to confirm Vault holds it.
    ///
    /// <para>
    /// The plan is passed in rather than recomputed so that what is uploaded is what was previewed
    /// and confirmed. The check-and-set version comes from it too, which is why a secret that moved
    /// between the preview and the press is refused by Vault rather than quietly overwritten.
    /// </para>
    /// </summary>
    public async Task<PushResult> PushAsync(
        TierDocument tier,
        PushPlan plan,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Blocked(tier, settings) is { } blocked)
        {
            return new PushResult(false, null, blocked, []);
        }

        if (plan.Identical)
        {
            return new PushResult(false, null,
                $"Vault already holds exactly this at v{plan.LiveVersion:00}. Nothing was sent — a version " +
                "that changes nothing is noise in the history.", []);
        }

        var connection = settings.Resolve(tier.Id);
        var notes = new List<string>();

        using var client = new VaultClient(connection);

        VaultWriteResult written;
        try
        {
            written = await client
                .WriteAsync(plan.SecretPath, plan.PayloadText, plan.LiveVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PushResult(false, null,
                $"Timed out after {VaultClient.Timeout.TotalSeconds:0}s. Read Vault again before retrying — " +
                "a timeout does not say whether the write landed.", notes);
        }
        catch (Exception ex)
        {
            return new PushResult(false, null, ex.Message, notes);
        }

        notes.Add($"Vault {plan.SecretPath} is now version {written.Version}.");

        // Everything below this line runs after the secret has already changed, so none of it may
        // turn the push into a failure. A read-back that cannot be performed is worth reporting and
        // is not evidence that the write did not land — reporting it as a failed push would send
        // somebody to push again over a version that is already theirs.
        try
        {
            var readBack = await client.ReadAsync(plan.SecretPath, cancellationToken).ConfigureAwait(false);
            notes.Add(Verify(readBack, plan.PayloadText));
        }
        catch (Exception ex)
        {
            notes.Add($"Pushed, but it could not be read back to verify: {ex.Message}");
        }

        return new PushResult(true, written.Version, null, notes);
    }

    /// <summary>
    /// The exact text that would be sent, or why it will not be.
    ///
    /// <para>
    /// Canonical rather than raw, so the diff against Vault shows content instead of formatting —
    /// the same decision the Text diff tab and the Vault loader already make. The re-parse is what
    /// carries the weight: nothing goes out unless what comes back off the text holds exactly the
    /// keys the tree does, which catches a serializer that dropped or invented one.
    /// </para>
    /// </summary>
    public (string? Text, string? Problem) Payload(string tierId, JsonNode updated) =>
        PayloadValidator.Validate(_flattener, tierId, updated);

    /// <summary>Compares what Vault handed back with what was sent, and says which it is.</summary>
    private static string Verify(VaultReadResult readBack, string sent)
    {
        string canonical;
        try
        {
            canonical = OrdinalJsonWriter.SerializeToText(OrdinalJsonWriter.Parse(readBack.Json));
        }
        catch (Exception ex)
        {
            return $"Read back v{readBack.Version}, but it would not parse: {ex.Message}";
        }

        return string.Equals(canonical, sent, StringComparison.Ordinal)
            ? $"Read back and verified — v{readBack.Version} matches what was sent, key for key."
            : $"WARNING: v{readBack.Version} was read back and does not match what was sent. " +
              "Pull and compare before trusting it.";
    }
}
