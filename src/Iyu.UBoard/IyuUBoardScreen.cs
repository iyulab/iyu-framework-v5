using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iyu.UBoard;

/// <summary>A file of the built screen.</summary>
/// <param name="Content">Its bytes.</param>
/// <param name="ContentType">The media type it is served as.</param>
internal sealed record ScreenAsset(byte[] Content, string ContentType);

/// <summary>
/// Where the screen's files come from. In the package they are embedded resources; a test hands in its own, or none. A path is the file's
/// place in the built screen — <c>index.html</c>, <c>assets/x.js</c>, <c>THIRD-PARTY-NOTICES.txt</c> — and is looked up as written, so no
/// path can reach anything that is not one of those files.
/// </summary>
internal interface IScreenAssets
{
    /// <summary>Whether this build carries a screen at all.</summary>
    bool Available { get; }

    /// <summary>The file at <paramref name="path"/>, or null.</summary>
    ScreenAsset? Find(string path);
}

/// <summary>The screen as the package carries it: the resources named <c>uboard-web/…</c>, written by the build.</summary>
internal sealed class EmbeddedScreenAssets : IScreenAssets
{
    private const string Root = "uboard-web/";

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private readonly Dictionary<string, string> _resources;
    private readonly ConcurrentDictionary<string, ScreenAsset> _read = new(StringComparer.Ordinal);

    public EmbeddedScreenAssets()
    {
        _resources = typeof(EmbeddedScreenAssets).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Root, StringComparison.Ordinal))
            .ToDictionary(name => name[Root.Length..], name => name, StringComparer.Ordinal);
    }

    public bool Available => _resources.ContainsKey("index.html");

    public ScreenAsset? Find(string path)
    {
        if (!_resources.TryGetValue(path, out var resource)) return null;
        if (!MediaTypes.TryGetValue(Path.GetExtension(path), out var type)) return null; // a kind of file the screen does not have is not served
        return _read.GetOrAdd(path, _ =>
        {
            using var stream = typeof(EmbeddedScreenAssets).Assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new ScreenAsset(buffer.ToArray(), type);
        });
    }
}

/// <summary>Says at the host's start, in its log, that the screen is switched on and this build carries none.</summary>
internal sealed class IyuUBoardScreenCheck(IScreenAssets assets, ILoggerFactory logs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!assets.Available)
            logs.CreateLogger("Iyu.UBoard.Screen").LogWarning(
                "The board screen is switched on, but this build of Iyu.UBoard carries no screen (it was built without Node.js 22.12 or later), so its routes answer 503. Use the package from the feed, or build with Node.js.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The screen's routes, <c>{prefix}/ui</c>: the list of boards, one page per board id, the files the pages use, and the third-party notices
/// that travel with them. Mapped only when <see cref="IyuUBoardOptions.Screen"/> is on.
/// </summary>
internal static class IyuUBoardScreenRoutes
{
    /// <summary>
    /// What a page may load and do: its own files and requests to its own host, nothing else. The page runs no inline script (what the server
    /// writes into it are <c>&lt;meta&gt;</c> values), is framed by nobody, and cannot post a form elsewhere. Styles may be set on an element
    /// (<c>style-src-attr</c>): the viewer and the chart library size things that way.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";

    private const string AbsentMessage =
        "The board screen is not part of this build of Iyu.UBoard: the package was built without its web part (Node.js 22.12 or later was not found at build time). "
        + "Use the package from the feed, or build with Node.js available. The board routes themselves are not affected.";

    internal static void Map(RouteGroupBuilder group, string prefix)
    {
        var ui = group.MapGroup("ui");

        ui.MapGet("", (HttpContext http, IScreenAssets assets) => Page(http, assets, prefix, board: null));

        // A board's page. Who may see it is asked of the application like any other board route, and answered with the same 404 — a page does
        // not tell a person which boards exist, so a board that is not there and one the person may not view look the same. (A new board is
        // made first, by the list's dialog, and then opened: the application records its owner when it is made.)
        ui.MapGet("boards/{id}", async (string id, HttpContext http, IBoardAccess access, IScreenAssets assets) =>
            await access.AllowsAsync(http.User, BoardAction.View, id, http.RequestAborted) ? Page(http, assets, prefix, id) : Results.NotFound());

        // The files a page loads. Their names carry a hash of their content, so a browser may keep them for good.
        ui.MapGet("assets/{**path}", (string path, HttpContext http, IScreenAssets assets) =>
        {
            if (!assets.Available) return Absent();
            if (assets.Find("assets/" + path) is not { } file) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(file.Content, file.ContentType);
        });

        ui.MapGet("third-party-notices.txt", (HttpContext http, IScreenAssets assets) =>
        {
            if (!assets.Available) return Absent();
            if (assets.Find("THIRD-PARTY-NOTICES.txt") is not { } file) return Results.NotFound();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(file.Content, file.ContentType);
        });
    }

    private static IResult Absent() => Results.Text(AbsentMessage, "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);

    // The page: the built page with what the server knows written at the top of its head, where the browser reads it before anything else.
    private static IResult Page(HttpContext http, IScreenAssets assets, string prefix, string? board)
    {
        if (!assets.Available) return Absent();
        if (assets.Find("index.html") is not { } index) return Absent();

        var html = Render(Encoding.UTF8.GetString(index.Content), http.Request.PathBase + prefix, board);
        if (html is null)
            return Results.Problem("The screen's page has no <head> to write the host's settings into.", statusCode: StatusCodes.Status500InternalServerError);

        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    /// <summary>
    /// The page with a <c>base</c> (so the files it names are found whatever the page's own path is), the routes' prefix and, for a board's
    /// page, the board's id, written right after <c>&lt;head&gt;</c>. Null when the page has no head to write them into. Values are HTML
    /// encoded; no script is written.
    /// </summary>
    internal static string? Render(string index, string mountedPrefix, string? board)
    {
        const string Head = "<head>";
        var at = index.IndexOf(Head, StringComparison.Ordinal);
        if (at < 0) return null;

        var encodedPrefix = WebUtility.HtmlEncode(mountedPrefix);
        var written = new StringBuilder()
            .Append($"<base href=\"{encodedPrefix}/ui/\">")
            .Append($"<meta name=\"uboard-prefix\" content=\"{encodedPrefix}\">");
        if (board is not null) written.Append($"<meta name=\"uboard-board\" content=\"{WebUtility.HtmlEncode(board)}\">");
        return index.Insert(at + Head.Length, written.ToString());
    }
}
