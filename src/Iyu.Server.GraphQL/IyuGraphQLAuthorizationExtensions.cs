using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Registers <see cref="IyuGraphQLPolicyAuthorizationHandler"/> as HotChocolate's authorization
/// handler and ensures <c>IHttpContextAccessor</c> is available for it to read the current
/// request's user from. Internal — <see cref="IyuGraphQLSchemaBuilder.ApplyTo"/> is the only
/// caller, invoked automatically the first time an entity pair is registered with an
/// <c>authorizePolicy</c>, so there is no separate step for a consumer to remember or forget.
/// </summary>
internal static class IyuGraphQLAuthorizationExtensions
{
    internal static IRequestExecutorBuilder AddIyuGraphQLAuthorization(this IRequestExecutorBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        return builder.AddAuthorizationCore().AddAuthorizationHandler<IyuGraphQLPolicyAuthorizationHandler>();
    }
}
