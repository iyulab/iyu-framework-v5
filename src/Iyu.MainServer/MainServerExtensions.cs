using System.Reflection;
using Iyu.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
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

        var mvc = services.AddControllers()
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .AddOData(odata => odata
                .Select().Filter().OrderBy().Expand().Count().SetMaxTop(null)
                // $search is registered per route component (the OData query pipeline resolves
                // ISearchBinder from the per-route sub-container, not the global DI container).
                // Without a binder, $search is silently ignored and returns the full set.
                .AddRouteComponents(
                    options.ODataRoutePrefix,
                    options.ODataModel.GetEdmModel(),
                    routeServices => routeServices.AddSingleton<
                        Microsoft.AspNetCore.OData.Query.Expressions.ISearchBinder,
                        Iyu.Server.OData.IyuStringSearchBinder>()));

        // The generated OData controllers (concrete IyuODataController<> subclasses)
        // live alongside the entity registrations, not in the entry assembly. MVC's
        // default part discovery only walks the entry assembly's closure, so under a
        // test host (entry = testhost) or any non-standard host those controllers are
        // never found and every endpoint silently 404s. Register the assemblies that
        // plausibly host them — the TContext's assembly and the registration
        // callback's declaring assembly cover the standard method-group pattern —
        // plus any the consumer named explicitly. Deduplicated so production (where
        // discovery already found them) does not double-register controller types.
        RegisterControllerParts(mvc.PartManager, CandidateControllerAssemblies(typeof(TContext), configure, options));

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

    /// <summary>
    /// Assembles the set of assemblies that may host the generated OData
    /// controllers: the consumer's explicit <see cref="IyuMainServerOptions.ControllerAssemblies"/>,
    /// the <c>TContext</c> assembly, and the assembly declaring the
    /// registration callback. The last is a best-effort signal — it resolves to the
    /// controller-hosting assembly for the standard method-group form
    /// (<c>configure: ApiRegistration.RegisterGeneratedEntities</c>) but to the
    /// caller for a lambda wrapper, which is exactly what
    /// <see cref="IyuMainServerOptions.ControllerAssemblies"/> exists to cover.
    /// </summary>
    private static IEnumerable<Assembly> CandidateControllerAssemblies(
        Type contextType,
        Action<IyuMainServerOptions> configure,
        IyuMainServerOptions options)
    {
        var seen = new HashSet<Assembly>();

        foreach (var asm in options.ControllerAssemblies)
        {
            if (asm is not null && seen.Add(asm))
                yield return asm;
        }

        if (seen.Add(contextType.Assembly))
            yield return contextType.Assembly;

        var callbackAssembly = configure.Method.DeclaringType?.Assembly;
        if (callbackAssembly is not null && seen.Add(callbackAssembly))
            yield return callbackAssembly;
    }

    /// <summary>
    /// Adds an <see cref="AssemblyPart"/> for each candidate assembly not already
    /// present in the part manager. The dedup guard is essential: adding an
    /// assembly whose controllers were already discovered would register duplicate
    /// controller types and produce ambiguous-action failures at routing time.
    /// </summary>
    private static void RegisterControllerParts(ApplicationPartManager partManager, IEnumerable<Assembly> assemblies)
    {
        var present = new HashSet<Assembly>(
            partManager.ApplicationParts.OfType<AssemblyPart>().Select(p => p.Assembly));

        foreach (var assembly in assemblies)
        {
            if (present.Add(assembly))
                partManager.ApplicationParts.Add(new AssemblyPart(assembly));
        }
    }
}
