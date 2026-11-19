using HotChocolate;
using HotChocolate.Execution;
using Iyu.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Answers a field that failed because the database does not have a table or view the host maps with
/// <see cref="DatabaseObjectMissing.Code"/> as the error's <c>code</c>, instead of HotChocolate's anonymous
/// <c>Unexpected Execution Error</c>.
/// </summary>
/// <remarks>
/// Only that failure is touched; any other error passes through unchanged. The provider's exception goes to the server's log
/// (once for the request) and never into the response. The message names the object and says to apply the schema only where
/// details are wanted — <see cref="IyuGraphQLSchemaBuilder.IncludeErrorDetails"/>, else the Development environment.
/// </remarks>
internal sealed class IyuGraphQLDatabaseErrorFilter(IServiceProvider schemaServices, Func<bool?> includeDetails) : IErrorFilter
{
    private const string GenericMessage = "An error occurred while processing your request.";

    public IError OnError(IError error)
    {
        if (error.Exception is not { } exception) return error;

        // The schema's own provider is not the host's: what the host registered (its translators, its environment, its
        // loggers) is reached through the application's root provider.
        var host = schemaServices.GetRootServiceProvider();
        if (DatabaseObjectMissing.Find(exception, host.GetServices<ISqlStateTranslator>()) is not { } missing) return error;

        var context = host.GetService<IHttpContextAccessor>()?.HttpContext;
        missing.Report(host.GetRequiredService<ILoggerFactory>().CreateLogger<IyuGraphQLDatabaseErrorFilter>(), exception, context?.Items);

        var details = includeDetails() ?? host.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        return ErrorBuilder.FromError(error)
            .SetMessage(details ? missing.Detail : GenericMessage)
            .SetCode(DatabaseObjectMissing.Code)
            .SetException(null)
            .Build();
    }
}
