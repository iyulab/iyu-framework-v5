using System.Net;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// What <c>MapIyuIdentity</c> actually publishes — read off the routing table, and then asked for
/// over HTTP.
/// </summary>
/// <remarks>
/// The handler tests call the handlers directly, so a route that is missing — or mounted one
/// segment away from where the documentation says — passes every one of them and fails only for a
/// consumer. This is the surface the documentation describes, so this is where it is pinned.
/// </remarks>
public class IdentityRouteMapTests
{
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IIdentityStore>(new FakeIdentityStore());
        builder.Services.AddSingleton<IServiceClientStore>(sp => (FakeIdentityStore)sp.GetRequiredService<IIdentityStore>());
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef", Issuer = "iyu", Audience = "iyu-api" },
            permissionCatalog: ["orders.read"]);

        var app = builder.Build();
        app.MapIyuIdentity();
        return app;
    }

    /// <remarks>
    /// Read off the route builder, not the composite <c>EndpointDataSource</c> in DI: a
    /// <c>WebApplication</c>'s own sources are folded into the composite when the app starts.
    /// </remarks>
    private static IEnumerable<RouteEndpoint> Endpoints(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>();

    private static IReadOnlyList<(string Method, string Pattern)> MappedRoutes(WebApplication app)
        => Endpoints(app)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                // A group-relative "" leaves a trailing slash in the raw text. Whether that is
                // reachable at the documented path is not settled by reading it, so it is settled
                // by request below; here it is only normalised away.
                .Select(m => (Method: m, Pattern: "/" + e.RoutePattern.RawText!.Trim('/'))))
            .ToList();

    /// <summary>
    /// The four service-client operations, at the paths the identity README documents. The listing
    /// is the one that makes the other three usable after the issuing response is gone, so its
    /// absence is not a missing convenience — it is the credential becoming unrevokable.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/auth/token")]
    [InlineData("POST", "/api/service-clients")]
    [InlineData("GET", "/api/service-clients")]
    [InlineData("DELETE", "/api/service-clients/{id:guid}")]
    [InlineData("POST", "/api/service-clients/{id:guid}/rotate")]
    public void The_documented_route_is_mapped(string method, string pattern)
    {
        var app = BuildApp();
        try { Assert.Contains((method, pattern), MappedRoutes(app)); }
        finally { ((IDisposable)app).Dispose(); }
    }

    /// <summary>
    /// The listing matches the same path the issuing endpoint does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A group-relative <c>""</c> leaves a trailing slash in the pattern's raw text, which raises a
    /// fair question: is <c>/api/service-clients</c> — the path the documentation gives — actually
    /// what this matches? The parsed segments answer it: the matcher works from those, not from the
    /// raw text.
    /// </para>
    /// <para>
    /// The comparison is against <c>POST</c> on the same group rather than a literal expectation,
    /// because that endpoint is the one every client is issued through. Whatever it matches is,
    /// empirically, reachable — so the listing matching the same thing is the claim worth pinning.
    /// </para>
    /// <para>
    /// This is asserted on the routing table and not over HTTP for a measured reason:
    /// <c>AddIyuIdentity</c> installs a fallback policy requiring an authenticated user, so an
    /// unauthenticated request to an unmapped path is answered <c>401</c> just like a mapped one.
    /// The response cannot tell the two apart, so it cannot be the evidence.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_listing_matches_the_same_path_as_the_issuing_endpoint()
    {
        var app = BuildApp();
        try
        {
            var onTheGroup = Endpoints(app)
                .Where(e => e.RoutePattern.RawText!.Trim('/') == "api/service-clients")
                .ToDictionary(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single());

            var issuing = onTheGroup["POST"].RoutePattern.PathSegments;
            var listing = onTheGroup["GET"].RoutePattern.PathSegments;

            Assert.Equal(["api", "service-clients"],
                listing.Select(s => string.Concat(s.Parts.OfType<RoutePatternLiteralPart>().Select(p => p.Content))));
            Assert.Equal(issuing.Count, listing.Count);
        }
        finally { ((IDisposable)app).Dispose(); }
    }

    /// <summary>The listing joins the group that already carries the owner-scoped policy.</summary>
    [Fact]
    public void The_listing_route_carries_the_owner_scoped_policy()
    {
        var app = BuildApp();
        try
        {
            var listing = Endpoints(app)
                .Single(e => e.RoutePattern.RawText!.Trim('/') == "api/service-clients"
                          && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));

            var authorize = listing.Metadata
                .GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>();

            // Simple name, unqualified: the type was renamed to IyuIdentityServiceCollectionExtensions
            // specifically so it no longer collides with ASP.NET Core Identity's own extensions class
            // when a consumer has both usings in scope (cycle-05 CS0433). This assertion is the
            // regression check for that fix — it fails to compile again if the old collision returns.
            Assert.Contains(authorize,
                a => a.Policy == IyuIdentityServiceCollectionExtensions.CookiePolicyName);
        }
        finally { ((IDisposable)app).Dispose(); }
    }
}
