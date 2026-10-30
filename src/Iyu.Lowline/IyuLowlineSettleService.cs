using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lowline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>How a request to settle a record ended.</summary>
public enum IyuLowlineSettleStatus
{
    /// <summary>The record was settled and the engine learned from it.</summary>
    Settled,

    /// <summary>The caller is not signed in. Nothing was read or stored.</summary>
    Unauthenticated,

    /// <summary>The caller is a machine (a service client): only a person settles a record. Nothing was read or stored.</summary>
    Forbidden,

    /// <summary>No form of that name is defined, or no hook serves it.</summary>
    FormUnknown,

    /// <summary>The hook found no saved record that the caller may settle — missing and forbidden look the same.</summary>
    RecordNotFound,

    /// <summary>The request or the stored record was refused; nothing was learned or stored.</summary>
    Refused,
}

/// <summary>The result of <see cref="IyuLowlineSettleService.SettleAsync"/>.</summary>
/// <param name="Status">How the request ended.</param>
/// <param name="Detail">For <see cref="IyuLowlineSettleStatus.Refused"/>: why, in the engine's or this service's words.
/// For the host's log — it can quote stored values, so it is not for the response body.</param>
/// <param name="SettledAt">For <see cref="IyuLowlineSettleStatus.Settled"/>: when the engine stamped the record.</param>
public sealed record IyuLowlineSettleOutcome(
    IyuLowlineSettleStatus Status,
    string? Detail = null,
    DateTimeOffset? SettledAt = null)
{
    /// <summary>Whether the record was settled.</summary>
    public bool Settled => Status == IyuLowlineSettleStatus.Settled;
}

/// <summary>
/// Settles a record the application has already saved: reads it back through the form's hook and hands the engine what
/// was <em>stored</em>, together with the caller's account of what was shown (the draft).
/// </summary>
/// <remarks>
/// <para>
/// Three things are fixed here, because a request cannot be trusted with them. <b>The values</b> come from the stored row;
/// the request carries only a record id and a draft. <b>The memory space</b> comes from the hook. <b>Who settled it, in
/// which batch, and which fields were drawn for a check</b> are never taken from the draft: an
/// <see cref="Attestation"/> is always given — naming the settler this host derives from the signed-in caller
/// (<see cref="IyuLowlineSettlerOptions"/>), and nothing else — and the engine lets an attestation replace the draft's own
/// word on those even where it names none. Without one, a caller could sign a record as someone else or claim a check it
/// was not given. A machine (a service client) is refused: only a person settles a record.
/// </para>
/// <para>
/// What the draft can still say is what the person was <em>shown</em> and what they did with it; the engine reads that
/// to tell a suggestion taken from a value typed. It is the caller's unverified account, and it bears on how a record is
/// counted, never on the values learned.
/// </para>
/// </remarks>
public sealed class IyuLowlineSettleService(
    LowlineForms forms,
    IEnumerable<ILowlineFormHook> hooks,
    IOptions<IyuLowlineOptions> options,
    ILogger<IyuLowlineSettleService> logger)
{
    /// <summary>Settles the saved record <paramref name="recordId"/> of form <paramref name="form"/>.</summary>
    /// <param name="form">The form's name, as defined in <c>Engine.Form</c>.</param>
    /// <param name="recordId">The id of the saved record; the hook reads the row it names.</param>
    /// <param name="draft">The caller's account of the record while it was written, as the engine reads it; <c>null</c> for
    /// a record written with nothing shown.</param>
    /// <param name="user">The signed-in caller; handed to the hook so it can refuse a record the caller may not settle.</param>
    /// <param name="cancellationToken">Cancels the read and the settlement.</param>
    public async Task<IyuLowlineSettleOutcome> SettleAsync(
        string form,
        string recordId,
        string? draft,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity?.IsAuthenticated != true)
            return new(IyuLowlineSettleStatus.Unauthenticated);

        var settings = options.Value;
        if (settings.Settler.IsServiceAccount(user))
            return new(IyuLowlineSettleStatus.Forbidden);

        string? settler = null;
        if (settings.Settler.Salt is { } salt)
        {
            var account = user.FindFirst(settings.Settler.AccountClaimType)?.Value;
            if (string.IsNullOrEmpty(account))
                return new(IyuLowlineSettleStatus.Refused, "the signed-in caller carries no account id claim to name the settler by");
            settler = Pseudonym(salt, account);
        }

        if (string.IsNullOrEmpty(form) || !settings.Engine.Forms.ContainsKey(form))
            return new(IyuLowlineSettleStatus.FormUnknown);
        var hook = hooks.FirstOrDefault(h => string.Equals(h.Form, form, StringComparison.Ordinal));
        if (hook is null)
            return new(IyuLowlineSettleStatus.FormUnknown);

        if (string.IsNullOrWhiteSpace(recordId))
            return new(IyuLowlineSettleStatus.Refused, "a record id is required");
        if (draft is not null && draft.Length > settings.MaxDraftLength)
            return new(IyuLowlineSettleStatus.Refused, "the draft is larger than the host allows");

        var saved = await hook.ReadSavedAsync(recordId, user, cancellationToken);
        if (saved is null)
            return new(IyuLowlineSettleStatus.RecordNotFound);

        SettledRecord settled;
        try
        {
            settled = await forms.SettleAsync(
                new LowlineScope(form, saved.Space),
                recordId,
                saved.Values,
                draft,
                SettledBy.Person,
                new Attestation { Settler = settler },
                cancellationToken);
        }
        catch (ArgumentException refusal)
        {
            // The engine refused the stored values or the draft and learned nothing. Logged without its message:
            // that message can quote a stored value.
            logger.LogWarning("Lowline refused to settle a record of form {Form}.", form);
            return new(IyuLowlineSettleStatus.Refused, refusal.Message);
        }

        return new(IyuLowlineSettleStatus.Settled, SettledAt: settled.SettledAt);
    }

    /// <summary>
    /// The opaque id the engine keeps for <paramref name="accountId"/>: <c>HMAC-SHA256</c> under a key derived from the salt
    /// (HKDF-SHA256, purpose <c>iyu-lowline/settler</c>), lower-case hex. The recheck draw uses a key derived from the same
    /// salt under another purpose. This is stored in every record, so changing it re-attributes history: the algorithm is
    /// pinned by a test.
    /// </summary>
    internal static string Pseudonym(string salt, string accountId)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(salt), 32, info: "iyu-lowline/settler"u8.ToArray());
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(accountId)));
    }
}
