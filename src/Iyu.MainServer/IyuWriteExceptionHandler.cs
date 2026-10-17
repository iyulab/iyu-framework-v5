using System.Data.Common;
using Iyu.Data;
using Iyu.Data.WriteRules;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer;

/// <summary>
/// Answers a failed write with what kind of failure it was, as an RFC 7807 <c>ProblemDetails</c> (on the OData route,
/// an OData error whose <c>code</c> is the kind) — instead of letting the exception surface as a bare 500.
/// </summary>
/// <remarks>
/// <para>
/// <b>A write rule's refusal</b> (<see cref="WriteRuleViolationException"/>) is <c>422</c> with the rule's code and
/// message — both written for the caller.
/// </para>
/// <para>
/// <b>The data store's refusal</b> (<see cref="DbUpdateException"/>) is classified by the standard SQLSTATE class the
/// provider reports through ADO.NET's <see cref="DbException.SqlState"/>, which keeps this framework provider-neutral
/// (no Npgsql or SqlClient reference): a unique violation is <c>409</c>, a missing required value or a value too long
/// for its column <c>400</c>, each with its own code (<see cref="ODataErrorCodes"/>). A foreign-key violation is two
/// failures with one SQLSTATE, told apart by what the failed write did — EF's own record of it
/// (<see cref="DbUpdateException.Entries"/>): removing a row others still refer to is the data's current state,
/// <c>409 ReferencedByOthers</c>; writing a reference to a row that does not exist is the request's value,
/// <c>400 ReferenceMissing</c>. A concurrency
/// conflict is <c>409 ConcurrencyConflict</c>. A provider that reports no SQLSTATE is translated by a registered
/// <see cref="ISqlStateTranslator"/> (SQL Server's: <c>Iyu.Data.SqlServer</c>). Anything else — no SQLSTATE and no
/// translation, or a class not named here — stays the neutral <c>409 Conflict</c>. The provider's text never reaches
/// the caller. An application classifies what this does not by registering its own <see cref="IExceptionHandler"/>
/// before <c>AddIyuMainServer</c>: handlers are asked in registration order.
/// </para>
/// <para>
/// The titles and details are English sentences for a person; the <c>code</c> extension is the contract. An application
/// that words them itself does so in its own <see cref="IProblemDetailsWriter"/>, keyed by the code — and where the
/// sentence depends on what failed, it reads the classified exception from <see cref="ProblemDetailsContext.Exception"/>
/// (a <see cref="DbUpdateException"/>'s <see cref="DbUpdateException.Entries"/>), instead of classifying the failure a
/// second time.
/// </para>
/// <para>
/// Any other exception is left unhandled (returns <see langword="false"/>), so ASP.NET Core's <c>AddProblemDetails()</c>
/// fallback turns it into a structured — if generic — 500.
/// </para>
/// </remarks>
public sealed class IyuWriteExceptionHandler(IEnumerable<ISqlStateTranslator> translators) : IExceptionHandler
{
    private readonly ISqlStateTranslator[] _translators = translators.ToArray();

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var answer = exception switch
        {
            WriteRuleViolationException rule => new Answer(StatusCodes.Status422UnprocessableEntity, rule.Code,
                "The write breaks a rule of the data.", rule.Message),
            DbUpdateConcurrencyException => new Answer(StatusCodes.Status409Conflict, ODataErrorCodes.ConcurrencyConflict,
                "The data changed since it was read.",
                "The row was changed or removed after it was read. Read it again and decide whether to reapply."),
            DbUpdateException update => Classify(SqlStateOf(update), update),
            _ => null,
        };
        if (answer is null) return false;

        httpContext.Response.StatusCode = answer.Status;
        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            // What failed, for a writer whose sentence depends on it — the rows a removal tripped on, the rule that
            // refused. A writer decides what of it the caller sees; this framework's own write none of it.
            Exception = exception,
            ProblemDetails =
            {
                Status = answer.Status,
                Title = answer.Title,
                Detail = answer.Detail,
                Extensions = { ["code"] = answer.Code },
            },
        });
    }

    private sealed record Answer(int Status, string Code, string Title, string Detail);

    /// <summary>
    /// The SQLSTATE of the provider's exception inside <paramref name="exception"/> — as it reports it, or as a
    /// registered translator names it — if any.
    /// </summary>
    private string? SqlStateOf(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is not DbException db) continue;
            if (db.SqlState is { Length: > 0 } state) return state;
            foreach (var translator in _translators)
                if (translator.SqlStateOf(db) is { Length: > 0 } translated) return translated;
        }
        return null;
    }

    private static Answer Classify(string? sqlState, DbUpdateException update) => sqlState switch
    {
        "23505" => new(StatusCodes.Status409Conflict, ODataErrorCodes.UniqueViolation,
            "The write conflicts with the current state of the data.",
            "Another row already has a value that must be unique."),
        // One SQLSTATE, two failures. A removal among the failed entries is the row others still refer to — the data's
        // state, which clearing those references changes. Otherwise the write named a row that does not exist — the
        // request's value, which sending it again never changes. A batch that mixes both is answered as the removal.
        "23503" when update.Entries.Any(e => e.State == EntityState.Deleted) => new(StatusCodes.Status409Conflict,
            ODataErrorCodes.ReferencedByOthers,
            "The write conflicts with the current state of the data.",
            "The row is still referred to by other rows. Remove or change those references first."),
        "23503" => new(StatusCodes.Status400BadRequest, ODataErrorCodes.ReferenceMissing,
            "The write refers to a row that does not exist.",
            "A reference names a row that does not exist."),
        "23502" => new(StatusCodes.Status400BadRequest, ODataErrorCodes.RequiredValueMissing,
            "The write is missing a required value.",
            "A value the data requires is missing."),
        "22001" => new(StatusCodes.Status400BadRequest, ODataErrorCodes.ValueTooLong,
            "A value is too long.",
            "A value is longer than its field holds."),
        _ => new(StatusCodes.Status409Conflict, ODataErrorCodes.Conflict,
            "The write conflicts with the current state of the data.",
            "The request could not be completed because it conflicts with existing data."),
    };
}
