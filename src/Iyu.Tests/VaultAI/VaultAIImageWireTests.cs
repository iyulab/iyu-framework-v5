using System.Net;
using System.Text.Json.Nodes;
using Iyu.VaultAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// An image passed to the structured call travels as a <c>file</c> content part whose inline content is in
/// <c>blocks</c> — the field the vault-ai server reads. A server that receives the content under any other name treats
/// the part as a reference to an upload it does not have, so the image never reaches the model.
/// </summary>
public sealed class VaultAIImageWireTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _url = default!;
    private JsonNode? _received;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapPost("/api/agents/{id}/messages", async (HttpContext ctx) =>
        {
            _received = await JsonNode.ParseAsync(ctx.Request.Body);
            return Results.Json(new
            {
                message = new { content = new[] { new { type = "text", text = "ok" } } },
                output = new { answer = "ok" },
            });
        });
        await _app.StartAsync();
        _url = _app.Urls.Single();
    }

    public Task DisposeAsync() => _app.DisposeAsync().AsTask();

    [Fact]
    public async Task An_image_is_sent_as_a_file_part_with_its_content_in_blocks()
    {
        var client = new VaultAIClient(Options.Create(new VaultAISettings { Url = _url, Token = "t" }));
        var bytes = new byte[] { 1, 2, 3, 4 };

        await client.GetStructuredMessageAsync(
            Guid.NewGuid(), "read it", JsonNode.Parse("""{"type":"object"}""")!,
            [new VaultAIImage("image/png", bytes, "scan.png")]);

        var file = _received!["messages"]![0]!["content"]!.AsArray().Single(p => (string?)p!["type"] == "file")!;
        Assert.Null(file["data"]);
        var block = Assert.Single(file["blocks"]!.AsArray())!;
        Assert.Equal("image", (string?)block["type"]);
        Assert.Equal("image/png", (string?)block["mimeType"]);
        Assert.Equal(Convert.ToBase64String(bytes), (string?)block["data"]);
        Assert.Equal("scan.png", (string?)file["fileName"]);
        Assert.Equal("image/png", (string?)file["contentType"]);
        Assert.Equal(bytes.LongLength, (long?)file["contentSize"]);
    }
}
