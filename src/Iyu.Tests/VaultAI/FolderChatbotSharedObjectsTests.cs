using Iyu.VaultAI.FolderChatbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The objects all folder chatbots share - a storage, an agent, a web channel and the engine's MCP connectors - and how the engine keeps
/// them in line with the folders, on an in-memory vault-ai. The properties that matter in production: it makes nothing twice, it never
/// touches what another sync instance owns, an update is the whole configuration written back, and it does nothing when nothing changed.
/// </summary>
public class FolderChatbotSharedObjectsTests
{
    private sealed class Settings : IFolderChatbotSettingsReader
    {
        public Dictionary<string, SettingsText> Agents { get; } = [];
        public Dictionary<string, SettingsText> Mcp { get; } = [];

        public SettingsText ReadAgents(string? bot, out string? warning)
        {
            warning = null;
            return Agents.GetValueOrDefault(bot ?? "") ?? new SettingsText(null, null);
        }

        public SettingsText ReadMcp(string bot, out string? warning)
        {
            warning = null;
            return Mcp.GetValueOrDefault(bot) ?? new SettingsText(null, null);
        }
    }

    private sealed class Harness
    {
        public FakeFolderChatbotAdmin Admin { get; } = new();
        public Settings Settings { get; } = new();
        public FolderChatbotManifest Manifest { get; } = new() { Owner = Guid.NewGuid() };
        public FolderChatbotModels Models { get; } =
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        public bool Adopt { get; init; }
        public bool EmbeddingExplicit { get; init; } = true;
        public int Saves;

        public FolderChatbotSyncContext Context()
            => new(Admin, "kb-", new FolderChatbotConfig(Settings, Models), Manifest, () => Saves++, NullLogger.Instance)
            {
                AdoptForeign = Adopt,
                EmbeddingExplicit = EmbeddingExplicit,
            };

        public FolderChatbotSharedObjects Objects(out FolderChatbotSyncContext cx)
        {
            cx = Context();
            return new FolderChatbotSharedObjects(cx);
        }

        public static FolderSnapshot Snapshot(params string[] bots)
            => new(true, bots.ToDictionary(b => b, _ => (IReadOnlyList<LocalFile>)[], StringComparer.Ordinal));

        public static Func<string, BotTally> Tally => _ => new BotTally();
    }

    private static readonly IReadOnlySet<string> NoBots = new HashSet<string>();

    private static SettingsText McpJson(string json) => new(json, null);

    // ---------------- making and repairing ----------------

    [Fact]
    public async Task The_shared_objects_are_made_once_and_stamped_with_the_owner()
    {
        var h = new Harness();
        var objects = h.Objects(out _);

        Assert.Null(await objects.EnsureAsync(default));

        var storage = Assert.Single(h.Admin.Storages.Values);
        var agent = Assert.Single(h.Admin.Agents.Values);
        var channel = Assert.Single(h.Admin.Channels.Values);
        Assert.Equal("kb-all", storage.Name);
        Assert.Equal("kb-all", agent.Name);
        Assert.Equal(agent.Id, channel.AgentId);
        Assert.Contains(storage.Id, agent.Storages);
        Assert.True(FolderChatbotStorageMeta.TryParse(storage.Description, out var meta) && meta!.Owner == h.Manifest.Owner);
        Assert.Equal((storage.Id, agent.Id, channel.Id), (h.Manifest.StorageId!.Value, h.Manifest.AgentId!.Value, h.Manifest.ChannelId!.Value));
        Assert.Equal(h.Models.Index, h.Manifest.StorageLanguageModel);   // a new storage has no unindexable files to process again

        // Again: it finds them by the recorded ids and makes nothing.
        h.Admin.Calls.Clear();
        Assert.Null(await h.Objects(out _).EnsureAsync(default));
        Assert.Empty(h.Admin.Calls);
    }

    [Fact]
    public async Task Objects_of_the_same_name_are_taken_over_not_made_again()
    {
        var h = new Harness();
        var owned = new FolderChatbotStorageMeta(h.Manifest.Owner, new Dictionary<string, FolderChatbotBotMeta>()).Format();
        var storageId = h.Admin.AddStorage("kb-all", owned, embedding: h.Models.Embedding);
        var agentId = h.Admin.AddAgent("kb-all");

        Assert.Null(await h.Objects(out _).EnsureAsync(default));

        Assert.Equal(storageId, h.Manifest.StorageId);
        Assert.Equal(agentId, h.Manifest.AgentId);
        Assert.Equal(["create-channel"], h.Admin.Calls);
    }

    [Fact]
    public async Task A_storage_that_belongs_to_another_instance_is_not_taken_over()
    {
        var h = new Harness();
        var other = Guid.NewGuid();
        h.Admin.AddStorage("kb-all", new FolderChatbotStorageMeta(other, new Dictionary<string, FolderChatbotBotMeta>()).Format());

        var ex = await Assert.ThrowsAsync<FolderChatbotForeignOwnerException>(() => h.Objects(out _).EnsureAsync(default));

        Assert.Equal(other, ex.Owner);
        Assert.Empty(h.Admin.Calls);
        Assert.Null(h.Manifest.StorageId);
    }

    [Fact]
    public async Task A_foreign_storage_is_taken_over_only_when_adopting_is_allowed()
    {
        var h = new Harness { Adopt = true };
        var id = h.Admin.AddStorage("kb-all",
            new FolderChatbotStorageMeta(Guid.NewGuid(), new Dictionary<string, FolderChatbotBotMeta>()).Format(),
            embedding: h.Models.Embedding);

        Assert.Null(await h.Objects(out _).EnsureAsync(default));

        Assert.Equal(id, h.Manifest.StorageId);
    }

    [Fact]
    public async Task A_storage_on_another_embedding_model_is_not_switched_unless_the_model_was_set_explicitly()
    {
        var used = Guid.NewGuid();
        var implicitModel = new Harness { EmbeddingExplicit = false };
        implicitModel.Admin.AddStorage("kb-all", embedding: used);

        var ex = await Assert.ThrowsAsync<FolderChatbotEmbeddingMismatchException>(
            () => implicitModel.Objects(out _).EnsureAsync(default));
        Assert.Equal("kb-all", ex.StorageName);

        var explicitModel = new Harness { EmbeddingExplicit = true };
        var id = explicitModel.Admin.AddStorage("kb-all", embedding: used);
        Assert.Null(await explicitModel.Objects(out _).EnsureAsync(default));
        Assert.Equal(id, explicitModel.Manifest.StorageId);
    }

    [Fact]
    public async Task When_all_three_vanish_at_once_nothing_is_made_and_the_check_says_why()
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);
        h.Admin.Storages.Clear();
        h.Admin.Agents.Clear();
        h.Admin.Channels.Clear();
        h.Admin.Calls.Clear();

        var warning = await h.Objects(out _).EnsureAsync(default);

        Assert.NotNull(warning);
        Assert.Contains("vanished", warning);
        Assert.Empty(h.Admin.Calls);
    }

    [Fact]
    public async Task An_object_the_list_leaves_out_but_a_lookup_finds_is_never_made_a_second_time()
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);
        h.Admin.HiddenFromLists.Add(h.Manifest.StorageId!.Value);
        h.Admin.Calls.Clear();

        var warning = await h.Objects(out _).EnsureAsync(default);

        Assert.NotNull(warning);
        Assert.Contains("storage", warning);
        Assert.Empty(h.Admin.Calls);
    }

    [Fact]
    public async Task A_vanished_storage_is_made_again_and_every_bots_file_record_is_forgotten()
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);
        h.Manifest.Bots["handbook"] = new BotState
        {
            Files = { ["a.md"] = new FileState(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, "x") },
        };
        h.Admin.Storages.Remove(h.Manifest.StorageId!.Value);
        h.Admin.Calls.Clear();

        Assert.Null(await h.Objects(out _).EnsureAsync(default));

        Assert.Equal(["create-storage"], h.Admin.Calls);
        Assert.Empty(h.Manifest.Bots["handbook"].Files);   // they were uploaded to a storage that no longer exists
        Assert.Contains(h.Manifest.StorageId!.Value, h.Admin.Storages.Keys);
    }

    [Fact]
    public async Task A_vanished_agent_takes_its_channel_with_it_and_both_are_made_again()
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);
        var oldChannel = h.Manifest.ChannelId!.Value;
        h.Admin.Agents.Remove(h.Manifest.AgentId!.Value);
        h.Admin.Calls.Clear();

        Assert.Null(await h.Objects(out _).EnsureAsync(default));

        Assert.Equal(["delete-channel", "create-agent", "create-channel"], h.Admin.Calls.OrderBy(c => c == "create-agent" ? 1 : c == "delete-channel" ? 0 : 2).ToList());
        Assert.NotEqual(oldChannel, h.Manifest.ChannelId);
        Assert.Equal(h.Manifest.AgentId, Assert.Single(h.Admin.Channels.Values).AgentId);
    }

    // ---------------- the agent and its scopes ----------------

    private static async Task<Harness> ReadyAsync(params string[] bots)
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);
        foreach (var bot in bots) h.Manifest.Bots[bot] = new BotState();
        h.Admin.Calls.Clear();
        return h;
    }

    [Fact]
    public async Task The_agent_gets_the_built_in_instructions_the_root_ones_and_one_scope_per_bot()
    {
        var h = await ReadyAsync("handbook", "faq");
        h.Settings.Agents[""] = new SettingsText("Answer in Korean.", null);
        h.Settings.Agents["faq"] = new SettingsText("  Be brief.  ", null);

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook", "faq"), NoBots, Harness.Tally, default);

        var agent = h.Admin.Agents[h.Manifest.AgentId!.Value];
        Assert.StartsWith(FolderChatbotFolderRules.DefaultInstructions.Trim(), agent.Instructions);
        Assert.EndsWith("\n\nAnswer in Korean.", agent.Instructions);
        Assert.Equal(["faq", "handbook"], agent.Scopes.Select(s => s.Name));   // ordinal order
        Assert.Equal("Be brief.", agent.Scopes.Single(s => s.Name == "faq").Instructions);
        Assert.Null(agent.Scopes.Single(s => s.Name == "handbook").Instructions);
        Assert.Equal([FolderChatbotNames.TagOf("faq")], agent.Scopes.Single(s => s.Name == "faq").Tags);
        Assert.Equal(h.Models.Answer, agent.Model);
        Assert.Equal([h.Manifest.StorageId!.Value], agent.Storages);
    }

    [Fact]
    public async Task The_agent_is_written_only_when_its_configuration_changed()
    {
        var h = await ReadyAsync("handbook");

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        Assert.Equal(1, h.Admin.Count("update-agent"));

        h.Settings.Agents["handbook"] = new SettingsText("Now with a rule.", null);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        Assert.Equal(2, h.Admin.Count("update-agent"));
    }

    [Fact]
    public async Task A_bot_being_removed_leaves_the_agents_scopes()
    {
        var h = await ReadyAsync("handbook", "old");

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), new HashSet<string> { "old" }, Harness.Tally, default);

        Assert.Equal(["handbook"], h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Select(s => s.Name));
    }

    [Fact]
    public async Task A_vanished_agent_found_while_updating_is_recorded_as_lost_and_the_failure_is_passed_on()
    {
        var h = await ReadyAsync("handbook");
        h.Admin.Agents.Remove(h.Manifest.AgentId!.Value);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default));

        Assert.Null(h.Manifest.AgentId);
        Assert.Null(h.Manifest.ChannelId);   // it pointed at the old agent
    }

    [Fact]
    public async Task Instructions_stay_as_last_applied_when_the_root_file_cannot_be_read()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Agents[""] = new SettingsText("Rule one.", null);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        var applied = h.Admin.Agents[h.Manifest.AgentId!.Value].Instructions;

        h.Settings.Agents[""] = new SettingsText(null, "access denied");
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Equal(applied, h.Admin.Agents[h.Manifest.AgentId!.Value].Instructions);
        Assert.Equal(1, h.Admin.Count("update-agent"));
    }

    // ---------------- connectors ----------------

    private const string OneServer = """{"mcpServers":{"search":{"url":"https://mcp.example.test/sse","headers":{"Authorization":"Bearer {{KEY}}"}}}}""";

    [Fact]
    public async Task A_url_entry_becomes_an_owned_connector_attached_to_the_scope_and_the_agent()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        var connector = Assert.Single(h.Admin.Connectors.Values);
        Assert.Equal("kb-handbook/search", connector.Name);
        Assert.Contains(h.Manifest.Owner.ToString(), connector.Description);
        Assert.Equal("Bearer {{KEY}}", connector.Headers["Authorization"]);   // the secret reference is passed on, not resolved
        var agent = h.Admin.Agents[h.Manifest.AgentId!.Value];
        Assert.Equal([connector.Id], agent.Scopes.Single().ConnectorIds);
        Assert.Equal([connector.Id], agent.Connectors);
        Assert.Equal(connector.Id, h.Manifest.Bots["handbook"].Connectors["search"].Id);

        // Unchanged: nothing is written again.
        h.Admin.Calls.Clear();
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        Assert.Empty(h.Admin.Calls);
    }

    [Fact]
    public async Task A_changed_address_updates_the_connector_and_the_agent()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        h.Admin.Calls.Clear();

        h.Settings.Mcp["handbook"] = McpJson(OneServer.Replace("/sse", "/v2/sse"));
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Equal(["update-connector", "update-agent"], h.Admin.Calls);
        Assert.EndsWith("/v2/sse", Assert.Single(h.Admin.Connectors.Values).Url);
    }

    [Fact]
    public async Task A_connector_of_the_same_name_that_belongs_to_another_instance_is_left_alone_and_not_attached()
    {
        var h = await ReadyAsync("handbook");
        var foreign = h.Admin.AddConnector("kb-handbook/search", $$"""{"owner":"{{Guid.NewGuid()}}"}""", "https://theirs.test/mcp");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        var cx = h.Context();

        await new FolderChatbotSharedObjects(cx).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Equal("https://theirs.test/mcp", h.Admin.Connectors[foreign].Url);   // untouched
        Assert.Empty(h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single().ConnectorIds);
        Assert.Single(cx.Warnings);
        Assert.Contains("another sync instance", cx.Warnings[0]);
        Assert.DoesNotContain("create-connector", h.Admin.Calls);
    }

    [Fact]
    public async Task A_connector_without_an_owner_mark_is_taken_over_and_stamped()
    {
        var h = await ReadyAsync("handbook");
        var id = h.Admin.AddConnector("kb-handbook/search", "made by hand");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Contains(h.Manifest.Owner.ToString(), h.Admin.Connectors[id].Description);
        Assert.Single(h.Admin.Connectors);
    }

    [Fact]
    public async Task A_connector_entry_attaches_an_existing_one_by_exact_name_and_ignores_a_missing_one()
    {
        var h = await ReadyAsync("handbook");
        var existing = h.Admin.AddConnector("company-wiki", "");
        h.Settings.Mcp["handbook"] = McpJson("""{"mcpServers":{"wiki":{"connector":"company-wiki"},"gone":{"connector":"no-such"}}}""");

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Equal([existing], h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single().ConnectorIds);
        Assert.Empty(h.Manifest.Bots["handbook"].Connectors);   // referenced connectors are not the engine's, so they are not recorded
        Assert.DoesNotContain(h.Admin.Calls, c => c.Contains("connector"));
    }

    [Theory]
    [InlineData("kb-faq/search", "")]                                        // named like an engine connector of this prefix
    [InlineData("shared-search", "{\"owner\":\"7d1d4f55-7a39-4c31-a2f8-0d0c1d1a9b11\"}")]   // carries an owner mark, whatever its name
    public async Task A_connector_entry_cannot_attach_a_connector_an_engine_owns_for_another_bots_scope(string name, string description)
    {
        var h = await ReadyAsync("handbook");
        var owned = h.Admin.AddConnector(name, description);
        h.Settings.Mcp["handbook"] = McpJson("{\"mcpServers\":{\"borrowed\":{\"connector\":\"" + name + "\"}}}");
        var cx = h.Context();

        await new FolderChatbotSharedObjects(cx).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Empty(h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single().ConnectorIds);
        Assert.DoesNotContain(owned, h.Admin.Agents[h.Manifest.AgentId!.Value].Connectors);
        Assert.Single(cx.Warnings);
        Assert.Contains("owns", cx.Warnings[0]);
    }

    [Fact]
    public async Task A_connector_recorded_but_vanished_remotely_is_made_again()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        h.Admin.Connectors.Clear();
        h.Admin.Calls.Clear();

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Single(h.Admin.Connectors);
        Assert.Contains("create-connector", h.Admin.Calls);
        Assert.Equal(h.Manifest.Bots["handbook"].Connectors["search"].Id, Assert.Single(h.Admin.Connectors.Keys));
    }

    [Fact]
    public async Task A_broken_mcp_file_freezes_that_bots_scope_and_does_not_hold_up_the_others()
    {
        var h = await ReadyAsync("handbook", "faq");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook", "faq"), NoBots, Harness.Tally, default);
        var applied = h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single(s => s.Name == "handbook");

        h.Settings.Mcp["handbook"] = McpJson("{ not json");
        h.Settings.Agents["faq"] = new SettingsText("Changed.", null);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook", "faq"), NoBots, Harness.Tally, default);

        var scopes = h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes;
        Assert.Equal(applied.ConnectorIds, scopes.Single(s => s.Name == "handbook").ConnectorIds);   // as last applied
        Assert.Equal("Changed.", scopes.Single(s => s.Name == "faq").Instructions);                 // the other bot moved on
        Assert.Single(h.Admin.Connectors);                                                          // and nothing was deleted
    }

    [Fact]
    public async Task A_frozen_scope_drops_a_connector_that_vanished_remotely_since_the_agent_may_not_name_it()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        h.Settings.Mcp["handbook"] = McpJson("{ not json");
        h.Admin.Connectors.Clear();
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        Assert.Empty(h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single().ConnectorIds);
    }

    [Fact]
    public async Task A_bot_whose_folder_is_gone_keeps_the_scope_it_had_while_its_deletion_waits()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Agents["handbook"] = new SettingsText("Be exact.", null);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        h.Admin.Calls.Clear();
        h.Settings.Agents["handbook"] = new SettingsText("Changed after the folder vanished.", null);

        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot(), NoBots, Harness.Tally, default);   // not in the snapshot

        Assert.Equal("Be exact.", h.Admin.Agents[h.Manifest.AgentId!.Value].Scopes.Single().Instructions);
        Assert.Empty(h.Admin.Calls);
    }

    [Fact]
    public async Task Connectors_for_entries_that_left_the_file_are_deleted_but_only_after_the_agent_stopped_using_them()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);

        h.Settings.Mcp["handbook"] = McpJson("""{"mcpServers":{}}""");
        var objects = h.Objects(out _);
        await objects.ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        Assert.Single(h.Admin.Connectors);   // still there: the agent is updated first
        Assert.Empty(h.Admin.Agents[h.Manifest.AgentId!.Value].Connectors);

        await objects.PruneConnectorsAsync("handbook", h.Manifest.Bots["handbook"], default);

        Assert.Empty(h.Admin.Connectors);
        Assert.Empty(h.Manifest.Bots["handbook"].Connectors);
    }

    [Fact]
    public async Task A_broken_mcp_file_never_causes_the_connectors_it_used_to_name_to_be_pruned()
    {
        // With the file unreadable the entries read as none, which would look like "every entry was removed".
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        h.Settings.Mcp["handbook"] = McpJson("{ not json");
        var objects = h.Objects(out _);

        await objects.PruneConnectorsAsync("handbook", h.Manifest.Bots["handbook"], default);

        Assert.Single(h.Admin.Connectors);
        Assert.Single(h.Manifest.Bots["handbook"].Connectors);
        Assert.DoesNotContain("delete-connector", h.Admin.Calls);
    }

    [Fact]
    public async Task A_recorded_connector_that_has_since_been_stamped_by_another_instance_is_not_deleted()
    {
        var h = await ReadyAsync("handbook");
        h.Settings.Mcp["handbook"] = McpJson(OneServer);
        await h.Objects(out _).ReconcileAgentAsync(Harness.Snapshot("handbook"), NoBots, Harness.Tally, default);
        var id = Assert.Single(h.Admin.Connectors.Keys);
        h.Admin.Connectors[id] = h.Admin.Connectors[id] with { Description = $$"""{"owner":"{{Guid.NewGuid()}}"}""" };
        h.Settings.Mcp["handbook"] = McpJson("""{"mcpServers":{}}""");
        var cx = h.Context();

        await new FolderChatbotSharedObjects(cx).PruneConnectorsAsync("handbook", h.Manifest.Bots["handbook"], default);

        Assert.Contains(id, h.Admin.Connectors.Keys);
        Assert.Single(cx.Warnings);
    }

    [Fact]
    public async Task Deleting_a_bot_deletes_its_connectors_even_when_the_record_was_lost_but_never_another_instances()
    {
        var h = await ReadyAsync("handbook");
        var ours = h.Admin.AddConnector("kb-handbook/orphan", $$"""{"owner":"{{h.Manifest.Owner}}"}""");
        var theirs = h.Admin.AddConnector("kb-handbook/theirs", $$"""{"owner":"{{Guid.NewGuid()}}"}""");
        var cx = h.Context();

        await new FolderChatbotSharedObjects(cx).DeleteOwnedConnectorsAsync("handbook", h.Manifest.Bots["handbook"], default);

        Assert.DoesNotContain(ours, h.Admin.Connectors.Keys);
        Assert.Contains(theirs, h.Admin.Connectors.Keys);
    }

    // ---------------- the storage ----------------

    [Fact]
    public async Task The_storage_is_written_only_when_its_settings_or_state_meta_changed()
    {
        var h = await ReadyAsync("handbook");

        await h.Objects(out _).ReconcileStorageAsync(Harness.Tally, default);
        await h.Objects(out _).ReconcileStorageAsync(Harness.Tally, default);
        Assert.Equal(1, h.Admin.Count("update-storage"));

        await h.Objects(out _).ReconcileStorageAsync(_ => new BotTally { Failed = 2 }, default);
        Assert.Equal(2, h.Admin.Count("update-storage"));
        var description = h.Admin.Storages[h.Manifest.StorageId!.Value].Description;
        Assert.True(FolderChatbotStorageMeta.TryParse(description, out var meta));
        Assert.Equal(2, meta!.Bots["handbook"].Failed);
    }

    [Fact]
    public async Task A_new_language_model_has_the_files_that_could_not_be_indexed_processed_again_once()
    {
        var h = await ReadyAsync();
        h.Manifest.StorageLanguageModel = null;
        h.Admin.Files[h.Manifest.StorageId!.Value] =
        [
            new RemoteFile(Guid.NewGuid(), "a/x.md", 1, "not_indexable"),
            new RemoteFile(Guid.NewGuid(), "a/y.md", 1, "indexed"),
        ];

        await h.Objects(out _).ReconcileStorageAsync(Harness.Tally, default);
        await h.Objects(out _).ReconcileStorageAsync(Harness.Tally, default);

        Assert.Equal(1, h.Admin.Count("retry-file"));
        Assert.Equal(h.Models.Index, h.Manifest.StorageLanguageModel);
    }

    [Fact]
    public async Task A_vanished_storage_found_while_updating_is_recorded_as_lost()
    {
        var h = await ReadyAsync();
        h.Admin.Storages.Remove(h.Manifest.StorageId!.Value);

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Objects(out _).ReconcileStorageAsync(Harness.Tally, default));

        Assert.Null(h.Manifest.StorageId);
    }

    [Fact]
    public async Task Every_state_change_saves_the_manifest_so_stopping_part_way_forgets_nothing()
    {
        var h = new Harness();
        await h.Objects(out _).EnsureAsync(default);

        Assert.True(h.Saves >= 3);   // storage, agent, channel
    }
}
