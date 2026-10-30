using System.Security.Claims;
using Lowline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>How a request to propose for a record ended.</summary>
public enum IyuLowlineProposeStatus
{
    /// <summary>The engine proposed; <see cref="IyuLowlineProposeOutcome.Proposals"/> holds what to show.</summary>
    Proposed,

    /// <summary>No form of that name is defined, or no hook serves it.</summary>
    FormUnknown,

    /// <summary>The hook found no record the caller may work on — missing and forbidden look the same.</summary>
    RecordNotFound,

    /// <summary>The request or the values were refused; nothing was proposed or kept.</summary>
    Refused,

    /// <summary>
    /// The caller already has as many records waiting for their settle with a check kept as
    /// <see cref="IyuLowlineOptions.MaxPendingChecks"/> allows. Nothing is proposed: answering without the check would be
    /// the way round the limit. <see cref="IyuLowlineProposeOutcome.RetryAfter"/> says when a place is likely to be free.
    /// </summary>
    CheckBacklog,
}

/// <summary>The result of <see cref="IyuLowlineProposeService.ProposeAsync"/>.</summary>
/// <param name="Status">How the request ended.</param>
/// <param name="Proposals">For <see cref="IyuLowlineProposeStatus.Proposed"/>: what to show, with the answers held back for a
/// check already left out.</param>
/// <param name="Detail">For <see cref="IyuLowlineProposeStatus.Refused"/>: why, in the engine's or this service's words — the
/// values are the caller's own, so they may go back to the caller.</param>
/// <param name="RetryAfter">For <see cref="IyuLowlineProposeStatus.CheckBacklog"/>: how long until the oldest waiting draw lapses.</param>
public sealed record IyuLowlineProposeOutcome(
    IyuLowlineProposeStatus Status,
    IReadOnlyList<Proposal>? Proposals = null,
    string? Detail = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>Whether the engine proposed.</summary>
    public bool Proposed => Status == IyuLowlineProposeStatus.Proposed;
}

/// <summary>
/// Proposes for a record being written: asks the form's hook where the record belongs and what its caller may pick, has the
/// engine propose from <em>the values it is given</em>, and — for a signed-in person, when the host names settlers — holds
/// some answers back to check the values that are settled afterwards. The mirror of <see cref="IyuLowlineSettleService"/>,
/// and what the <c>propose</c> route calls.
/// </summary>
/// <remarks>
/// <para>
/// The values are the <em>caller's</em>: a host that derives the evidence of a proposal on the server — from a catalogue, a
/// parent record, a rule of its own domain — calls this from its own route and hands over what it derived. The route this
/// package maps hands over what the request carried. The check is the same either way: it is drawn, kept and bounded here,
/// so a host that does not use the mapped route gets it too, and gets the same answer when too many are waiting.
/// </para>
/// <para>
/// Pair it with <see cref="IyuLowlineSettleService"/> on the same record id: the draw kept here is what the settle finds.
/// </para>
/// </remarks>
public sealed class IyuLowlineProposeService(
    LowlineForms forms,
    IEnumerable<ILowlineFormHook> hooks,
    IOptions<IyuLowlineOptions> options,
    IServiceProvider services,
    ILogger<IyuLowlineProposeService> logger)
{
    /// <summary>Proposes for the record <paramref name="recordId"/> of form <paramref name="form"/>.</summary>
    /// <param name="form">The form's name, as defined in <c>Engine.Form</c>.</param>
    /// <param name="recordId">The id the form element uses for the record; it may not be saved yet.</param>
    /// <param name="values">The values the proposal rests on, by field name — what the host decided to put in front of the
    /// engine. They are not read from anywhere else.</param>
    /// <param name="user">The signed-in caller; handed to the hook so it can refuse a record the caller may not work on, and
    /// the settler the check is kept under when the host names settlers.</param>
    /// <param name="cancellationToken">Cancels the hook, the engine and the cache.</param>
    public async Task<IyuLowlineProposeOutcome> ProposeAsync(
        string form,
        string recordId,
        IReadOnlyDictionary<string, FieldValue> values,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(user);

        var settings = options.Value;
        if (string.IsNullOrEmpty(form) || !settings.Engine.Forms.ContainsKey(form))
            return new(IyuLowlineProposeStatus.FormUnknown);
        var hook = hooks.FirstOrDefault(h => string.Equals(h.Form, form, StringComparison.Ordinal));
        if (hook is null)
            return new(IyuLowlineProposeStatus.FormUnknown);

        if (string.IsNullOrWhiteSpace(recordId))
            return new(IyuLowlineProposeStatus.Refused, Detail: "a record id is required");

        var context = await hook.OpenAsync(recordId, user, cancellationToken);
        if (context is null)
            return new(IyuLowlineProposeStatus.RecordNotFound);

        try
        {
            var scope = new LowlineScope(form, context.Space);
            var shown = await forms.ProposeAsync(scope, recordId, values, context.Allowed, cancellationToken);

            // With a settler id (a salt) the server holds some answers back to check the values people then settle; a machine
            // is not checked, and a caller with no account id has no settler to keep the draw under.
            if (!settings.Settler.IsServiceAccount(user)
                && IyuLowlineSettlerIdentity.Resolve(settings.Settler, user, out _) is { } settler)
            {
                var recheck = services.GetRequiredService<IyuLowlineRecheck>();
                shown = await recheck.ApplyAsync(scope, recordId, settler, values, shown, cancellationToken);
            }

            return new(IyuLowlineProposeStatus.Proposed, shown);
        }
        catch (ArgumentException refused)
        {
            return new(IyuLowlineProposeStatus.Refused, Detail: refused.Message);
        }
        catch (IyuLowlineCheckBacklogException backlog)
        {
            // Logged for whoever runs the host, without the record or the person: the id is the caller's and the pseudonym
            // ties to records.
            logger.LogWarning(
                "Lowline refused a proposal of form {Form}: the settler already has {Limit} records awaiting settlement with a check.",
                form, settings.MaxPendingChecks);
            return new(IyuLowlineProposeStatus.CheckBacklog, RetryAfter: backlog.RetryAfter);
        }
    }
}
