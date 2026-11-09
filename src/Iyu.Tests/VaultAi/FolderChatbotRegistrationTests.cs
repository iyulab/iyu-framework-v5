using Iyu.VaultAi;
using Iyu.VaultAi.FolderChatbots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// Turning the folder chatbots on, and keeping them running. Off means nothing exists (no service, no client); on without what they need
/// means the host does not start, and the message names the setting and never its value; and the loop survives a check that throws.
/// </summary>
public class FolderChatbotRegistrationTests
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["VaultAi:Url"] = "https://vault.example.test",
        ["VaultAi:Token"] = "k",
        ["VaultAi:Chatbots:Enabled"] = "true",
        ["VaultAi:Chatbots:Path"] = "chatbots",
    };

    private static ServiceProvider Build(Dictionary<string, string?> values, string contentRoot = "/content")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new Env(contentRoot));
        services.AddVaultAiFolderChatbots(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] changes)
    {
        var copy = new Dictionary<string, string?>(Valid);
        foreach (var (key, value) in changes) copy[key] = value;
        return copy;
    }

    // ---------------- off means nothing ----------------

    [Fact]
    public void Off_by_default_nothing_is_registered()
    {
        using var provider = Build(new Dictionary<string, string?> { ["VaultAi:Url"] = "https://vault.example.test", ["VaultAi:Token"] = "k" });

        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.Null(provider.GetService<IFolderChatbotAdmin>());
        Assert.Null(provider.GetService<FolderChatbotSyncService>());
    }

    [Fact]
    public void Explicitly_off_registers_nothing_even_with_every_other_setting_present()
    {
        using var provider = Build(With(("VaultAi:Chatbots:Enabled", "false")));

        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.Null(provider.GetService<IFolderChatbotAdmin>());
    }

    // ---------------- on ----------------

    [Fact]
    public void Turned_on_the_engine_the_client_and_the_background_service_exist_and_bind_the_settings()
    {
        using var provider = Build(With(
            ("VaultAi:Chatbots:Prefix", "kb-"), ("VaultAi:Chatbots:RescanMinutes", "3"), ("VaultAi:Chatbots:MaxFileMB", "50"),
            ("VaultAi:Chatbots:McpAllowedHosts:0", "mcp.corp.example"), ("VaultAi:Chatbots:AdoptForeign", "true")));

        var options = provider.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value;

        Assert.Equal("kb-", options.Prefix);
        Assert.Equal(3, options.RescanMinutes);
        Assert.Equal(50, options.MaxFileMB);
        Assert.True(options.AdoptForeign);
        Assert.Equal(["mcp.corp.example"], options.McpAllowedHosts);
        Assert.NotNull(provider.GetRequiredService<IFolderChatbotAdmin>());
        Assert.NotNull(provider.GetRequiredService<FolderChatbotSyncService>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is FolderChatbotSyncHostedService);
        Assert.Equal(TimeSpan.FromMinutes(3), provider.GetRequiredService<FolderChatbotSyncHostedService>().Interval);
    }

    [Fact]
    public void The_default_record_is_the_file_existing_installations_already_have_and_the_folder_is_taken_from_the_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "content-root-test");
        using var provider = Build(Valid, root);

        var store = Assert.IsType<FileFolderChatbotManifestStore>(provider.GetRequiredService<IFolderChatbotManifestStore>());
        var source = Assert.IsType<FileSystemFolderChatbotSource>(provider.GetRequiredService<IFolderChatbotSource>());

        Assert.Equal(Path.Combine(root, "App_Data", "vault-ai-chatbots.manifest.json"), store.Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "chatbots")), source.Root);
    }

    [Fact]
    public void A_record_path_given_explicitly_is_used()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "elsewhere", "state.json");
        using var provider = Build(With(("VaultAi:Chatbots:ManifestPath", explicitPath)));

        Assert.Equal(explicitPath, Assert.IsType<FileFolderChatbotManifestStore>(provider.GetRequiredService<IFolderChatbotManifestStore>()).Path);
    }

    // ---------------- on without what it needs ----------------

    private const string Secret = "TOP-SECRET-VALUE-DO-NOT-LEAK";

    [Theory]
    [InlineData("VaultAi:Url", "", "VaultAi:Url")]
    [InlineData("VaultAi:Url", "not a url", "VaultAi:Url")]
    [InlineData("VaultAi:Url", "ftp://vault.example.test", "VaultAi:Url")]
    [InlineData("VaultAi:Token", "", "VaultAi:Token")]
    [InlineData("VaultAi:Chatbots:Path", "", "Path")]
    [InlineData("VaultAi:Chatbots:Prefix", "  ", "Prefix")]
    [InlineData("VaultAi:Chatbots:RescanMinutes", "0", "RescanMinutes")]
    public void Without_what_it_needs_the_host_does_not_start_and_the_message_names_the_setting(string key, string value, string named)
    {
        using var provider = Build(With((key, value)));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value);

        Assert.Contains(named, ex.Message);
    }

    [Theory]
    [InlineData("https://mcp.corp.example")]       // a scheme
    [InlineData("mcp.corp.example:8443")]          // a port
    [InlineData("mcp.corp.example/path")]          // a path
    [InlineData("user@mcp.corp.example")]          // credentials
    [InlineData("mcp corp")]                       // a space
    public void An_allowed_host_that_is_not_a_plain_host_name_stops_the_host_and_its_text_is_not_repeated(string entry)
    {
        using var provider = Build(With(("VaultAi:Chatbots:McpAllowedHosts:0", entry + Secret)));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value);

        Assert.Contains("McpAllowedHosts", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public void A_blank_allowed_host_entry_stops_the_host()
    {
        using var provider = Build(With(("VaultAi:Chatbots:McpAllowedHosts:0", " ")));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value);

        Assert.Contains("McpAllowedHosts", ex.Message);
    }

    [Fact]
    public void An_empty_allowed_host_list_is_valid_and_means_no_address_at_all()
    {
        // An empty list is how an administrator says "no url entry may be used". Binding an absent key leaves null (any host), so the list
        // has to be written in code or as an empty section; here it is set through the options directly.
        var validator = new FolderChatbotOptionsValidator(Options.Create(new VaultAiSettings { Url = "https://v.example.test", Token = "k" }));

        var result = validator.Validate(null, new VaultAiFolderChatbotOptions { Enabled = true, Path = "p", McpAllowedHosts = [] });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Failure_messages_never_contain_the_values_that_were_wrong()
    {
        var validator = new FolderChatbotOptionsValidator(Options.Create(new VaultAiSettings { Url = Secret, Token = "k" }));

        var result = validator.Validate(null, new VaultAiFolderChatbotOptions { Enabled = true, Path = Secret });

        Assert.True(result.Failed);
        Assert.DoesNotContain(Secret, result.FailureMessage);
    }

    // ---------------- the loop ----------------

    private sealed class CountingStore : IFolderChatbotManifestStore
    {
        public int Reads;
        public int ThrowOnRead;
        public string? Json;
        public string? Read()
        {
            var n = Interlocked.Increment(ref Reads);
            if (n <= ThrowOnRead) throw new InvalidOperationException("boom");
            return Json;
        }
        public void Write(string json) => Json = json;
        public void Backup() { }
    }

    private static (FolderChatbotSyncHostedService Hosted, CountingStore Store) Loop(TimeSpan interval, int throwFirst = 0)
    {
        var admin = new FakeFolderChatbotAdmin();
        admin.Models.Add(new ModelRef(Guid.NewGuid(), "language", "chat", "Chat", 131_072));
        admin.Models.Add(new ModelRef(Guid.NewGuid(), "embedding", "embed", "Embed", null));
        var source = new FakeFolderChatbotSource();
        source.Put("handbook", "a.md", "a");
        var store = new CountingStore { ThrowOnRead = throwFirst };
        var options = new VaultAiFolderChatbotOptions { Enabled = true, Path = "/docs", Prefix = "kb-" };
        var service = new FolderChatbotSyncService(options, admin, source, store, "https://v.example.test", NullLogger.Instance);
        var hosted = new FolderChatbotSyncHostedService(service, Options.Create(options), NullLogger<FolderChatbotSyncHostedService>.Instance)
        {
            Interval = interval,
            ErrorBackoff = TimeSpan.FromMilliseconds(20),
        };
        return (hosted, store);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task The_loop_checks_at_start_and_then_at_every_interval()
    {
        var (hosted, _) = Loop(TimeSpan.FromMilliseconds(30));
        await hosted.StartAsync(default);

        var ran = await UntilAsync(() => hosted.Checks >= 3);
        await hosted.StopAsync(default);

        Assert.True(ran, "the first check and at least two more should have run");
    }

    [Fact]
    public async Task A_request_for_a_check_now_does_not_wait_for_the_interval()
    {
        var (hosted, _) = Loop(TimeSpan.FromHours(1));
        await hosted.StartAsync(default);
        Assert.True(await UntilAsync(() => hosted.Checks == 1));
        await Task.Delay(100);
        Assert.Equal(1, hosted.Checks);   // and it really is waiting out the hour

        hosted.RequestSync();

        Assert.True(await UntilAsync(() => hosted.Checks == 2));
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task A_check_that_throws_is_logged_and_the_loop_goes_on()
    {
        var (hosted, store) = Loop(TimeSpan.FromMilliseconds(30), throwFirst: 2);
        await hosted.StartAsync(default);

        var recovered = await UntilAsync(() => store.Reads >= 4 && hosted.Checks >= 4);
        await hosted.StopAsync(default);

        Assert.True(recovered, "two checks threw, and the loop still reached later ones");
        Assert.NotNull(store.Json);   // a later check did its work
    }

    [Fact]
    public async Task Stopping_ends_the_loop_promptly_even_in_a_long_wait()
    {
        var (hosted, _) = Loop(TimeSpan.FromHours(1));
        await hosted.StartAsync(default);
        Assert.True(await UntilAsync(() => hosted.Checks == 1));

        var stop = hosted.StopAsync(default);

        Assert.True(await Task.WhenAny(stop, Task.Delay(5000)) == stop, "the host stops without waiting the interval out");
    }
}
