using System.Text.Json;
using Iyu.VaultAI.FolderChatbots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// One check as a host runs it: the manifest is read, bound to the prefix and address, the models are picked, and the engine goes through
/// the folders - against an in-memory vault-ai and an in-memory folder. What matters: the record is bound to what it was written for, the
/// engine does nothing until the models are decided, an unreadable record or folder changes nothing remotely, and checks never overlap.
/// </summary>
public class FolderChatbotSyncServiceTests
{
    private sealed class MemoryStore : IFolderChatbotManifestStore
    {
        public string? Json;
        public string? BackedUp;
        public int Writes;

        public string? Read() => Json;
        public void Write(string json) { Json = json; Writes++; }
        public void Backup() => BackedUp = Json;
    }

    private sealed class Rig
    {
        public FakeFolderChatbotAdmin Admin { get; } = new();
        public FakeFolderChatbotSource Source { get; } = new();
        public MemoryStore Store { get; } = new();
        public VaultAIFolderChatbotOptions Options { get; } = new() { Enabled = true, Path = "/docs", Prefix = "kb-" };
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        public string VaultUrl { get; set; } = "https://vault.example.test/";
        public HashSet<string> Intents { get; } = [];
        public List<string> Deleted { get; } = [];

        public Rig(bool withModels = true)
        {
            if (!withModels) return;
            Admin.Models.Add(new ModelRef(Guid.NewGuid(), "language", "chat-large", "Chat large", 131_072));
            Admin.Models.Add(new ModelRef(Guid.NewGuid(), "embedding", "embed", "Embed", null));
        }

        public FolderChatbotSyncService Service() =>
            new(Options, Admin, Source, Store, VaultUrl, NullLogger.Instance, Time, () => Intents, Deleted.Add);

        public FolderChatbotManifest Manifest => FolderChatbotManifest.Parse(Store.Json);
    }

    // ---------------- the first check and the record ----------------

    [Fact]
    public async Task The_first_check_binds_the_record_to_the_prefix_and_address_and_stores_what_it_did()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Equal(0, report.Failed);
        Assert.True(report.Applied > 0);
        var manifest = rig.Manifest;
        Assert.NotEqual(Guid.Empty, manifest.Owner);
        Assert.Equal("kb-", manifest.Prefix);
        Assert.Equal("https://vault.example.test", manifest.Url);   // without the trailing slash: the two spellings are one address
        Assert.True(manifest.Models!.IsComplete);
        Assert.Single(manifest.Bots["handbook"].Files);
        Assert.Contains(rig.Admin.Calls, c => c == "upload:handbook/intro.md");
    }

    [Fact]
    public async Task A_second_check_with_nothing_changed_changes_nothing()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.Service().RunOnceAsync(default);
        rig.Admin.Calls.Clear();

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Equal(0, report.Applied);
        Assert.Empty(rig.Admin.Calls);
    }

    [Fact]
    public async Task A_manifest_written_for_another_prefix_is_set_aside_and_the_old_prefixs_objects_are_left_alone()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.Service().RunOnceAsync(default);
        var owner = rig.Manifest.Owner;
        var oldStorage = rig.Admin.Storages.Values.Single().Id;

        rig.Options.Prefix = "kb2-";
        rig.Admin.Calls.Clear();
        await rig.Service().RunOnceAsync(default);

        Assert.NotNull(rig.Store.BackedUp);                       // a copy of the old record is kept
        Assert.Equal(owner, rig.Manifest.Owner);                  // the same instance
        Assert.Equal("kb2-", rig.Manifest.Prefix);
        Assert.Contains(oldStorage, rig.Admin.Storages.Keys);     // the old prefix's storage still exists, untouched
        Assert.Equal(2, rig.Admin.Storages.Count);                // and a new one was made under the new prefix
        Assert.DoesNotContain(rig.Admin.Calls, c => c.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_written_for_another_address_is_set_aside_too()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.Service().RunOnceAsync(default);

        rig.VaultUrl = "https://other.example.test";
        await rig.Service().RunOnceAsync(default);

        Assert.NotNull(rig.Store.BackedUp);
        Assert.Equal("https://other.example.test", rig.Manifest.Url);
    }

    [Fact]
    public async Task An_unreadable_manifest_stops_the_check_without_touching_vault_ai()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        rig.Store.Json = "{ this is not json";

        var report = await rig.Service().RunOnceAsync(default);

        Assert.NotNull(report.GuardWarning);
        Assert.Empty(rig.Admin.Calls);
        Assert.Equal("{ this is not json", rig.Store.Json);   // not overwritten either
    }

    [Theory]
    [InlineData("", "kb-")]
    [InlineData("/docs", "")]
    [InlineData("/docs", "  ")]
    public async Task Without_a_path_or_a_prefix_nothing_is_done(string path, string prefix)
    {
        var rig = new Rig();
        rig.Options.Path = path;
        rig.Options.Prefix = prefix;

        var report = await rig.Service().RunOnceAsync(default);

        Assert.NotNull(report.GuardWarning);
        Assert.Empty(rig.Admin.Calls);
        Assert.Equal(0, rig.Store.Writes);
    }

    // ---------------- models ----------------

    [Fact]
    public async Task Until_the_models_are_decided_nothing_is_uploaded_or_made()
    {
        var rig = new Rig(withModels: false);   // the workspace has no models at all
        rig.Source.Put("handbook", "intro.md", "hello");

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Contains("models", report.GuardWarning);
        Assert.Empty(rig.Admin.Calls);   // a file uploaded before a model exists would harden into "cannot be indexed"
    }

    [Fact]
    public async Task An_unreadable_model_list_skips_the_check()
    {
        var rig = new Rig();
        rig.Admin.ModelListGate = Task.FromException(new HttpRequestException("down"));
        rig.Source.Put("handbook", "intro.md", "hello");

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Contains("model list", report.GuardWarning);
        Assert.Empty(rig.Admin.Calls);
    }

    [Fact]
    public async Task With_the_manifest_lost_the_embedding_model_already_in_use_is_not_switched()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.Service().RunOnceAsync(default);
        var inUse = rig.Admin.Storages.Values.Single().Embedding!.Value;
        rig.Admin.Models.Add(new ModelRef(Guid.NewGuid(), "embedding", "embed-2", "Embed 2", null));   // a second candidate appears
        rig.Store.Json = null;                                                                          // and the manifest is lost
        rig.Options.AdoptForeign = true;   // the storage carries the lost manifest's owner id, so taking it over has to be allowed

        await rig.Service().RunOnceAsync(default);

        Assert.Equal(inUse, rig.Manifest.Models!.Embedding);
    }

    // ---------------- the folder ----------------

    [Fact]
    public async Task An_unreadable_folder_does_nothing_remote()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.Service().RunOnceAsync(default);
        rig.Source.Unreadable = true;
        rig.Admin.Calls.Clear();

        var report = await rig.Service().RunOnceAsync(default);

        Assert.NotNull(report.GuardWarning);
        Assert.Empty(rig.Admin.Calls);
    }

    [Fact]
    public async Task A_bot_the_host_says_was_deleted_on_purpose_skips_the_grace_period()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("faq", "q.md", "q");
        await rig.Service().RunOnceAsync(default);

        rig.Source.Bots.Remove("faq");
        rig.Intents.Add("faq");
        await rig.Service().RunOnceAsync(default);

        Assert.DoesNotContain("faq", rig.Manifest.Bots.Keys);
        Assert.Equal(["faq"], rig.Deleted);
    }

    [Fact]
    public async Task A_restricted_host_list_keeps_an_mcp_address_elsewhere_out_of_the_agent_and_says_so()
    {
        var rig = new Rig();
        rig.Options.McpAllowedHosts = ["mcp.corp.example"];
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Mcp["handbook"] = new SettingsText(
            """{"mcpServers":{"search":{"url":"https://attacker.example/sse","headers":{"A":"Bearer {{KEY}}"}}}}""", null);

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Empty(rig.Admin.Connectors);
        Assert.Contains("not one the administrator allows", report.GuardWarning);
    }

    [Fact]
    public async Task Allowing_adoption_does_not_take_over_another_instances_objects_once_this_instance_has_its_own_storage()
    {
        // Adoption is meant for a fresh start. Left on after that, it would let this instance seize a connector another instance owns.
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.Service().RunOnceAsync(default);
        var theirs = rig.Admin.AddConnector("kb-handbook/search", "{\"owner\":\"" + Guid.NewGuid() + "\"}", "https://theirs.example.test/mcp");
        rig.Source.Mcp["handbook"] = new SettingsText(
            "{\"mcpServers\":{\"search\":{\"url\":\"https://mine.example.test/sse\"}}}", null);
        rig.Options.AdoptForeign = true;

        var report = await rig.Service().RunOnceAsync(default);

        Assert.Equal("https://theirs.example.test/mcp", rig.Admin.Connectors[theirs].Url);   // untouched
        Assert.Contains("another sync instance", report.GuardWarning);
    }

    // ---------------- one check at a time ----------------

    [Fact]
    public async Task Two_checks_started_together_do_not_overlap()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        var gate = new TaskCompletionSource();
        rig.Admin.ModelListGate = gate.Task;
        var service = rig.Service();

        var first = service.RunOnceAsync(default);
        var second = service.RunOnceAsync(default);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref rig.Admin.ModelListCalls));   // the second has not started: it waits for the first

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, rig.Admin.ModelListCalls);
        Assert.Single(rig.Admin.Calls, c => c == "upload:handbook/a.md");   // and the file went up once
    }

    [Fact]
    public async Task The_stored_manifest_is_valid_json_a_later_check_can_read_back()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.Service().RunOnceAsync(default);

        using var doc = JsonDocument.Parse(rig.Store.Json!);

        Assert.True(doc.RootElement.TryGetProperty("bots", out var bots));
        Assert.True(bots.TryGetProperty("handbook", out _));
    }
}
