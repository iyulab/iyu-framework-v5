using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using static Iyu.Tests.Server.OData.ExpandAuthorizationEndToEndTests;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// A set's read policy holds for every way a request can reach the set through a navigation — not only
/// <c>$expand</c>. Grouping by a protected set's value prints it, filtering on it with a count tests it one guess at
/// a time, and sorting by it orders the answer by it: each is reading the set, and each is refused to a caller who
/// may not read it. Same host as <see cref="ExpandAuthorizationEndToEndTests"/>: <c>NavOrders</c> is open, and
/// references <c>NavSecrets</c>, which needs <c>secrets.read</c>.
/// </summary>
public class NavigationPathAuthorizationEndToEndTests
{
    private const string Orders = "/$data/" + OrdersSet;

    /// <summary>Every request shape that reaches <c>NavSecrets</c> through the <c>Secret</c> navigation without expanding it.</summary>
    public static TheoryData<string> PathsIntoTheProtectedSet => new()
    {
        // Printed: the protected values are in the response body.
        $"{Orders}?$apply=groupby((Secret/Code))",
        $"{Orders}?$apply=groupby((Secret/Code),aggregate($count as n))",
        $"{Orders}?$apply=aggregate(Secret/Code with max as m)",
        $"{Orders}?$compute=Secret/Code as c&$select=Name,c",
        $"{Orders}?$apply=compute(Secret/Code as c)/groupby((c))",
        // Tested: the answer says whether a guess is right.
        $"{Orders}/$count?$filter=Secret/Code eq '{SecretCode}'",
        $"{Orders}?$filter=startswith(Secret/Code,'the-')",
        $"{Orders}?$filter=Secret ne null",
        $"{Orders}?$apply=filter(Secret/Code eq '{SecretCode}')/aggregate($count as n)",
        // Ordered: the sequence follows the protected values.
        $"{Orders}?$orderby=Secret/Code desc",
        // The same options without the dollar, which the pipeline applies just the same.
        $"{Orders}?apply=groupby((Secret/Code))",
        $"{Orders}/$count?filter=Secret/Code eq '{SecretCode}'",
        $"{Orders}?orderby=Secret/Code",
        // And in another letter case.
        $"{Orders}?$Filter=secret/code eq '{SecretCode}'",
    };

    [Theory]
    [MemberData(nameof(PathsIntoTheProtectedSet))]
    public async Task A_caller_who_may_not_read_the_set_cannot_reach_it_through_a_path(string url)
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();

            using var signedIn = await http.SendAsync(Request(url, perm: "something.else"));
            Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);
            Assert.DoesNotContain(SecretCode, await signedIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            // A host that words its own refusals is handed the policy that failed, and the set it guards.
            Assert.Equal($"{SecretReadPolicy}|{SecretsSet}", signedIn.Headers.GetValues(FailedPolicyHeader).Single());

            using var anonymous = await http.SendAsync(Request(url));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The other direction, which only a consumer notices: a caller holding the policy must still get every one of
    /// those answers. A check that parses a query the pipeline accepts and refuses it is as wrong as one that lets a
    /// protected value through.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathsIntoTheProtectedSet))]
    public async Task A_caller_who_may_read_the_set_reaches_it_as_before(string url)
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().SendAsync(Request(url, perm: SecretReadPolicy));

            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>What grouping by a protected value answers a caller who may read it — the value itself, which is the leak.</summary>
    [Fact]
    public async Task Grouping_by_a_protected_value_prints_it_for_the_caller_who_may_read_it_and_only_for_them()
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();
            var url = $"{Orders}?$apply=groupby((Secret/Code),aggregate($count as n))";

            using var permitted = await http.SendAsync(Request(url, perm: SecretReadPolicy));
            Assert.Contains(SecretCode, await permitted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var refused = await http.SendAsync(Request(url, perm: "something.else"));
            Assert.DoesNotContain(SecretCode, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>Queries that name no navigation are nobody's business but the addressed set's — open, here.</summary>
    [Theory]
    [InlineData("?$filter=Name eq 'an order'")]
    [InlineData("?$orderby=Name desc&$top=1")]
    [InlineData("?$select=Name")]
    [InlineData("?$apply=groupby((Name),aggregate($count as n))")]
    // An $orderby after an $apply sorts by the alias the apply introduced — the options are read together.
    [InlineData("?$apply=groupby((Name),aggregate($count as n))&$orderby=n desc")]
    [InlineData("?$compute=length(Name) as l&$select=Name,l&$orderby=l")]
    [InlineData("/$count?$filter=contains(Name,'order')")]
    [InlineData("?$filter=SecretId ne null")]                      // the order's own column, not the secret's
    public async Task A_query_that_names_no_navigation_is_not_affected(string query)
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().SendAsync(Request(Orders + query));

            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The pipeline reads an option with or without the dollar, and applies one of the two when both are given. The
    /// check does not guess which: the one it did not look at would be the way through.
    /// </summary>
    [Theory]
    [InlineData("?$filter=Name eq 'an order'&filter=Secret/Code eq 'x'")]
    [InlineData("?filter=Secret/Code eq 'x'&$filter=Name eq 'an order'")]
    [InlineData("?$apply=groupby((Name))&apply=groupby((Secret/Code))")]
    public async Task An_option_given_under_both_spellings_is_refused_whoever_asks(string query)
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().SendAsync(Request(Orders + query, perm: SecretReadPolicy));
            var body = await resp.Content.ReadAsStringAsync();

            Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"{(int)resp.StatusCode}: {body}");
            Assert.Contains("InvalidQuery", body, StringComparison.Ordinal);
            Assert.True(body.Contains("more than once", StringComparison.Ordinal), body);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// Repeated under one spelling, an option is one value to the pipeline — the repeats joined with commas — and that
    /// joined value is what is checked: a path in the second repeat is a path in the query.
    /// </summary>
    [Fact]
    public async Task An_option_repeated_under_one_spelling_is_checked_as_the_pipeline_reads_it()
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();
            var url = $"{Orders}?$orderby=Name&$ORDERBY=Secret/Code";

            using var refused = await http.SendAsync(Request(url, perm: "something.else"));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            using var permitted = await http.SendAsync(Request(url, perm: SecretReadPolicy));
            Assert.True(permitted.StatusCode == HttpStatusCode.OK, $"{(int)permitted.StatusCode}: {await permitted.Content.ReadAsStringAsync()}");
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A parameter alias stands for an expression, and the expression may be a path.</summary>
    [Fact]
    public async Task A_path_hidden_behind_a_parameter_alias_does_not_get_through()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().SendAsync(
                Request($"{Orders}/$count?$filter=@p eq '{SecretCode}'&@p=Secret/Code", perm: "something.else"));

            // Forbidden where the pipeline resolves such an alias, a query error where it does not — never an answer.
            Assert.Contains(resp.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.BadRequest });
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A query the pipeline would refuse anyway is still a query error, in the pipeline's words.</summary>
    [Fact]
    public async Task A_malformed_filter_is_still_a_query_error()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().SendAsync(Request($"{Orders}?$filter=NoSuchColumn eq 1"));
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("InvalidQuery", body, StringComparison.Ordinal);
            Assert.Contains("NoSuchColumn", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
