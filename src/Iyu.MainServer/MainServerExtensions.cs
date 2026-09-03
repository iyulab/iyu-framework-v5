using System.Reflection;
using Iyu.Data;
using Microsoft.AspNetCore.Authorization;
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
        // Resolved via [FromServices] on IyuODataController's write actions, not
        // constructor injection — adding a constructor parameter would break every
        // generated controller subclass, which calls only `base(context)`.
        services.AddSingleton(options.ODataModel.Registry);

        var mvc = services.AddControllers()
            .AddJsonOptions(json =>
            {
                // Enums carrying [EnumMember(Value=...)] serialize by that wire name here,
                // matching IyuEdmModelBuilder's /$data behavior (Iyu.Server.OData) so /api
                // and /$data cannot disagree on the same enum's spelling. An enum with no
                // [EnumMember] attributes falls through to the plain converter below,
                // unaffected — see EnumMemberJsonConverterFactory.CanConvert.
                json.JsonSerializerOptions.Converters.Add(new Iyu.Data.EnumMemberJsonConverterFactory());
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

        // Wire OData per-set authorization only when some registered pair actually uses
        // RestrictPolicy — the same "only pay for what you use" gate options.GraphQL.ApplyTo
        // applies for its own authorization bridge (_usesAuthorization). AddAuthorizationCore
        // guarantees a working IAuthorizationService even for a consumer that calls
        // RestrictPolicy without ever calling AddIyuIdentity.
        if (options.ODataModel.Registry.All.Any(p => p.ReadPolicy is not null || p.WritePolicy is not null))
        {
            services.AddAuthorizationCore();
            mvc.AddMvcOptions(o => o.Conventions.Add(
                new IyuODataAuthorizationConvention(options.ODataModel.Registry)));
        }

        // HotChocolate rejects a schema whose Query type has zero fields at host
        // startup (RequestExecutorWarmupService eagerly builds it) — a consumer that
        // registered no GraphQL entity pairs would otherwise crash the whole host, not
        // just the GraphQL surface. Only wire GraphQL when there is something to expose;
        // UseIyuMainServer makes the matching call on the read side (MapGraphQL).
        if (options.GraphQL.QueryNames.Count > 0)
        {
            var gql = services.AddGraphQLServer();
            options.GraphQL.ApplyTo(gql);
        }

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

        var options = app.Services.GetRequiredService<IyuMainServerOptions>();
        if (options.GraphQL.QueryNames.Count > 0)
        {
            app.MapGraphQL();
        }
        return app;
    }

    /// <summary>
    /// Assembles the set of assemblies that may host the OData controllers: the
    /// consumer's explicit <see cref="IyuMainServerOptions.ControllerAssemblies"/>,
    /// the <c>TContext</c> assembly, the assembly declaring the registration
    /// callback, and the one hosting OData's own <c>MetadataController</c>. The
    /// callback's assembly is a best-effort signal — it resolves to the
    /// controller-hosting assembly for the standard method-group form but to the
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

        // $metadata and the service document are served by MetadataController, which lives in
        // OData's own assembly — never the entry assembly, so default discovery reaches it only
        // through the entry assembly's dependency graph. Under a test host that graph is the
        // runner's, so `AddRouteComponents` publishes the route and nothing answers it: an
        // integration test asking for $metadata gets a 404 that looks like a modelling mistake.
        // The same asymmetry the consumer-assembly registration above exists to remove, and the
        // dedup guard makes it free where discovery already found it.
        var metadataAssembly = typeof(Microsoft.AspNetCore.OData.Routing.Controllers.MetadataController).Assembly;
        if (seen.Add(metadataAssembly))
            yield return metadataAssembly;
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
