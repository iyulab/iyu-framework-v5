using Iyu.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer;

/// <summary>
/// Composite bootstrap for the Iyu runtime — a single entry point that wires
/// EF Core, OData, and GraphQL into an ASP.NET Core host. Consumers register
/// entity pairs via the configuration callback, then call
/// <see cref="UseIyuMainServer"/> once the app is built.
/// </summary>
public static class MainServerExtensions
{
    /// <summary>
    /// Registers the Iyu runtime services. The <paramref name="configure"/>
    /// callback receives an <see cref="IyuMainServerOptions"/> onto which
    /// consumers (or generator-emitted registration classes) register OData
    /// and GraphQL entity pairs.
    /// </summary>
    /// <typeparam name="TContext">The concrete <see cref="IyuDbContext"/> the application uses.</typeparam>
    /// <param name="services">The DI container.</param>
    /// <param name="configureDb">
    /// EF Core options configuration (provider selection, connection string,
    /// etc.). The <see cref="IyuTimestampInterceptor"/> is added automatically
    /// by <see cref="IyuDbContext.OnConfiguring"/>.
    /// </param>
    /// <param name="configure">Entity pair registration callback.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddIyuMainServer<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDb,
        Action<IyuMainServerOptions> configure)
        where TContext : IyuDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDb);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new IyuMainServerOptions();
        configure(options);

        services.AddDbContext<TContext>(configureDb);
        // Let the generic OData controller and any other consumer resolve the
        // base class IyuDbContext from DI without knowing the concrete type.
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<TContext>());

        services.AddControllers()
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .AddOData(odata => odata
                .Select().Filter().OrderBy().Expand().Count().SetMaxTop(null)
                .AddRouteComponents(options.ODataRoutePrefix, options.ODataModel.GetEdmModel()));

        var gql = services.AddGraphQLServer();
        options.GraphQL.ApplyTo(gql);

        // Stash the options so UseIyuMainServer can finish the pipeline wiring.
        services.AddSingleton(options);

        return services;
    }

    /// <summary>
    /// Completes the Iyu runtime pipeline: routing, controllers (OData), and
    /// the GraphQL endpoint at <c>/graphql</c>. Call once in <c>Program.cs</c>
    /// after <c>var app = builder.Build();</c>.
    /// </summary>
    public static WebApplication UseIyuMainServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRouting();
        app.MapControllers();
        app.MapGraphQL();
        return app;
    }
}
