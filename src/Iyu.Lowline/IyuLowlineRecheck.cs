using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lowline;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>Who a signed-in caller is to the engine: the opaque id derived from the salt, or why there is none.</summary>
internal static class IyuLowlineSettlerIdentity
{
    /// <summary>
    /// The pseudonym of <paramref name="user"/>, or <c>null</c> when no salt is configured. <paramref name="accountMissing"/>
    /// is set when a salt is configured but the caller carries no account id to derive it from.
    /// </summary>
    public static string? Resolve(IyuLowlineSettlerOptions settings, ClaimsPrincipal user, out bool accountMissing)
    {
        accountMissing = false;
        if (settings.Salt is not { } salt) return null;

        var account = user.FindFirst(settings.AccountClaimType)?.Value;
        if (string.IsNullOrEmpty(account))
        {
            accountMissing = true;
            return null;
        }

        return Pseudonym(salt, account);
    }

    /// <summary>
    /// The opaque id the engine keeps for <paramref name="accountId"/>: <c>HMAC-SHA256</c> under a key derived from the salt
    /// (HKDF-SHA256, purpose <c>iyu-lowline/settler</c>), lower-case hex. This is stored in every record, so changing it
    /// re-attributes history: the algorithm is pinned by a test.
    /// </summary>
    public static string Pseudonym(string salt, string accountId)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(salt), 32, info: "iyu-lowline/settler"u8.ToArray());
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(accountId)));
    }
}

/// <summary>The number the host draws, for one field of one record, before it looks at the answer — uniform in [0, 1).</summary>
internal interface ILowlineRoll
{
    double Roll(string salt, string form, string space, string recordId, string field);
}

/// <summary>
/// A roll derived from the salt: <c>HMAC-SHA256</c> under a key derived from it for this purpose (HKDF-SHA256, purpose
/// <c>iyu-lowline/recheck</c>), over the form, space, record and field. The same inputs give the same roll, so asking again
/// for the same field of the same record does not redraw it; and a caller who cannot see the salt cannot choose which of
/// their fields are drawn.
/// </summary>
internal sealed class HmacLowlineRoll : ILowlineRoll
{
    public double Roll(string salt, string form, string space, string recordId, string field)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(salt), 32, info: "iyu-lowline/recheck"u8.ToArray());
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('\u001f', form, space, recordId, field)));
        return (BitConverter.ToUInt64(hash, 0) >> 11) / (double)(1UL << 53);
    }
}

/// <summary>One field drawn for a check: with what probability, and the answer that was held back.</summary>
internal sealed record HeldField(
    double Probability, string? Value, string? Origin, string? Fit, string? Layer, double? Promise,
    Dictionary<string, List<string>> Inputs);

/// <summary>What a propose held back for a record, kept until that record is settled.</summary>
internal sealed record PendingCheck(Dictionary<string, HeldField> Held, List<string> Hidden);

/// <summary>
/// Holds some answers back so the values people settle can be checked against them (the engine's "recheck"), and remembers
/// what it held back until the record is settled.
/// </summary>
/// <remarks>
/// <para>
/// Which fields are drawn is the server's: the draw is made from a salt-derived roll when a proposal is answered, the held
/// answers are left out of the response, and the draw is kept in the distributed cache under the record and the caller's
/// settler id. The settle path reads it back and tells the engine — as the host's attestation, and in the draft's account
/// of the field — so a caller can neither claim a check it was not given nor avoid one it was.
/// </para>
/// <para>
/// A draw is <b>sticky</b>: a later proposal for the same record adds to it and never removes from it, so asking again with
/// other values cannot clear a check. The <em>answer</em> that was held back is kept only if the saved record agrees with the
/// values it rested on; if it does not — the proposal was made on values the record never had — the field is still a check,
/// but no answer is written into the record, so a proposal cannot plant a convenient answer and cannot escape the check by
/// disagreeing with it. A settle that fails leaves the draw in place for the retry.
/// </para>
/// <para>
/// This needs a settler id, hence a salt. A settle that finds nothing in the cache (it expired, another node answered the
/// proposal and the cache is not shared, the proposal was never asked for) is a record settled with no check: the engine
/// counts it as a value settled with nothing in view, which is what its precision is counted on anyway, so the effect is
/// fewer checks, never a wrong count.
/// </para>
/// </remarks>
internal sealed class IyuLowlineRecheck(IDistributedCache cache, ILowlineRoll roll, IOptions<IyuLowlineOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Key(string form, string space, string recordId, string settler) =>
        $"iyu-lowline/check/{form}/{space}/{recordId}/{settler}";

    /// <summary>Draws the checks for <paramref name="proposals"/>, keeps them, and returns the proposals with the held answers removed.</summary>
    public async Task<IReadOnlyList<Proposal>> ApplyAsync(
        LowlineScope scope, string recordId, string settler, IReadOnlyDictionary<string, FieldValue> values,
        IReadOnlyList<Proposal> proposals, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var key = Key(scope.Form, scope.Space, recordId, settler);
        var earlier = await PeekAsync(scope, recordId, settler, cancellationToken);
        var held = earlier?.Held ?? [];
        var inputs = values.ToDictionary(v => v.Key, v => v.Value.Values.ToList());
        var recheck = new Recheck(settings.CheckRate);
        foreach (var proposal in proposals)
        {
            var rolled = roll.Roll(settings.Settler.Salt!, scope.Form, scope.Space, recordId, proposal.Field);
            if (recheck.Draw(proposal, rolled) is not { } probability) continue;

            held[proposal.Field] = new HeldField(
                probability,
                proposal.Suggestion?.Value,
                proposal.Suggestion?.Origin,
                proposal.Suggestion?.Fit,
                proposal.Layer?.ToString().ToLowerInvariant(),
                proposal.Promise,
                inputs);
        }

        // Fields drawn earlier stay drawn: asking again with other values must not clear a check.
        var round = proposals.Select(p => p.Field).ToHashSet();
        var hidden = new List<string>(earlier?.Hidden ?? []);
        foreach (var field in Recheck.Hidden(proposals, [.. held.Keys.Where(round.Contains)]))
            if (!hidden.Contains(field)) hidden.Add(field);
        if (held.Count == 0 && hidden.Count == 0) return proposals;

        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(new PendingCheck(held, hidden), Json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = settings.CheckLifetime },
            cancellationToken);

        return [.. proposals.Select(p => hidden.Contains(p.Field)
            ? p with { Suggestion = null, Prefill = false, Layer = null, Basis = [], Promise = null }
            : p)];
    }

    /// <summary>The checks drawn for this record so far, left in place: they are cleared once the record is settled.</summary>
    public async Task<PendingCheck?> PeekAsync(
        LowlineScope scope, string recordId, string settler, CancellationToken cancellationToken)
    {
        var stored = await cache.GetStringAsync(Key(scope.Form, scope.Space, recordId, settler), cancellationToken);
        return stored is null ? null : JsonSerializer.Deserialize<PendingCheck>(stored, Json);
    }

    /// <summary>Forgets the checks drawn for this record: it was settled.</summary>
    public Task ClearAsync(LowlineScope scope, string recordId, string settler, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(scope.Form, scope.Space, recordId, settler), cancellationToken);

    /// <summary>
    /// Whether the saved record agrees with every value a held answer rested on. When it does not, the field is still a
    /// check but its answer is not written into the record.
    /// </summary>
    private static bool Rested(HeldField held, IReadOnlyDictionary<string, FieldValue> saved) =>
        held.Inputs.All(input => saved.TryGetValue(input.Key, out var value) && value.Values.SequenceEqual(input.Value));

    /// <summary>
    /// The draft with the server's account of the held fields written over whatever the caller said of them: for a field
    /// drawn, the probability and the answer held back; for one hidden with it, nothing shown. Throws
    /// <see cref="ArgumentException"/> for a draft that is not a JSON object.
    /// </summary>
    public static string MergeDraft(string? draft, PendingCheck pending, IReadOnlyDictionary<string, FieldValue> saved)
    {
        JsonObject root;
        try
        {
            root = draft is null ? [] : JsonNode.Parse(draft) as JsonObject ?? throw new ArgumentException("the draft is not a JSON object");
        }
        catch (JsonException failure)
        {
            throw new ArgumentException("the draft is not JSON", failure);
        }

        root["opened"] ??= new JsonObject();
        if (root["fields"] is not JsonObject fields)
        {
            fields = [];
            root["fields"] = fields;
        }

        foreach (var field in pending.Hidden)
        {
            var track = new JsonObject();
            if (pending.Held.TryGetValue(field, out var held))
            {
                track["held"] = held.Probability;
                if (held.Value is not null && Rested(held, saved))
                {
                    track["withheld"] = new JsonObject
                    {
                        ["value"] = held.Value,
                        ["origin"] = held.Origin ?? "decider",
                        ["fit"] = held.Fit ?? "exact",
                    };
                    if (held.Layer is not null) track["layer"] = held.Layer;
                    if (held.Promise is { } promise) track["promise"] = promise;
                }
            }

            // When the person reached the field is theirs to report; what they were shown is not.
            if (fields[field] is JsonObject earlier && earlier["at"] is { } at) track["at"] = at.DeepClone();
            fields[field] = track;
        }

        return root.ToJsonString();
    }
}
