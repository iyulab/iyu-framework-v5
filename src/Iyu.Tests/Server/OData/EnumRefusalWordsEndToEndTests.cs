using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Server.OData;

public enum WordsStatus { draft, open, closed }

public sealed class WordsTicket : IyuEntity
{
    public string Title { get; set; } = "";
    public WordsStatus Status { get; set; }
}

public sealed class WordsTicketExt : IyuEntity
{
    public string Title { get; set; } = "";
    public WordsStatus Status { get; set; }
    public WordsStatus? Previous { get; set; }
}

public sealed class WordsContext(DbContextOptions<WordsContext> options) : IyuDbContext(options)
{
    public DbSet<WordsTicket> Tickets => Set<WordsTicket>();
    public DbSet<WordsTicketExt> TicketsExt => Set<WordsTicketExt>();
}

public sealed class WordsTicketsController(WordsContext ctx) : IyuODataController<WordsTicketExt, WordsTicket>(ctx);

/// <summary>
/// A text that is not a member of an enumeration is refused with the members it could have been — when what the caller wrote makes
/// the property certain, and with nothing extra when it does not (no guess about which property).
/// </summary>
public sealed class EnumRefusalWordsEndToEndTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<WordsContext>(
            configureDb: db => db.UseInMemoryDatabase("enum-words-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(WordsTicketsController).Assembly);
                options.ODataModel.AddEntityPair<WordsTicketExt, WordsTicket>("WordsTickets");
                options.ComputeODataReach = true;
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Message)> GetAsync(WebApplication app, string query)
    {
        using var response = await app.GetTestClient().GetAsync("/$data/WordsTickets?" + query);
        if (response.IsSuccessStatusCode) return (response.StatusCode, "");
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        return (response.StatusCode, error.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task A_text_that_is_not_a_member_is_met_with_the_members_of_the_property_it_was_compared_to()
    {
        await using var app = await StartAsync();

        var (status, message) = await GetAsync(app, "$filter=Status eq 'Custom'");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'Custom'", message, StringComparison.Ordinal);
        Assert.Contains("The values of 'Status' are: closed, draft, open.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_text_on_the_left_is_read_the_same_way()
    {
        await using var app = await StartAsync();

        var (_, message) = await GetAsync(app, "$filter='Custom' eq Status");

        Assert.Contains("The values of 'Status' are:", message, StringComparison.Ordinal);
    }

    /// <summary>The same bad text compared to two enumeration properties: which one was meant is not known, so none is guessed.</summary>
    [Fact]
    public async Task When_the_text_is_compared_to_two_properties_no_property_is_guessed()
    {
        await using var app = await StartAsync();

        var (status, message) = await GetAsync(app, "$filter=Status eq 'Custom' or Previous eq 'Custom'");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'Custom'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("The values of", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_is_not_refused()
    {
        await using var app = await StartAsync();

        var (status, _) = await GetAsync(app, "$filter=Status eq 'open'");

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
