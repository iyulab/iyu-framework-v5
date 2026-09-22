using Iyu.Server.OData;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// What <see cref="IyuEntityPairRegistry.DeclareSharedKey"/> refuses at configuration time.
/// </summary>
/// <remarks>
/// Each of these is a statement that cannot be made true later by any request, so it is answered
/// where it is written rather than where it would eventually be felt.
/// </remarks>
public class SharedKeyDeclarationTests
{
    private static IyuEntityPairRegistry WithTwoPairs()
    {
        var registry = new IyuEntityPairRegistry();
        registry.Register<MachineExt, Machine>("Machines");
        registry.Register<ServicePlanExt, ServicePlan>("ServicePlans");
        return registry;
    }

    [Fact]
    public void A_declared_pair_records_its_principal()
    {
        var registry = WithTwoPairs();

        registry.DeclareSharedKey("ServicePlans", "Machines");

        Assert.Equal("Machines", registry.Find("ServicePlans")!.SharedKeyPrincipalSet);
    }

    /// <summary>
    /// The negative control for the one above: registration alone declares nothing.
    /// </summary>
    [Fact]
    public void An_undeclared_pair_records_no_principal()
    {
        var registry = WithTwoPairs();

        Assert.Null(registry.Find("ServicePlans")!.SharedKeyPrincipalSet);
    }

    [Fact]
    public void A_set_cannot_share_its_key_with_itself()
    {
        var registry = WithTwoPairs();

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.DeclareSharedKey("Machines", "Machines"));

        Assert.Contains("with itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_principal_that_is_not_registered_is_refused()
    {
        var registry = WithTwoPairs();

        Assert.Throws<InvalidOperationException>(
            () => registry.DeclareSharedKey("ServicePlans", "Nowhere"));
    }

    [Fact]
    public void A_set_that_is_not_registered_cannot_be_declared()
    {
        var registry = WithTwoPairs();

        Assert.Throws<InvalidOperationException>(
            () => registry.DeclareSharedKey("Nowhere", "Machines"));
    }

    /// <summary>
    /// A chain is refused: if the principal's own key already belongs to a third set, nothing
    /// decides which of the two the bottom row's key answers to.
    /// </summary>
    [Fact]
    public void A_principal_that_already_shares_its_own_key_is_refused()
    {
        var registry = WithTwoPairs();
        registry.Register<MemoExt, Memo>("Memos");
        registry.DeclareSharedKey("ServicePlans", "Machines");

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.DeclareSharedKey("Memos", "ServicePlans"));

        Assert.Contains("in turn", ex.Message, StringComparison.Ordinal);
    }
}
