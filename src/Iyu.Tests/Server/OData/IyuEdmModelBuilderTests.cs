using Iyu.Core.Entities;
using Iyu.Server.OData;
using Xunit;

namespace Iyu.Tests.Server.OData;

public class IyuEdmModelBuilderTests
{
    public class BankAccount : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
    }

    public class BankAccountExt : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
    }

    public class Customer : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    public class CustomerExt : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void AddEntityPair_registers_and_exposes_set_in_edm_model()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");

        var model = builder.GetEdmModel();
        var container = model.EntityContainer;
        Assert.NotNull(container);
        Assert.NotNull(container!.FindEntitySet("BankAccounts"));

        var pair = builder.Registry.Find("BankAccounts");
        Assert.NotNull(pair);
        Assert.Equal(typeof(BankAccountExt), pair!.ReadType);
        Assert.Equal(typeof(BankAccount), pair.WriteType);
    }

    [Fact]
    public void AddEntityPair_rejects_duplicate_set_name()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        Assert.Throws<InvalidOperationException>(
            () => builder.AddEntityPair<CustomerExt, Customer>("BankAccounts"));
    }

    [Fact]
    public void Registry_All_returns_all_registered_pairs()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.AddEntityPair<CustomerExt, Customer>("Customers");

        var all = builder.Registry.All;
        Assert.Equal(2, all.Count);
    }
}
