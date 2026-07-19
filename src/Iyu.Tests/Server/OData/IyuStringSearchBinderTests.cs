using System.Linq.Expressions;
using Iyu.Core.Entities;
using Iyu.Server.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.Edm;
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

    private static IEdmModel BuildModel()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Widget, Widget>("Widgets");
        return builder.GetEdmModel();
    }

    private static Func<Widget, bool> Compile(SearchClause clause, IEdmModel model)
    {
        var context = new QueryBinderContext(model, new ODataQuerySettings(), typeof(Widget));
        var lambda = Assert.IsAssignableFrom<LambdaExpression>(
            new IyuStringSearchBinder().BindSearch(clause, context));
        return (Func<Widget, bool>)lambda.Compile();
    }

    private static Func<Widget, bool> BindPredicate(string term)
        => Compile(new SearchClause(new SearchTermNode(term)), BuildModel());

    /// <summary>
    /// Binds the search expression exactly as the runtime sees it — through the real
    /// OData parser — so the node shape under test is the one produced in production.
    /// </summary>
    private static Func<Widget, bool> BindParsed(string search)
    {
        var model = BuildModel();
        var parser = new ODataUriParser(model, new Uri($"Widgets?$search={Uri.EscapeDataString(search)}", UriKind.Relative));
        return Compile(parser.ParseSearch(), model);
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

    [Fact]
    public void And_requires_every_term_but_they_may_sit_in_different_properties()
    {
        // The regression this decomposition fixes: multi-word search used to bind to a
        // constant false, so a row containing both words returned nothing (HTTP 200, 0 rows).
        var predicate = BindParsed("\"세계\" AND \"골프\"");

        Assert.True(predicate(new Widget { Name = "세계의 골프코스" }));       // both, one property
        Assert.True(predicate(new Widget { Name = "세계지도", Code = "골프-1" })); // both, split across properties
        Assert.False(predicate(new Widget { Name = "세계지도" }));             // only one term
        Assert.False(predicate(new Widget { Name = "골프장" }));
    }

    [Fact]
    public void Space_separated_words_parse_as_an_implicit_and()
    {
        var predicate = BindParsed("한국풍경 와이드");

        Assert.True(predicate(new Widget { Name = "한국풍경 와이드숫자판" }));
        Assert.False(predicate(new Widget { Name = "한국풍경 롱" }));
    }

    [Fact]
    public void Or_matches_either_term()
    {
        var predicate = BindParsed("\"abc\" OR \"xyz\"");

        Assert.True(predicate(new Widget { Name = "xxABCxx" }));
        Assert.True(predicate(new Widget { Name = "n", Code = "XYZ" }));
        Assert.False(predicate(new Widget { Name = "n", Code = "c" }));
    }

    [Fact]
    public void Not_excludes_matching_rows()
    {
        var predicate = BindParsed("\"abc\" AND NOT \"draft\"");

        Assert.True(predicate(new Widget { Name = "abc final" }));
        Assert.False(predicate(new Widget { Name = "abc draft" }));
    }

    [Fact]
    public void Nested_groups_bind_as_written()
    {
        var predicate = BindParsed("(\"abc\" OR \"xyz\") AND \"tail\"");

        Assert.True(predicate(new Widget { Name = "abc", Code = "tail" }));
        Assert.True(predicate(new Widget { Name = "xyz tail" }));
        Assert.False(predicate(new Widget { Name = "abc" }));      // missing "tail"
        Assert.False(predicate(new Widget { Name = "tail only" })); // neither branch of the OR
    }

    [Fact]
    public void Unsupported_node_matches_nothing_rather_than_everything()
    {
        // Not reachable through the OData 4.01 $search grammar; asserts the conservative
        // fallback if a future node kind appears.
        var model = BuildModel();
        var clause = new SearchClause(new ConstantNode(true));

        Assert.False(Compile(clause, model)(new Widget { Name = "anything" }));
    }
}
