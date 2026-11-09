using System.Reflection;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// <c>Iyu.VaultAi</c> is host-neutral: a host that has no Entity Framework and no main server must be able to run the folder chatbots
/// with this package alone. That is true of the project file today; this holds it, so that a reference added later cannot quietly take it away.
/// </summary>
public class VaultAiReferenceBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "Iyu.Core", "Iyu.Data", "Iyu.MainServer", "Iyu.Server", "Iyu.Report", "Iyu.FileServer", "Iyu.Lowline",
        "Microsoft.EntityFrameworkCore",
    ];

    private static IEnumerable<string> Offending(Assembly assembly)
        => assembly.GetReferencedAssemblies().Select(a => a.Name ?? "")
            .Where(name => Forbidden.Any(f => name == f || name.StartsWith(f + ".", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void The_vault_ai_package_references_no_framework_core_data_or_server_assembly()
    {
        Assert.Empty(Offending(typeof(Iyu.VaultAi.VaultAiSettings).Assembly));
    }

    [Fact]
    public void The_check_does_see_such_a_reference_where_there_is_one()
    {
        // A control: the same test on an assembly that does reference the core, so a check that can never fail is noticed.
        Assert.NotEmpty(Offending(typeof(Iyu.MainServer.IyuMainServerOptions).Assembly));
    }
}
