using Iyu.Core.Authorization;
using Iyu.Core.Entities;
using Iyu.Server.GraphQL;
using Iyu.Server.OData;
using Xunit;

namespace Iyu.Tests.Core;

public sealed class SurfaceWidget : IyuEntity { public string Name { get; set; } = ""; }
public sealed class SurfaceWidgetExt : IyuEntity { public string Name { get; set; } = ""; }
public sealed class SurfaceGadget : IyuEntity { public string Name { get; set; } = ""; }
public sealed class SurfaceGadgetExt : IyuEntity { public string Name { get; set; } = ""; }

/// <summary>
/// The report exists so a consumer can assert "nothing registered is unprotected" once, and have
/// that assertion widen by itself as entities and surfaces are added. These pin the shape that
/// assertion depends on.
/// </summary>
public class AuthorizationSurfaceReportTests
{
    private static IyuEntityPairRegistry RegistryWith(params (string Set, string? Read, string? Write)[] sets)
    {
        var registry = new IyuEntityPairRegistry();
        foreach (var (set, read, write) in sets)
        {
            registry.Register<SurfaceWidgetExt, SurfaceWidget>(set);
            if (read is not null || write is not null)
                registry.RestrictPolicy(set, read, write);
        }
        return registry;
    }

    [Fact]
    public void An_odata_set_reports_a_read_and_a_write_entry()
    {
        var provider = new ODataAuthorizationSurfaceProvider(
            RegistryWith(("Widgets", "widgets.read", "widgets.write")));

        var entries = provider.Describe();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e =>
            e is { Surface: "OData", Entity: "Widgets", Operation: AuthorizationSurfaceOperation.Read, Policy: "widgets.read" });
        Assert.Contains(entries, e =>
            e is { Surface: "OData", Entity: "Widgets", Operation: AuthorizationSurfaceOperation.Write, Policy: "widgets.write" });
    }

    [Fact]
    public void A_set_with_no_policy_reports_null_rather_than_being_omitted()
    {
        var provider = new ODataAuthorizationSurfaceProvider(RegistryWith(("Widgets", null, null)));

        Assert.All(provider.Describe(), e => Assert.Null(e.Policy));
    }

    /// <summary>
    /// A read-only set has no write surface to protect. Reporting a phantom write row would put a
    /// permanently-null entry in a list whose job is to be empty — and a list that can never be
    /// emptied stops being read.
    /// </summary>
    [Fact]
    public void A_fully_read_only_set_reports_no_write_entry()
    {
        var registry = new IyuEntityPairRegistry();
        registry.Register<SurfaceWidgetExt, SurfaceWidget>(
            "Widgets", Enum.GetValues<ODataVerb>().ToHashSet());
        registry.RestrictPolicy("Widgets", "widgets.read", null);

        var entries = new ODataAuthorizationSurfaceProvider(registry).Describe();

        Assert.Single(entries);
        Assert.Equal(AuthorizationSurfaceOperation.Read, entries[0].Operation);
    }

    /// <summary>
    /// Read entries only — this builder records a mutation prefix but generates no mutations yet,
    /// so a write row would describe a surface that does not exist and could never be given a
    /// policy. It would sit in <see cref="IAuthorizationSurfaceReport.Unprotected"/> forever.
    /// </summary>
    [Fact]
    public void A_graphql_field_reports_one_read_entry_and_no_write_entry()
    {
        var builder = new IyuGraphQLSchemaBuilder();
        builder.AddEntityPair<SurfaceWidgetExt, SurfaceWidget>("widgets", "widget", "widgets.read");

        var entries = new GraphQLAuthorizationSurfaceProvider(builder).Describe();

        var entry = Assert.Single(entries);
        Assert.Equal("GraphQL", entry.Surface);
        Assert.Equal(AuthorizationSurfaceOperation.Read, entry.Operation);
        Assert.Equal("widgets.read", entry.Policy);
    }

    [Fact]
    public void An_unprotected_graphql_field_reports_null_rather_than_being_omitted()
    {
        var builder = new IyuGraphQLSchemaBuilder();
        builder.AddEntityPair<SurfaceWidgetExt, SurfaceWidget>("widgets", "widget");

        var entry = Assert.Single(new GraphQLAuthorizationSurfaceProvider(builder).Describe());

        Assert.Null(entry.Policy);
    }

    /// <summary>
    /// The gap this closes: the builder could be asked what it exposes and how mutations are
    /// named, but not what protected any of it.
    /// </summary>
    [Fact]
    public void GetAuthorizePolicy_distinguishes_unprotected_from_unregistered()
    {
        var builder = new IyuGraphQLSchemaBuilder();
        builder.AddEntityPair<SurfaceWidgetExt, SurfaceWidget>("widgets", "widget");
        builder.AddEntityPair<SurfaceGadgetExt, SurfaceGadget>("gadgets", "gadget", "gadgets.read");

        Assert.Equal("gadgets.read", builder.GetAuthorizePolicy("gadgets"));
        Assert.Null(builder.GetAuthorizePolicy("widgets"));
        Assert.Null(builder.GetAuthorizePolicy("nope"));
        // "registered but unprotected" is told apart from "not registered" by QueryNames.
        Assert.Contains("widgets", builder.QueryNames);
        Assert.DoesNotContain("nope", builder.QueryNames);
    }

    [Fact]
    public void Restrict_applied_after_registration_is_visible_to_the_report()
    {
        var builder = new IyuGraphQLSchemaBuilder();
        builder.AddEntityPair<SurfaceWidgetExt, SurfaceWidget>("widgets", "widget");
        builder.Restrict("widgets", "widgets.read");

        Assert.All(new GraphQLAuthorizationSurfaceProvider(builder).Describe(),
            e => Assert.Equal("widgets.read", e.Policy));
    }
}
