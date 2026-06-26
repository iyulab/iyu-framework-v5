using System.Linq.Expressions;
using Iyu.Core.Entities;
using Iyu.Server.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.UriParser;
using Xunit;

namespace Iyu.Tests.Server.OData;

public class IyuStringSearchBinderTests
{
    public class Widget : IyuEntity
    {
        public string Name { get; set; } = "";
        public string? Code { get; set; }
        public int Quantity { get; set; }
    }

    private static Func<Widget, bool> BindPredicate(string term)
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Widget, Widget>("Widgets");
        var model = builder.GetEdmModel();

        var context = new QueryBinderContext(model, new ODataQuerySettings(), typeof(Widget));
        var clause = new SearchClause(new SearchTermNode(term));

        var lambda = Assert.IsAssignableFrom<LambdaExpression>(
            new IyuStringSearchBinder().BindSearch(clause, context));
        return (Func<Widget, bool>)lambda.Compile();
    }

    [Fact]
    public void Matches_any_string_property_case_insensitively()
    {
        var predicate = BindPredicate("abc");

        Assert.True(predicate(new Widget { Name = "xxABCxx" }));          // Name, case-insensitive
        Assert.True(predicate(new Widget { Name = "", Code = "abc-1" })); // a different string prop
        Assert.False(predicate(new Widget { Name = "zzz", Code = "yyy" }));
    }

    [Fact]
    public void Null_string_property_does_not_throw()
    {
        var predicate = BindPredicate("abc");
        Assert.False(predicate(new Widget { Name = "zzz", Code = null }));
        Assert.True(predicate(new Widget { Name = "ABChere", Code = null }));
    }

    [Fact]
    public void Does_not_match_non_string_properties()
    {
        // The term "5" must not match the integer Quantity (only string props are searched).
        var predicate = BindPredicate("5");
        Assert.False(predicate(new Widget { Name = "n", Code = "c", Quantity = 5 }));
    }
}
