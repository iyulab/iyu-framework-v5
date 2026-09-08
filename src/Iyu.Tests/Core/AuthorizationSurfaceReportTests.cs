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
    public void An_odata_set_reports_a_read_a_write_and_a_delete_entry()
    {
        var provider = new ODataAuthorizationSurfaceProvider(
            RegistryWith(("Widgets", "widgets.read", "widgets.write")));

        var entries = provider.Describe();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e =>
            e is { Surface: "OData", Entity: "Widgets", Operation: AuthorizationSurfaceOperation.Read, Policy: "widgets.read" });
        Assert.Contains(entries, e =>
            e is { Surface: "OData", Entity: "Widgets", Operation: AuthorizationSurfaceOperation.Write, Policy: "widgets.write" });
        // No delete policy was named, so DELETE runs under the write one — the row reports the
        // policy that actually applies, not the parameter that happened to be filled in.
        Assert.Contains(entries, e =>
            e is { Surface: "OData", Entity: "Widgets", Operation: AuthorizationSurfaceOperation.Delete, Policy: "widgets.write" });
    }

    /// <summary>
    /// The delete axis: an app where "may edit" and "may delete" differ gets its own row, which is
    /// what makes the report usable for it at all. Before this, such an app had to attach policies
    /// outside the framework and then saw its entire surface reported as unprotected.
    /// </summary>
    [Fact]
    public void A_separate_delete_policy_reports_on_its_own_row()
    {
        var registry = new IyuEntityPairRegistry();
        registry.Register<SurfaceWidgetExt, SurfaceWidget>("Widgets");
        registry.RestrictPolicy("Widgets", "widgets.read", "widgets.write", "widgets.delete");

        var entries = new ODataAuthorizationSurfaceProvider(registry).Describe();

        Assert.Contains(entries, e =>
            e is { Operation: AuthorizationSurfaceOperation.Write, Policy: "widgets.write" });
        Assert.Contains(entries, e =>
            e is { Operation: AuthorizationSurfaceOperation.Delete, Policy: "widgets.delete" });
    }

    /// <summary>
    /// The phantom rule holds per verb, not just for the all-or-nothing case: a set that withdraws
    /// DELETE alone still has a POST/PATCH surface, so the write row stays and only the delete row
    /// goes. Reporting a delete row for a set that refuses DELETE would be the same permanently-null
    /// entry <see cref="A_fully_read_only_set_reports_no_write_entry"/> exists to prevent.
    /// </summary>
    [Fact]
    public void A_set_that_withdraws_delete_alone_reports_a_write_entry_but_no_delete_entry()
    {
        var registry = new IyuEntityPairRegistry();
        registry.Register<SurfaceWidgetExt, SurfaceWidget>(
            "Widgets", new HashSet<ODataVerb> { ODataVerb.Delete });
        registry.RestrictPolicy("Widgets", "widgets.read", "widgets.write");

        var entries = new ODataAuthorizationSurfaceProvider(registry).Describe();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Operation == AuthorizationSurfaceOperation.Write);
        Assert.DoesNotContain(entries, e => e.Operation == AuthorizationSurfaceOperation.Delete);
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
