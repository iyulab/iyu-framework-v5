using System.Security.Claims;
using System.Text.Encodings.Web;
using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UBoardEmbed;

// A host that keeps boards for an embedded viewer. There is no sign-in here: every request is made by one fixed development user.
// A real host puts its own authentication and its own rule for who may read or change a board in front of these routes.
var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration["Database"] ?? Path.Combine(AppContext.BaseDirectory, "boards.db");
builder.Services.AddDbContext<BoardsDb>(options => options.UseSqlite($"Data Source={database}"));
builder.Services.AddIyuUBoard<BoardsDb, BoardRow, BoardRevisionRow, BoardPublicationRow>();
builder.Services.AddAuthentication("Dev").AddScheme<AuthenticationSchemeOptions, DevUserHandler>("Dev", null);
builder.Services.AddAuthorization();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BoardsDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// The board routes — make, read, save on a revision, list revisions, publish, withdraw, read what is published and what was
// published before it, list, rename and delete — are the library's: they are served under /api/boards/{id}.
app.MapIyuUBoard("/api");

app.Run();

namespace UBoardEmbed
{
    /// <summary>Signs every request in as one development user.</summary>
    internal sealed class DevUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "dev")], "Dev")), "Dev")));
    }
}
