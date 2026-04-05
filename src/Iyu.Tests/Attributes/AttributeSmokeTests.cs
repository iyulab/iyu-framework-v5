using Iyu.Core.Attributes;
using Xunit;

namespace Iyu.Tests.Attributes;

public class AttributeSmokeTests
{
    private sealed class Dummy
    {
        [Lookup("customer.name")]
        public string? CustomerName { get; set; }

        [Rollup("sum(items.amount)", Indexed = true)]
        public decimal Total { get; set; }

        [Computed("supply_total * 0.1")]
        public decimal Vat { get; set; }

        [Reference("Customer")]
        public Guid CustomerId { get; set; }
    }

    [Fact]
    public void Lookup_metadata_roundtrip()
    {
        var attr = typeof(Dummy).GetProperty(nameof(Dummy.CustomerName))!
            .GetCustomAttributes(typeof(LookupAttribute), false)
            .Cast<LookupAttribute>().Single();
        Assert.Equal("customer.name", attr.Path);
    }

    [Fact]
    public void Rollup_carries_indexed_flag()
    {
        var attr = typeof(Dummy).GetProperty(nameof(Dummy.Total))!
            .GetCustomAttributes(typeof(RollupAttribute), false)
            .Cast<RollupAttribute>().Single();
        Assert.Equal("sum(items.amount)", attr.Expression);
        Assert.True(attr.Indexed);
    }

    [Fact]
    public void Computed_and_Reference_attributes_expose_expression_and_target()
    {
        var computed = typeof(Dummy).GetProperty(nameof(Dummy.Vat))!
            .GetCustomAttributes(typeof(ComputedAttribute), false)
            .Cast<ComputedAttribute>().Single();
        Assert.Equal("supply_total * 0.1", computed.Expression);

        var reference = typeof(Dummy).GetProperty(nameof(Dummy.CustomerId))!
            .GetCustomAttributes(typeof(ReferenceAttribute), false)
            .Cast<ReferenceAttribute>().Single();
        Assert.Equal("Customer", reference.Target);
    }
}
