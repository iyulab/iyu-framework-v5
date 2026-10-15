using System.Data.Common;
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
/// (no Npgsql or SqlClient reference): a unique or foreign-key violation is <c>409</c>, a missing required value or a
/// value too long for its column <c>400</c>, each with its own code (<see cref="ODataErrorCodes"/>). A concurrency
/// conflict is <c>409 ConcurrencyConflict</c>. Anything else — a provider that reports no SQLSTATE (SQL Server's client
/// does not), or a class not named here — stays the neutral <c>409 Conflict</c>. The provider's text never reaches
/// the caller. An application classifies what this does not by registering its own <see cref="IExceptionHandler"/>
/// before <c>AddIyuMainServer</c>: handlers are asked in registration order.
/// </para>
/// <para>
/// Any other exception is left unhandled (returns <see langword="false"/>), so ASP.NET Core's <c>AddProblemDetails()</c>
/// fallback turns it into a structured — if generic — 500.
/// </para>
/// </remarks>
public sealed class IyuWriteExceptionHandler : IExceptionHandler
{
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
            DbUpdateException update => Classify(SqlStateOf(update)),
            _ => null,
        };
        if (answer is null) return false;

        httpContext.Response.StatusCode = answer.Status;
        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
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

    /// <summary>The SQLSTATE the provider's exception inside <paramref name="exception"/> reports, if any.</summary>
    private static string? SqlStateOf(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: { Length: > 0 } state }) return state;
        }
        return null;
    }

    private static Answer Classify(string? sqlState) => sqlState switch
    {
        "23505" => new(StatusCodes.Status409Conflict, ODataErrorCodes.UniqueViolation,
            "The write conflicts with the current state of the data.",
            "Another row already has a value that must be unique."),
        "23503" => new(StatusCodes.Status409Conflict, ODataErrorCodes.ForeignKeyViolation,
            "The write conflicts with the current state of the data.",
            "The write refers to a row that does not exist, or removes a row that others still refer to."),
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
