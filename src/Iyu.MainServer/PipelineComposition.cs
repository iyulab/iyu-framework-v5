using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer;

/// <summary>
/// Which of the runtime's two pipeline pieces the host has called — written by
/// <see cref="MainServerExtensions.UseIyuErrorResponses"/> and <see cref="MainServerExtensions.MapIyuMainServer"/>,
/// read once when the host starts.
/// </summary>
internal sealed class PipelineComposition
{
    public bool ErrorResponsesUsed { get; set; }

    public bool EndpointsMapped { get; set; }
}

/// <summary>
/// Says at startup that the runtime's endpoints are mapped without the middleware that gives their failures a
/// body.
/// </summary>
/// <remarks>
/// A host that composes its pipeline by hand can leave <see cref="MainServerExtensions.UseIyuErrorResponses"/>
/// out, and nothing then fails: every request is served, and the OData route's <c>401</c>, <c>403</c>, <c>404</c>
/// and <c>405</c> answer with the right status and no body. That is only visible to whoever reads a failure's
/// body, so it is said here, once, where an operator looks first. A warning rather than a refusal to start: a
/// host may write those responses itself, and the order in which the two pieces are called does not matter —
/// which is why this is checked when the host starts and not when either is called.
/// </remarks>
internal sealed class PipelineCompositionCheck(PipelineComposition composition, ILoggerFactory loggers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (composition.EndpointsMapped && !composition.ErrorResponsesUsed)
        {
            loggers.CreateLogger("Iyu.MainServer.Pipeline").LogWarning(
                "MapIyuMainServer() was called without UseIyuErrorResponses(): failures under the OData route "
                + "that carry no body of their own (a set policy's 401/403, an unknown path's 404, an unsupported "
                + "verb's 405) are answered with no error body, and unhandled exceptions are not answered by the "
                + "runtime's handler. Call app.UseIyuErrorResponses() before app.UseRouting() — or use "
                + "app.UseIyuMainServer(), which calls both — unless this host writes those responses itself.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
