using Iyu.Core.Lowline;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The two row interfaces are what an application's entities implement, and the assembly that holds the entities has no
/// reason to carry a web host, a native engine or EF Core. They live in the package every entity layer already references.
/// </summary>
public class LowlineRowsPlacementTests
{
    [Theory]
    [InlineData(typeof(ILowlineRecordRow))]
    [InlineData(typeof(ILowlineTuningRow))]
    public void A_row_interface_lives_in_Iyu_Core(Type row) => Assert.Equal("Iyu.Core", row.Assembly.GetName().Name);

    [Fact]
    public void The_assembly_that_holds_them_references_neither_the_host_nor_the_engine_nor_EF()
    {
        var references = typeof(ILowlineRecordRow).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r =>
            r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || r.Equals("Lowline", StringComparison.Ordinal)
            || r.StartsWith("Iyu.", StringComparison.Ordinal));
    }
}
