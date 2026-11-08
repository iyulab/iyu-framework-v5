using System.Net;
using System.Net.Http.Json;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// A property read policy holds on every way the OData read surface can print, test or sort by the property — not only
/// <c>$select</c>. <c>NavOrders.Name</c> needs <c>names.read</c> and <c>NavSecrets.Code</c> needs <c>codes.read</c>; the
/// sets themselves are open. A caller who fails a property's policy is refused, never handed a blank, because a blank
/// is indistinguishable from "no value" and still lets <c>$filter</c> recover the real one.
/// </summary>
public class PropertyReadPolicyEndToEndTests
{
    private const string Orders = "/$data/NavOrders";
    private const string NamesRead = "names.read";
    private const string CodesRead = "codes.read";
    private const string FailedPolicyHeader = "X-Failed-Policy";
    private const string OrderName = "a-name-behind-the-policy";
    private static readonly Guid OrderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecretId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(NamesRead, p => p.RequireClaim("perm", NamesRead));
            o.AddPolicy(CodesRead, p => p.RequireClaim("perm", CodesRead));
        });
        var dbName = "prop-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>("NavSecrets");
                options.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead);
                options.AuthorizeProperty<NavSecretExt>(x => x.Code, CodesRead);
            });

        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                if (http.Features.Get<IIyuAuthorizationFailureFeature>() is { } failure)
                    http.Response.Headers[FailedPolicyHeader] = $"{failure.Policy}|{failure.EntitySet}|{failure.Property}";
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<NavContext>();
        ctx.SecretsExt.Add(new NavSecretExt { Id = SecretId, Code = "a-code" });
        ctx.OrdersExt.Add(new NavOrderExt { Id = OrderId, Name = OrderName, SecretId = SecretId });
        // The write type's own table: an update loads the row it changes from there, not from the read type's.
        ctx.Orders.Add(new NavOrder { Id = OrderId, Name = OrderName, SecretId = SecretId });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static HttpRequestMessage Get(string url, params string[] perms)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        return request;
    }

    /// <summary>Every way a request can print, test, order by or aggregate the restricted name — or print all of it.</summary>
    public static TheoryData<string> WaysToReadTheName => new()
    {
        // Printed.
        $"{Orders}?$select=Name",
        $"{Orders}?$select=Id,Name",
        $"{Orders}?$select=*",
        $"{Orders}",                                       // no $select: every property is printed
        $"{Orders}?$top=1",                                // still every property
        $"{Orders}({OrderId})",                            // by key, the same
        $"{Orders}?$expand=Secret&$select=Name",
        $"{Orders}?$compute=Name as n&$select=Id,n",
        $"{Orders}?$apply=groupby((Name))",
        $"{Orders}?$apply=aggregate(Name with max as m)",
        // Tested: the answer says whether a guess is right.
        $"{Orders}?$filter=Name eq '{OrderName}'&$select=Id",
        $"{Orders}/$count?$filter=startswith(Name,'a-')",
        $"{Orders}?$apply=filter(Name eq '{OrderName}')/aggregate($count as n)",
        // Ordered: the sequence follows the restricted values.
        $"{Orders}?$orderby=Name&$select=Id",
        // The same options without the dollar, and in another letter case.
        $"{Orders}?select=Name",
        $"{Orders}/$count?filter=Name eq '{OrderName}'",
        $"{Orders}?$Filter=name eq '{OrderName}'&$select=Id",
    };

    [Theory]
    [MemberData(nameof(WaysToReadTheName))]
    public async Task A_caller_who_fails_the_policy_cannot_read_the_property_any_way(string url)
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();

            using var signedIn = await http.SendAsync(Get(url, "something.else"));
            Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);
            Assert.DoesNotContain(OrderName, await signedIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            // A host that words its own refusals is told which policy failed, on which set, for which property.
            Assert.Equal($"{NamesRead}|NavOrders|Name", signedIn.Headers.GetValues(FailedPolicyHeader).Single());

            using var anonymous = await http.SendAsync(Get(url));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The other direction, which only a consumer notices: a caller who holds every policy gets all of it as before.</summary>
    [Theory]
    [MemberData(nameof(WaysToReadTheName))]
    public async Task A_caller_who_holds_the_policies_reads_as_before(string url)
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient().SendAsync(Get(url, NamesRead, CodesRead));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>Shapes that never print, test or sort by the restricted properties: nobody's business but the set's own.</summary>
    [Theory]
    [InlineData("?$select=Id")]
    [InlineData("?$select=Id,SecretId")]
    [InlineData("/$count")]
    [InlineData("?$apply=aggregate($count as n)")]
    [InlineData("?$filter=SecretId ne null&$select=Id")]
    [InlineData("?$orderby=SecretId&$select=Id")]
    public async Task A_caller_who_fails_the_policy_still_reads_what_the_policy_does_not_guard(string query)
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient().SendAsync(Get(Orders + query, "something.else"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
            Assert.DoesNotContain(OrderName, body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A property of the set an expand reaches is guarded the same way, by what the expand prints.</summary>
    [Theory]
    [InlineData("?$select=Id&$expand=Secret", HttpStatusCode.Forbidden)]                  // every property of the target
    [InlineData("?$select=Id&$expand=Secret($select=Code)", HttpStatusCode.Forbidden)]
    [InlineData("?$select=Id&$filter=Secret/Code eq 'a-code'", HttpStatusCode.Forbidden)]
    [InlineData("?$select=Id&$orderby=Secret/Code", HttpStatusCode.Forbidden)]
    [InlineData("?$select=Id&$expand=Secret($select=Id)", HttpStatusCode.OK)]
    public async Task A_property_of_the_set_an_expand_reaches_is_guarded_by_what_the_expand_prints(
        string query, HttpStatusCode expected)
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient().SendAsync(Get(Orders + query, "something.else"));
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Forbidden)
                Assert.Equal($"{CodesRead}|NavSecrets|Code", response.Headers.GetValues(FailedPolicyHeader).Single());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_holds_one_of_two_policies_is_refused_the_other()
    {
        var app = await StartAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient()
                .SendAsync(Get($"{Orders}?$expand=Secret", NamesRead));
            // Prints the name (held) and the target's code (not held).
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal($"{CodesRead}|NavSecrets|Code", response.Headers.GetValues(FailedPolicyHeader).Single());
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A write answers with the row it created: a read of the entity. A caller who fails a property's policy gets the
    /// Location and no body; one who holds the policies gets the row.
    /// </summary>
    [Fact]
    public async Task A_write_does_not_echo_the_row_to_a_caller_who_fails_a_property_policy()
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();

            var withheld = new HttpRequestMessage(HttpMethod.Post, Orders) { Content = JsonContent.Create(new { Name = "posted-name" }) };
            withheld.Headers.Add("X-Test-Perm", "something.else");
            using var withheldResponse = await http.SendAsync(withheld);
            Assert.Equal(HttpStatusCode.Created, withheldResponse.StatusCode);
            Assert.NotNull(withheldResponse.Headers.Location);
            Assert.Equal("", await withheldResponse.Content.ReadAsStringAsync());

            var echoed = new HttpRequestMessage(HttpMethod.Post, Orders) { Content = JsonContent.Create(new { Name = "posted-name" }) };
            echoed.Headers.Add("X-Test-Perm", NamesRead);
            using var echoedResponse = await http.SendAsync(echoed);
            Assert.Equal(HttpStatusCode.Created, echoedResponse.StatusCode);
            Assert.Contains("posted-name", await echoedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The one place a write answers with the row is the create. The guard on that echo rests on it: an update answers
    /// <c>204</c> with no body and there is no replace verb, so a caller who fails a property's policy gets no row from
    /// either. If a write ever starts answering with the row, this test fails first.
    /// </summary>
    [Fact]
    public async Task Only_a_create_answers_with_the_row_an_update_and_a_replace_do_not()
    {
        var app = await StartAsync();
        try
        {
            var http = app.GetTestServer().CreateClient();

            var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Orders}({OrderId})")
            {
                Content = JsonContent.Create(new { Name = "renamed-by-patch" }),
            };
            patch.Headers.Add("X-Test-Perm", "something.else");
            using var patched = await http.SendAsync(patch);
            Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
            Assert.Equal("", await patched.Content.ReadAsStringAsync());

            var put = new HttpRequestMessage(HttpMethod.Put, $"{Orders}({OrderId})")
            {
                Content = JsonContent.Create(new { Name = "replaced" }),
            };
            put.Headers.Add("X-Test-Perm", "something.else");
            using var replaced = await http.SendAsync(put);
            Assert.True(
                replaced.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"a replace verb answered {(int)replaced.StatusCode}");
            Assert.DoesNotContain("renamed-by-patch", await replaced.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    // ---------- declaration: refused when the host is built, never quietly doing nothing ----------

    [Fact]
    public void A_property_the_type_does_not_have_is_refused_where_it_is_written()
    {
        var options = new IyuMainServerOptions();
        Assert.Throws<ArgumentException>(() => options.AuthorizeProperty(typeof(NavOrderExt), "NoSuchProperty", NamesRead));
    }

    [Fact]
    public void A_second_declaration_for_one_property_is_refused()
    {
        var options = new IyuMainServerOptions().AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead);
        Assert.Throws<InvalidOperationException>(() => options.AuthorizeProperty<NavOrderExt>(x => x.Name, CodesRead));
    }

    [Fact]
    public void A_policy_for_a_type_no_surface_serves_stops_the_host()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIyuMainServer<NavContext>(
                db => db.UseInMemoryDatabase("x"),
                options => options.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead)));
        Assert.Contains("no OData set serves", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_for_a_property_removed_with_Exclude_stops_the_host()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIyuMainServer<NavContext>(
                db => db.UseInMemoryDatabase("x"),
                options =>
                {
                    options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                    options.ODataModel.Exclude<NavOrderExt>(x => x.Name);
                    options.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead);
                }));
        Assert.Contains("Exclude", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A surface that serves the type and does not enforce property policies is a hole — refused, not ignored.</summary>
    [Fact]
    public void A_policy_for_a_type_another_surface_also_serves_stops_the_host()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIyuMainServer<NavContext>(
                db => db.UseInMemoryDatabase("x"),
                options =>
                {
                    options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                    options.Surface<ServesEveryType>();
                    options.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead);
                }));
        Assert.Contains("does not enforce property policies yet", error.Message, StringComparison.Ordinal);
    }

    private sealed class ServesEveryType : IIyuMainServerSurface
    {
        public bool Serves(Type readType) => true;
        public void ConfigureServices(IServiceCollection services, IyuMainServerOptions options) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
    }
}
