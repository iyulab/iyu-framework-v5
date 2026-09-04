using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer;

/// <summary>
/// Maps <see cref="DbUpdateException"/> (and its subtype <see cref="DbUpdateConcurrencyException"/>)
/// raised while an <c>IyuODataController</c> write action calls <c>SaveChangesAsync</c> to a
/// structured RFC 7807 <c>ProblemDetails</c> 409 response, instead of letting the underlying
/// provider exception surface as a bare, unstructured 500.
/// </summary>
/// <remarks>
/// Every <see cref="DbUpdateException"/> is mapped to the same generic 409 regardless of its actual
/// cause (unique-index violation, concurrency conflict, a foreign-key violation, ...) — this
/// framework stays deliberately provider-agnostic (no Npgsql/SqlClient package reference), so it has
/// no reliable, provider-independent way to distinguish those causes without one. Any other
/// exception is left unhandled (returns <see langword="false"/>) so ASP.NET Core's own
/// <c>AddProblemDetails()</c> fallback still turns it into a structured — if generic — 500; this
/// handler only narrows the one case it has an opinion about.
/// </remarks>
public sealed class IyuWriteExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException) return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The write conflicts with the current state of the data.",
                Detail = "The request could not be completed because it conflicts with existing data.",
            },
        });
    }
}
