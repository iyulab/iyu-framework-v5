using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// An <c>$expand</c> that names a navigation the type does not have is refused in words a caller can act on: what they wrote,
/// and which navigation properties the type does have. The refusal used to say only that the expression could not be
/// interpreted and that nested options are not accepted — which reads as a ban on a nested option rather than as a typo.
/// </summary>
public sealed class ExpandRefusalWordsEndToEndTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "expand-words-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<RefContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(RefOrdersController).Assembly);
                options.ODataModel.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
                options.ODataModel.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
                // The navigation check is attached to a host that declares a read policy, or asks for the reach.
                options.ComputeODataReach = true;
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string? Code, string Message)> GetAsync(WebApplication app, string path)
    {
        using var response = await app.GetTestClient().GetAsync(path);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        return (response.StatusCode, error.GetProperty("code").GetString(), error.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task A_misspelled_navigation_is_named_and_the_real_ones_are_listed()
    {
        await using var app = await StartAsync();

        var (status, code, message) = await GetAsync(app, "/$data/RefOrders?$expand=Custmer");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("InvalidQuery", code);
        Assert.Contains("Custmer", message, StringComparison.Ordinal);
        Assert.Contains("Customer", message[(message.IndexOf("are:", StringComparison.Ordinal))..], StringComparison.Ordinal);
        Assert.DoesNotContain("without a nested option", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_without_navigations_says_so()
    {
        await using var app = await StartAsync();

        var (_, _, message) = await GetAsync(app, "/$data/RefCustomers?$expand=Nope");

        Assert.Contains("Nope", message, StringComparison.Ordinal);
        Assert.Contains("has no navigation properties", message, StringComparison.Ordinal);
    }

    /// <summary>A wrong name inside a nested expand belongs to another type; the addressed type's navigations are not offered for it.</summary>
    [Fact]
    public async Task A_name_wrong_inside_a_nested_expand_does_not_list_the_outer_types_navigations()
    {
        await using var app = await StartAsync();

        var (status, _, message) = await GetAsync(app, "/$data/RefOrders?$expand=Customer($expand=Nope)");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Nope", message, StringComparison.Ordinal);
        Assert.DoesNotContain("The navigation properties of", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_misspelled_property_in_a_filter_or_an_order_is_met_with_the_closest_names()
    {
        await using var app = await StartAsync();

        var (_, _, inFilter) = await GetAsync(app, "/$data/RefCustomers?$filter=Nmae eq 'a'");
        var (_, _, inOrder) = await GetAsync(app, "/$data/RefOrders?$orderby=Nots");

        Assert.Contains("Nmae", inFilter, StringComparison.Ordinal);
        Assert.Contains("Did you mean: Name", inFilter, StringComparison.Ordinal);
        Assert.Contains("Did you mean: Note", inOrder, StringComparison.Ordinal);
    }

    /// <summary>Nothing close, nothing offered — a list of every column would say less than the parser already did.</summary>
    [Fact]
    public async Task A_name_far_from_every_property_is_given_no_suggestion()
    {
        await using var app = await StartAsync();

        var (_, _, message) = await GetAsync(app, "/$data/RefCustomers?$filter=Zzzzzzzzzzzz eq 'a'");

        Assert.Contains("Zzzzzzzzzzzz", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correct_expand_is_not_refused()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().GetAsync("/$data/RefOrders?$expand=Customer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
