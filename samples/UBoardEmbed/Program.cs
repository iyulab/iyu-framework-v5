using System.Text.Json;
using System.Text.Json.Nodes;
using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using UBoardEmbed;

// A host that keeps boards for an embedded viewer. There is no sign-in here: every request is made by one fixed development user.
// A real host puts its own authentication and its own rule for who may read or change a board in front of these routes.
const string DevUser = "dev";

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration["Database"] ?? Path.Combine(AppContext.BaseDirectory, "boards.db");
builder.Services.AddDbContext<BoardsDb>(options => options.UseSqlite($"Data Source={database}"));
builder.Services.AddIyuUBoard<BoardsDb, BoardRow, BoardRevisionRow, BoardPublicationRow>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BoardsDb>().Database.EnsureCreated();

// A value the service refuses (a blank id, a document that is not JSON) is the caller's mistake, not the server's.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ArgumentException refused)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = refused.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var boards = app.MapGroup("/api/boards/{id}");

// A board as a page reads it: the document is sent as the JSON it is, not as a string of JSON.
static JsonObject Envelope(BoardRecord board) => new()
{
    ["id"] = board.Id,
    ["name"] = board.Name,
    ["revision"] = board.CurrentRevision,
    ["publishedRevision"] = board.PublishedRevision,
    ["document"] = JsonNode.Parse(board.Document),
};

static async Task<string> Body(HttpRequest request) => await new StreamReader(request.Body).ReadToEndAsync();

boards.MapPost("", async (string id, string name, HttpRequest request, IyuBoardService service) =>
{
    var made = await service.CreateAsync(id, name, await Body(request), DevUser);
    return made.Succeeded
        ? Results.Json(Envelope(made.Board!), statusCode: 201)
        : Results.Json(Envelope(made.Board!), statusCode: 409);
});

boards.MapGet("", async (string id, IyuBoardService service) =>
    await service.GetAsync(id) is { } board ? Results.Json(Envelope(board)) : Results.NotFound());

// A save names the revision it worked from. A session that is behind is told so, with the board as it now stands.
boards.MapPut("", async (string id, int expected, HttpRequest request, IyuBoardService service) =>
{
    var change = await service.SaveAsync(id, await Body(request), expected, DevUser);
    return change.Status switch
    {
        BoardChangeStatus.Done => Results.Json(Envelope(change.Board!)),
        BoardChangeStatus.Conflict => Results.Json(Envelope(change.Board!), statusCode: 409),
        _ => Results.NotFound(),
    };
});

boards.MapGet("/revisions", async (string id, IyuBoardService service) =>
    Results.Json((await service.ListRevisionsAsync(id)).Select(r => new { r.Revision, r.CreatedAt, r.CreatedBy })));

boards.MapGet("/revisions/{revision:int}", async (string id, int revision, IyuBoardService service) =>
    await service.GetRevisionAsync(id, revision) is { } found
        ? Results.Json(new JsonObject
        {
            ["revision"] = found.Revision,
            ["createdAt"] = found.CreatedAt,
            ["createdBy"] = found.CreatedBy,
            ["document"] = JsonNode.Parse(found.Document),
        })
        : Results.NotFound());

boards.MapPost("/publish", async (string id, int revision, IyuBoardService service) =>
    await service.PublishAsync(id, revision, DevUser) is { } line ? Results.Json(line) : Results.NotFound());

boards.MapPost("/withdraw", async (string id, IyuBoardService service) =>
    await service.WithdrawAsync(id, DevUser) is { } line ? Results.Json(line) : Results.NotFound());

boards.MapGet("/publications", async (string id, IyuBoardService service) =>
    Results.Json(await service.ListPublicationsAsync(id)));

// What readers see: the revision the board is publishing. Nothing published is an ordinary answer, not an error.
boards.MapGet("/published", async (string id, IyuBoardService service) =>
    await service.GetPublishedAsync(id) is { } published
        ? Results.Json(new JsonObject { ["revision"] = published.Revision, ["document"] = JsonNode.Parse(published.Document) })
        : Results.NoContent());

// What a reader falls back through when the publication in force is one its viewer refuses: the revisions published before it,
// newest first, each once. The check that says which one the viewer accepts runs where the viewer runs, so the list goes to the page.
boards.MapGet("/fallback", async (string id, IyuBoardService service) =>
    Results.Json((await service.ListEarlierPublicationsAsync(id)).Select(found => new JsonObject
    {
        ["revision"] = found.Revision,
        ["document"] = JsonNode.Parse(found.Document),
    })));

app.Run();
