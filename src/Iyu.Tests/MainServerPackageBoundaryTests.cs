using Iyu.MainServer;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// GraphQL is an optional surface: <c>Iyu.MainServer</c> must not reference it, so a host that serves only OData
/// carries none of the GraphQL stack — HotChocolate, and the IDE package it depends on, which comes with its own
/// license terms. The surface is composed in by <c>Iyu.MainServer.GraphQL</c>.
/// </summary>
public class MainServerPackageBoundaryTests
{
    [Fact]
    public void The_composite_host_assembly_references_no_GraphQL_assembly()
    {
        var references = typeof(IyuMainServerOptions).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, n => n.StartsWith("HotChocolate", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("ChilliCream", StringComparison.Ordinal));
        Assert.DoesNotContain("Iyu.Server.GraphQL", references);
    }

    [Fact]
    public void Options_compose_the_GraphQL_surface_only_when_it_is_asked_for()
    {
        var options = new IyuMainServerOptions();
        Assert.Same(options.GraphQL, options.GraphQL);
        Assert.Same(options.Surface<GraphQLSurface>().Schema, options.GraphQL);
    }
}
