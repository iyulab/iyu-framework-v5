using Iyu.Core.Authorization;
using Iyu.Server.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer;

/// <summary>
/// The GraphQL surface of a host built with <c>AddIyuMainServer</c> — HotChocolate over the entity pairs registered on
/// <see cref="Schema"/>, mapped at <c>/graphql</c>. Reached as <c>options.GraphQL</c>.
/// </summary>
public sealed class GraphQLSurface : IIyuMainServerSurface
{
    /// <summary>The schema builder entity pairs are registered on.</summary>
    public IyuGraphQLSchemaBuilder Schema { get; } = new();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAuthorizationSurfaceProvider>(_ => new GraphQLAuthorizationSurfaceProvider(Schema));

        // HotChocolate rejects a schema whose Query type has zero fields at host startup
        // (RequestExecutorWarmupService eagerly builds it), which would crash the whole host, not just
        // this surface. Only wire GraphQL when there is something to expose; MapEndpoints matches it.
        if (Schema.QueryNames.Count > 0)
            Schema.ApplyTo(services.AddGraphQLServer());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (Schema.QueryNames.Count > 0)
            endpoints.MapGraphQL();
    }
}

/// <summary><c>options.GraphQL</c> — the GraphQL surface's schema builder.</summary>
public static class GraphQLMainServerOptionsExtensions
{
    extension(IyuMainServerOptions options)
    {
        /// <summary>
        /// The GraphQL schema builder (HotChocolate). Using it composes the GraphQL surface into the host;
        /// a host that never does serves no <c>/graphql</c>.
        /// </summary>
        public IyuGraphQLSchemaBuilder GraphQL => options.Surface<GraphQLSurface>().Schema;
    }
}
