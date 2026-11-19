using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// Starts the built <c>samples/BoardChat</c> host as a process of its own — the way a person runs it — on a free port with a database
/// of its own and no model configured, and always stops it.
/// </summary>
public sealed class BoardChatSampleHost : IDisposable
{
    private readonly Process _process;
    private readonly string _database = Path.Combine(Path.GetTempPath(), "boardchat-" + Guid.NewGuid().ToString("N") + ".db");

    public HttpClient Client { get; }

    public BoardChatSampleHost()
    {
        // .../src/Iyu.Tests/bin/<configuration>/<framework>/ -> the sample's output of the same configuration, built beside the tests.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(root, "samples", "BoardChat", "bin", output.Parent!.Name, output.Name, "BoardChat.dll");
        Assert.True(File.Exists(dll), "The sample is built with the tests (a project reference); its output was not found.");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--urls", $"http://127.0.0.1:{port}", "--Database", _database },
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // No model: whatever the machine has in its environment, the sample under test names none.
        foreach (var name in new[] { "Model__BaseUrl", "Model__ApiKey", "Model__Name" }) start.Environment.Remove(name);
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (_process.HasExited) throw new InvalidOperationException("The sample host stopped while starting.");
            try
            {
                using var ready = Client.GetAsync("/$data/Regions/$count").GetAwaiter().GetResult();
                if (ready.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }

            if (DateTime.UtcNow > deadline) throw new TimeoutException("The sample host did not answer in time.");
            Thread.Sleep(200);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        finally
        {
            _process.Dispose();
            foreach (var file in new[] { _database, _database + "-shm", _database + "-wal" })
                try { File.Delete(file); } catch (IOException) { }
        }
    }
}

/// <summary>
/// The sample serves dummy sales over OData, keeps a person's boards, and says what each caller may build them from — and all of it
/// is switched on from <c>Program.cs</c>, not written there. The checks go over HTTP to the host as it runs.
/// </summary>
public sealed class BoardChatSampleTests(BoardChatSampleHost host) : IClassFixture<BoardChatSampleHost>
{
    private static HttpRequestMessage Get(string url, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user is not null) request.Headers.Add("X-Sample-User", user);
        return request;
    }

    private async Task<JsonNode> Json(string url, string? user = null)
    {
        using var response = await host.Client.SendAsync(Get(url, user));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<int> Count(string set) => int.Parse(await host.Client.GetStringAsync($"/$data/{set}/$count"), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_dummy_data_is_there_on_start_regions_products_customers_and_hundreds_of_orders()
    {
        Assert.Equal(5, await Count("Regions"));
        Assert.Equal(12, await Count("Products"));
        Assert.Equal(60, await Count("Customers"));
        Assert.InRange(await Count("Orders"), 400, 2000);
    }

    [Fact]
    public async Task The_metadata_lists_the_four_entity_sets()
    {
        var metadata = await host.Client.GetStringAsync("/$data/$metadata");

        foreach (var set in new[] { "Orders", "Customers", "Products", "Regions" })
            Assert.Contains($"EntitySet Name=\"{set}\"", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_are_read_a_page_at_a_time_with_the_names_of_what_they_point_to()
    {
        var page = await Json("/$data/Orders?$top=5&$select=OrderDate,Status,Total,RegionName,CustomerName,ProductName&$count=true");

        Assert.Equal(5, page["value"]!.AsArray().Count);
        Assert.True((int)page["@odata.count"]! >= 400);
        Assert.False(string.IsNullOrEmpty((string?)page["value"]![0]!["RegionName"]));
        Assert.True((decimal)page["value"]![0]!["Total"]! > 0);
    }

    [Fact]
    public async Task A_sum_of_the_order_totals_is_done_by_the_server_and_agrees_with_the_sum_by_region()
    {
        var all = await Json("/$data/Orders?$apply=aggregate(Total with sum as Revenue)");
        var byRegion = await Json("/$data/Orders?$apply=groupby((RegionName),aggregate(Total with sum as Revenue))");

        var total = (decimal)all["value"]![0]!["Revenue"]!;
        Assert.True(total > 0);
        Assert.Equal(5, byRegion["value"]!.AsArray().Count);
        Assert.Equal(total, byRegion["value"]!.AsArray().Sum(r => (decimal)r!["Revenue"]!));
    }

    [Fact]
    public async Task Revenue_by_month_comes_back_as_twelve_buckets_and_refunds_are_in_the_data()
    {
        var months = await Json("/$data/Orders?$apply=compute(year(OrderDate) as Y, month(OrderDate) as M)/groupby((Y,M),aggregate(Total with sum as Revenue))");
        var refunded = await Json("/$data/Orders?$filter=Status eq 'refunded'&$top=1&$select=Total&$count=true");

        Assert.Equal(12, months["value"]!.AsArray().Count);
        Assert.True((int)refunded["@odata.count"]! > 0);
    }

    [Fact]
    public async Task The_analyst_may_not_read_the_margin_any_way_and_the_finance_user_may()
    {
        foreach (var url in new[]
                 {
                     "/$data/Orders?$top=1&$select=Margin",
                     "/$data/Orders?$apply=aggregate(Margin with sum as M)",
                     "/$data/Orders?$filter=Margin gt 1&$select=Total",
                     "/$data/Customers?$top=1&$select=Email",
                 })
        {
            using var analyst = await host.Client.SendAsync(Get(url));
            using var finance = await host.Client.SendAsync(Get(url, "finance"));
            Assert.Equal(HttpStatusCode.Forbidden, analyst.StatusCode);
            Assert.Equal(HttpStatusCode.OK, finance.StatusCode);
        }

        using var open = await host.Client.SendAsync(Get("/$data/Orders?$top=1&$select=Total"));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task The_catalog_shows_the_protected_columns_as_locked_to_the_analyst_and_open_to_finance()
    {
        static bool Readable(JsonNode catalog, string set, string property) =>
            (bool)catalog["entities"]!.AsArray().Single(e => (string?)e!["name"] == set)!["properties"]!.AsArray()
                .Single(p => (string?)p!["name"] == property)!["readable"]!;

        var analyst = await Json("/uboard/catalog");
        var finance = await Json("/uboard/catalog", "finance");

        Assert.False(Readable(analyst, "Orders", "Margin"));
        Assert.False(Readable(analyst, "Customers", "Email"));
        Assert.True(Readable(analyst, "Orders", "Total"));
        Assert.True(Readable(finance, "Orders", "Margin"));
        Assert.True(Readable(finance, "Customers", "Email"));
        Assert.Equal(["Customers", "Orders", "Products", "Regions"], analyst["entities"]!.AsArray().Select(e => (string)e!["name"]!));
    }

    [Fact]
    public async Task A_board_is_made_read_back_and_listed_through_the_library_routes()
    {
        using var made = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/uboard/boards/sample-1?name=Revenue")
        {
            Content = new StringContent("""{"title":"Revenue"}""", Encoding.UTF8, "application/json"),
        });
        var again = await Json("/uboard/boards/sample-1");
        var listed = await Json("/uboard/boards");

        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(1, (int)again["revision"]!);
        Assert.Equal("Revenue", (string?)again["document"]!["title"]);
        Assert.Contains(listed.AsArray(), b => (string?)b!["id"] == "sample-1");
    }

    [Fact]
    public async Task A_request_from_another_site_is_refused_on_the_board_and_catalog_routes()
    {
        foreach (var url in new[] { "/uboard/boards", "/uboard/catalog" })
        {
            using var request = Get(url);
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task With_no_model_configured_the_assistants_route_is_not_there()
    {
        using var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/assistant/turn")
        {
            Content = new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
