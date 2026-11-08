using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Iyu.Core.Entities;
using Iyu.MainServer;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// Properties a read policy cannot guard, because the framework prints them on a request that never names them: the
/// key (links, paging tokens, the <c>Location</c> header), a concurrency token (the entity tag and the <c>If-Match</c>
/// answer) and a foreign-key scalar (the same value a navigation prints as the related row's key). A declaration for one
/// is refused when it is made, as a declaration the framework cannot honour.
/// </summary>
public class PropertyReadPolicyDeclarationTests
{
    private const string Policy = "p.read";

    [Fact]
    public void The_key_cannot_carry_a_property_policy()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new IyuMainServerOptions().AuthorizeProperty<NavOrderExt>(x => x.Id, Policy));
        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_foreign_key_scalar_named_by_convention_cannot_carry_a_property_policy()
    {
        // NavOrderExt.SecretId is the scalar behind the Secret navigation.
        var error = Assert.Throws<ArgumentException>(() =>
            new IyuMainServerOptions().AuthorizeProperty<NavOrderExt>(x => x.SecretId, Policy));
        Assert.Contains("foreign key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_foreign_key_scalar_named_by_an_attribute_cannot_carry_a_property_policy()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new IyuMainServerOptions().AuthorizeProperty<AttributedForeignKeyRead>(x => x.Owner, Policy));
        Assert.Contains("foreign key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_concurrency_token_cannot_carry_a_property_policy()
    {
        var timestamp = Assert.Throws<ArgumentException>(() =>
            new IyuMainServerOptions().AuthorizeProperty<ConcurrencyRead>(x => x.Version, Policy));
        Assert.Contains("concurrency", timestamp.Message, StringComparison.OrdinalIgnoreCase);
        var check = Assert.Throws<ArgumentException>(() =>
            new IyuMainServerOptions().AuthorizeProperty<ConcurrencyRead>(x => x.Stamp, Policy));
        Assert.Contains("concurrency", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_property_still_takes_a_policy()
    {
        var options = new IyuMainServerOptions().AuthorizeProperty<NavOrderExt>(x => x.Name, Policy);
        Assert.Equal(Policy, options.PropertyPolicies[(typeof(NavOrderExt), nameof(NavOrderExt.Name))]);
    }

    public sealed class AttributedForeignKeyRead : IyuEntity
    {
        public Guid? Owner { get; set; }

        [ForeignKey(nameof(Owner))]
        public NavSecretExt? Holder { get; set; }
    }

    public sealed class ConcurrencyRead : IyuEntity
    {
        [Timestamp]
        public byte[]? Version { get; set; }

        [ConcurrencyCheck]
        public DateTime Stamp { get; set; }
    }
}
