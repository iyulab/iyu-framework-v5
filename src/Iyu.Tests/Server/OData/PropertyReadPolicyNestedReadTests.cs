using System.Net;
using Iyu.MainServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// Reads of a restricted property that sit inside another option: the options of an <c>$expand</c> (filter, order,
/// compute, apply), the wildcard expand, and the paging token. An independent review of the property read policy named
/// these as shapes the existing tests never sent; each must be refused to a caller who fails the target's policy (or
/// must not depend on a restricted value at all).
/// </summary>
public class PropertyReadPolicyNestedReadTests
{
    private const string CodesRead = "codes.read";
    private const string Orders = "/$data/NavOrders";
    private const string SecretCode = "a-code-behind-the-policy";
    private static readonly Guid OrderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecretId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(CodesRead, p => p.RequireClaim("perm", CodesRead)));
        var dbName = "prop-nested-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>("NavSecrets");
                options.AuthorizeProperty<NavSecretExt>(x => x.Code, CodesRead);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<NavContext>();
        ctx.SecretsExt.Add(new NavSecretExt { Id = SecretId, Code = SecretCode });
        ctx.OrdersExt.Add(new NavOrderExt { Id = OrderId, Name = "an-order", SecretId = SecretId });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static HttpRequestMessage Get(string url, params string[] perms)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        return request;
    }

    public static TheoryData<string> NestedWaysToReadTheCode => new()
    {
        $"{Orders}?$select=Id&$expand=*",
        $"{Orders}?$select=Id&$expand=Secret($select=Id;$filter=Code eq '{SecretCode}')",
        $"{Orders}?$select=Id&$expand=Secret($select=Id;$orderby=Code)",
        $"{Orders}?$select=Id&$expand=Secret($select=Id;$compute=Code as c)",
        $"{Orders}?$select=Id&$expand=Secret($apply=groupby((Code)))",
        $"{Orders}?$select=Id&$expand=Secret($select=Id)&$filter=Secret/Code eq '{SecretCode}'",
        $"{Orders}?$select=Id&$filter=Secret/Code ne null",
        $"{Orders}?$apply=groupby((Secret/Code))",
        $"{Orders}?$apply=groupby((Secret/Code),aggregate($count as n))",
    };

    [Theory]
    [MemberData(nameof(NestedWaysToReadTheCode))]
    public async Task A_caller_who_fails_the_policy_cannot_read_the_code_from_inside_another_option(string url)
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient().SendAsync(Get(url, "something.else"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode}: {body}");
            Assert.DoesNotContain(SecretCode, body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A paging token that names a restricted property must not act as a range on it: the row either comes back (the
    /// token was ignored) or the request is refused, but a token naming a value past the row's must not hide it.
    /// </summary>
    [Fact]
    public async Task A_paging_token_naming_the_restricted_property_does_not_become_a_range_on_it()
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();
            using var response = await http.SendAsync(
                Get($"{Secrets}?$select=Id&$skiptoken=Code:'zzzz',Id:{SecretId}", "something.else"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode != HttpStatusCode.OK || body.Contains(SecretId.ToString(), StringComparison.OrdinalIgnoreCase),
                $"{(int)response.StatusCode}: {body}");
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>An option the server does not interpret is refused outright, not passed through unchecked.</summary>
    [Fact]
    public async Task A_nested_option_the_server_does_not_interpret_is_refused()
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient()
                .SendAsync(Get($"{Orders}?$select=Id&$expand=Secret($levels=1)", "something.else"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(SecretCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    private const string Secrets = "/$data/NavSecrets";
}
