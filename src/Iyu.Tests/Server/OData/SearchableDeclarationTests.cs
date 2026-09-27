using System.Linq.Expressions;
using Iyu.Core.Attributes;
using Iyu.Core.Entities;
using Iyu.Server.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.UriParser;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// <c>$search</c> follows the <c>[Searchable]</c> declaration: a type that declares searched
/// properties is searched across those only, and a type that declares none keeps searching every
/// string property.
/// </summary>
public class SearchableDeclarationTests
{
    public class DeclaredOrder : IyuEntity
    {
        [Searchable] public string OrderNumber { get; set; } = "";
        [Searchable] public string CustomerName { get; set; } = "";
        public string? Memo { get; set; }
        public string? ShareToken { get; set; }
    }

    /// <summary>A read type that inherits its declared properties from a base.</summary>
    public class DerivedOrder : DeclaredOrder
    {
        public string? Extra { get; set; }
    }

    public class NumberOnlyDeclaration : IyuEntity
    {
        [Searchable] public int Seq { get; set; }
        public string Name { get; set; } = "";
    }

    public class Undeclared : IyuEntity
    {
        public string Name { get; set; } = "";
        public string? Memo { get; set; }
    }

    private static Func<T, bool> Bind<T>(string term) where T : IyuEntity
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<T, T>("Items");
        var model = builder.GetEdmModel();
        var context = new QueryBinderContext(model, new ODataQuerySettings(), typeof(T));
        var lambda = Assert.IsAssignableFrom<LambdaExpression>(
            new IyuStringSearchBinder().BindSearch(new SearchClause(new SearchTermNode(term)), context));
        return (Func<T, bool>)lambda.Compile();
    }

    [Fact]
    public void A_declared_type_is_searched_across_its_declared_properties()
    {
        var predicate = Bind<DeclaredOrder>("acme");

        Assert.True(predicate(new DeclaredOrder { CustomerName = "ACME Corp" }));
        Assert.True(predicate(new DeclaredOrder { OrderNumber = "acme-001" }));
    }

    [Fact]
    public void A_declared_type_is_not_searched_across_undeclared_strings()
    {
        var predicate = Bind<DeclaredOrder>("acme");

        // A note naming another customer, and a token that happens to contain the term.
        Assert.False(predicate(new DeclaredOrder { CustomerName = "Other", Memo = "was ACME before" }));
        Assert.False(predicate(new DeclaredOrder { CustomerName = "Other", ShareToken = "x-acme-y" }));
    }

    [Fact]
    public void Inherited_declarations_count()
    {
        var predicate = Bind<DerivedOrder>("acme");

        Assert.True(predicate(new DerivedOrder { CustomerName = "acme" }));
        Assert.False(predicate(new DerivedOrder { CustomerName = "Other", Extra = "acme" }));
    }

    [Fact]
    public void A_type_declaring_nothing_is_searched_across_every_string()
    {
        var predicate = Bind<Undeclared>("acme");

        Assert.True(predicate(new Undeclared { Name = "Other", Memo = "acme" }));
    }

    [Fact]
    public void A_declaration_on_non_strings_only_matches_nothing()
    {
        var predicate = Bind<NumberOnlyDeclaration>("acme");

        Assert.False(predicate(new NumberOnlyDeclaration { Seq = 1, Name = "acme" }));
    }
}
