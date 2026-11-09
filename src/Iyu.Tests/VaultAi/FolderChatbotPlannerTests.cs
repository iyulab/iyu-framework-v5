using Iyu.VaultAi.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAi;

public class FolderChatbotPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private static readonly IReadOnlySet<string> NoIntents = new HashSet<string>();

    private static LocalFile File(string path, long size = 10, string sha = "aa", DateTimeOffset? at = null)
        => new(path, size, at ?? T0, sha);

    private static FolderSnapshot Snapshot(params (string Bot, LocalFile[] Files)[] bots)
        => new(true, bots.ToDictionary(b => b.Bot, b => (IReadOnlyList<LocalFile>)b.Files, StringComparer.Ordinal));

    private static BotState Recorded(params (string Path, Guid Id, long Size, string Sha)[] files)
    {
        var state = new BotState();
        foreach (var f in files) state.Files[f.Path] = new FileState(f.Id, f.Size, T0, f.Sha);
        return state;
    }

    private static FolderChatbotManifest Manifest(params (string Bot, BotState State)[] bots)
    {
        var m = new FolderChatbotManifest();
        foreach (var b in bots) m.Bots[b.Bot] = b.State;
        return m;
    }

    private static IReadOnlyList<SyncAction> Plan(
        FolderSnapshot fs, FolderChatbotManifest m, IReadOnlyList<RemoteFile>? remote = null, IReadOnlySet<string>? intents = null)
        => FolderChatbotPlanner.Plan(fs, m, remote, Now, Grace, FolderChatbotSupportedFiles.IsSupported,
            intents ?? NoIntents, out _);

    private static string? Warning(FolderSnapshot fs, FolderChatbotManifest m, IReadOnlyList<RemoteFile>? remote = null)
    {
        FolderChatbotPlanner.Plan(fs, m, remote, Now, Grace, FolderChatbotSupportedFiles.IsSupported, NoIntents, out var warning);
        return warning;
    }

    private static RemoteFile Remote(Guid id, string name, params string[] tags)
        => new(id, name, 10, "indexed") { Tags = tags };

    // ---- a new bot, an unreadable root

    [Fact]
    public void A_new_folder_becomes_a_bot_and_its_supported_files_are_uploaded_in_order()
    {
        var actions = Plan(Snapshot(("sales", [File("b.pdf"), File("a.pdf"), File("c.exe")])), Manifest());

        Assert.Collection(actions,
            a => Assert.Equal(new CreateBot("sales"), a),
            a => Assert.Equal("a.pdf", Assert.IsType<UploadFile>(a).File.RelativePath),
            a => Assert.Equal("b.pdf", Assert.IsType<UploadFile>(a).File.RelativePath),
            a => Assert.Equal(new SkipUnsupported("sales", "c.exe"), a));
    }

    [Fact]
    public void A_folder_name_too_long_for_a_tag_is_not_made_a_bot_and_is_said()
    {
        var name = new string('x', FolderChatbotNames.MaxBotNameLength + 1);

        var actions = Plan(Snapshot((name, [File("a.pdf")])), Manifest());

        Assert.Empty(actions);
        Assert.Contains("is longer than", Warning(Snapshot((name, [File("a.pdf")])), Manifest()));
    }

    [Fact]
    public void An_unreadable_root_plans_nothing_and_says_why()
    {
        var fs = new FolderSnapshot(false, new Dictionary<string, IReadOnlyList<LocalFile>>(), "access denied");

        var actions = FolderChatbotPlanner.Plan(fs, Manifest(("sales", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa")))), null,
            Now, Grace, _ => true, NoIntents, out var warning);

        Assert.Empty(actions);
        Assert.Contains("cannot be read", warning);
        Assert.Contains("access denied", warning);
    }

    // ---- files of an existing bot

    [Fact]
    public void A_file_whose_size_and_time_are_unchanged_is_left_alone_without_looking_at_its_hash()
    {
        var id = Guid.NewGuid();
        var actions = Plan(Snapshot(("sales", [File("a.pdf", sha: "different-but-never-compared")])),
            Manifest(("sales", Recorded(("a.pdf", id, 10, "aa")))));

        Assert.Empty(actions);
    }

    [Fact]
    public void A_changed_size_with_the_same_content_only_refreshes_the_record()
    {
        var id = Guid.NewGuid();
        var actions = Plan(Snapshot(("sales", [File("a.pdf", size: 99, sha: "AA")])),
            Manifest(("sales", Recorded(("a.pdf", id, 10, "aa")))));

        var refresh = Assert.IsType<RefreshFileState>(Assert.Single(actions));
        Assert.Equal(id, refresh.RemoteId);
    }

    [Fact]
    public void Changed_content_replaces_the_old_file()
    {
        var id = Guid.NewGuid();
        var actions = Plan(Snapshot(("sales", [File("a.pdf", size: 99, sha: "bb")])),
            Manifest(("sales", Recorded(("a.pdf", id, 10, "aa")))));

        var replace = Assert.IsType<ReplaceFile>(Assert.Single(actions));
        Assert.Equal(id, replace.OldRemoteId);
    }

    [Fact]
    public void A_file_gone_from_the_folder_is_deleted_from_vault_ai()
    {
        var id = Guid.NewGuid();
        var actions = Plan(Snapshot(("sales", [File("keep.pdf")])),
            Manifest(("sales", Recorded(("keep.pdf", Guid.NewGuid(), 10, "aa"), ("gone.pdf", id, 10, "aa")))));

        var delete = Assert.IsType<DeleteFile>(Assert.Single(actions));
        Assert.Equal(("gone.pdf", id), (delete.RelativePath, delete.RemoteId));
    }

    [Fact]
    public void A_file_recorded_but_missing_from_the_remote_list_is_uploaded_again()
    {
        var id = Guid.NewGuid();
        var actions = Plan(Snapshot(("sales", [File("a.pdf")])),
            Manifest(("sales", Recorded(("a.pdf", id, 10, "aa")))),
            remote: []);

        Assert.IsType<UploadFile>(Assert.Single(actions));
    }

    [Fact]
    public void Without_a_remote_list_the_records_are_trusted()
    {
        var actions = Plan(Snapshot(("sales", [File("a.pdf")])),
            Manifest(("sales", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa")))), remote: null);

        Assert.Empty(actions);
    }

    // ---- the guards: a disappearance that may be an accident deletes nothing

    [Fact]
    public void An_existing_but_empty_bot_folder_keeps_its_files_and_says_so()
    {
        var id = Guid.NewGuid();
        var fs = Snapshot(("sales", []));
        var m = Manifest(("sales", Recorded(("a.pdf", id, 10, "aa"))));

        Assert.Empty(Plan(fs, m));
        Assert.Contains("is empty", Warning(fs, m));
    }

    [Fact]
    public void An_empty_folder_also_protects_the_remote_files_of_that_bot_from_the_orphan_sweep()
    {
        var id = Guid.NewGuid();
        var orphan = Remote(Guid.NewGuid(), "sales/stray.pdf", "bot:sales");
        var fs = Snapshot(("sales", []));
        var m = Manifest(("sales", Recorded(("a.pdf", id, 10, "aa"))));

        var actions = Plan(fs, m, remote: [Remote(id, "sales/a.pdf", "bot:sales"), orphan]);

        Assert.DoesNotContain(actions, a => a is DeleteOrphan);
    }

    [Fact]
    public void Every_bot_vanishing_at_once_deletes_nothing_except_what_was_deleted_on_purpose()
    {
        var fs = Snapshot();   // the root is readable and has no bot folders
        var m = Manifest(("sales", Recorded()), ("hr", Recorded()), ("ops", Recorded()));

        var actions = FolderChatbotPlanner.Plan(fs, m, null, Now, Grace, _ => true,
            new HashSet<string> { "hr" }, out var warning);

        Assert.Equal(new DeleteBot("hr"), Assert.Single(actions));   // only the one deleted on purpose
        Assert.Contains("vanished at once", warning);
    }

    [Fact]
    public void Every_bot_vanishing_is_no_accident_when_every_one_was_deleted_on_purpose()
    {
        var m = Manifest(("sales", Recorded()), ("hr", Recorded()));

        var actions = Plan(Snapshot(), m, intents: new HashSet<string> { "sales", "hr" });

        Assert.Equal(["hr", "sales"], actions.Cast<DeleteBot>().Select(a => a.Bot));
    }

    // ---- a folder that is gone: grace, then delete

    [Fact]
    public void A_folder_seen_missing_for_the_first_time_is_marked_not_deleted()
    {
        var m = Manifest(("sales", Recorded()), ("hr", Recorded()));
        var actions = Plan(Snapshot(("hr", [File("a.pdf")])), m);

        Assert.Contains(new MarkPendingDelete("sales", Now), actions);
        Assert.DoesNotContain(actions, a => a is DeleteBot);
    }

    [Fact]
    public void During_the_grace_period_nothing_happens_and_after_it_the_bot_is_deleted()
    {
        var keep = (Bot: "hr", Files: new[] { File("a.pdf") });
        var inGrace = Recorded(); inGrace.PendingDeleteSince = Now - TimeSpan.FromHours(23);
        var past = Recorded(); past.PendingDeleteSince = Now - TimeSpan.FromHours(25);

        var during = Plan(Snapshot(keep), Manifest(("sales", inGrace), ("hr", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa")))));
        var after = Plan(Snapshot(keep), Manifest(("sales", past), ("hr", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa")))));

        Assert.DoesNotContain(during, a => a.Bot == "sales");
        Assert.Contains(new DeleteBot("sales"), after);
    }

    [Fact]
    public void A_bot_deleted_on_purpose_is_deleted_without_grace()
    {
        var m = Manifest(("sales", Recorded()), ("hr", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa"))));

        var actions = Plan(Snapshot(("hr", [File("a.pdf")])), m, intents: new HashSet<string> { "sales" });

        Assert.Contains(new DeleteBot("sales"), actions);
    }

    [Fact]
    public void A_folder_that_comes_back_clears_the_pending_deletion()
    {
        var state = Recorded(("a.pdf", Guid.NewGuid(), 10, "aa"));
        state.PendingDeleteSince = Now - TimeSpan.FromHours(3);

        var actions = Plan(Snapshot(("sales", [File("a.pdf")])), Manifest(("sales", state)));

        Assert.Equal(new ClearPendingDelete("sales"), Assert.Single(actions));
    }

    [Fact]
    public void An_intended_deletion_is_ignored_when_the_folder_is_back_and_said()
    {
        var fs = Snapshot(("sales", [File("a.pdf")]));
        var m = Manifest(("sales", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa"))));
        var intents = new HashSet<string> { "sales" };

        var actions = FolderChatbotPlanner.Plan(fs, m, null, Now, Grace, _ => true, intents, out var warning);

        Assert.DoesNotContain(actions, a => a is DeleteBot);
        Assert.Contains("revived", warning);
        Assert.Equal(["sales"], FolderChatbotPlanner.StaleIntents(fs, m, intents));
    }

    [Fact]
    public void Stale_intents_are_those_of_bots_that_are_back_or_unknown_and_none_when_the_root_is_unreadable()
    {
        var m = Manifest(("gone", Recorded()), ("back", Recorded()));
        var fs = Snapshot(("back", [File("a.pdf")]));
        var intents = new HashSet<string> { "gone", "back", "never-existed" };

        Assert.Equal(["back", "never-existed"], FolderChatbotPlanner.StaleIntents(fs, m, intents));
        Assert.Empty(FolderChatbotPlanner.StaleIntents(
            new FolderSnapshot(false, new Dictionary<string, IReadOnlyList<LocalFile>>()), m, intents));
    }

    // ---- remote files nobody records

    [Fact]
    public void A_remote_file_no_bot_records_and_no_local_file_pairs_with_is_deleted()
    {
        var stray = Remote(Guid.NewGuid(), "sales/old/stray.pdf", "bot:sales");
        var actions = Plan(Snapshot(("sales", [File("a.pdf")])), Manifest(("sales", Recorded(("a.pdf", Guid.NewGuid(), 10, "aa")))),
            remote: [Remote(Guid.NewGuid(), "sales/a.pdf", "bot:sales"), stray]);

        var orphan = Assert.Single(actions.OfType<DeleteOrphan>());
        Assert.Equal(stray, orphan.File);
        Assert.Equal("sales", orphan.Bot);
    }

    /// <summary>The lost-manifest case: the file is in vault-ai under the right name and tag but nothing records it — the upload adopts it.</summary>
    [Fact]
    public void A_remote_file_that_pairs_with_an_unrecorded_local_file_is_left_for_the_upload_to_adopt()
    {
        var existing = Remote(Guid.NewGuid(), "sales/a.pdf", "bot:sales");

        var actions = Plan(Snapshot(("sales", [File("a.pdf")])), Manifest(("sales", Recorded())), remote: [existing]);

        Assert.DoesNotContain(actions, a => a is DeleteOrphan);
        Assert.IsType<UploadFile>(Assert.Single(actions));
    }

    [Fact]
    public void A_remote_file_with_no_bot_at_all_is_an_orphan_with_an_empty_bot()
    {
        var actions = Plan(Snapshot(), Manifest(), remote: [Remote(Guid.NewGuid(), "stray.pdf")]);

        Assert.Equal("", Assert.Single(actions.OfType<DeleteOrphan>()).Bot);
    }
}

public class FolderChatbotManifestTests
{
    [Fact]
    public void A_manifest_survives_a_round_trip_through_json()
    {
        var m = new FolderChatbotManifest
        {
            Owner = Guid.NewGuid(), Prefix = "acme", Url = "https://vault.example",
            Models = new FolderChatbotModels(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null),
            StorageId = Guid.NewGuid(), AgentId = Guid.NewGuid(), ChannelId = Guid.NewGuid(), AgentConfigHash = "h",
        };
        var bot = new BotState { PendingDeleteSince = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        bot.Files["a.pdf"] = new FileState(Guid.NewGuid(), 10, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "aa");
        bot.Unsupported["x.bin"] = "bb";
        bot.Connectors["crm"] = new ConnectorState(Guid.NewGuid(), "hash");
        bot.Scope = new ScopeState("be brief", [Guid.NewGuid()]);
        m.Bots["sales"] = bot;

        var read = FolderChatbotManifest.Parse(m.ToJson());

        Assert.Equal(m.Owner, read.Owner);
        Assert.Equal(m.Models, read.Models);
        Assert.Equal(bot.Files["a.pdf"], read.Bots["sales"].Files["a.pdf"]);
        Assert.Equal("bb", read.Bots["sales"].Unsupported["x.bin"]);
        Assert.Equal(bot.Connectors["crm"], read.Bots["sales"].Connectors["crm"]);
        Assert.Equal("be brief", read.Bots["sales"].Scope!.Instructions);
        Assert.Equal(bot.Scope.ConnectorIds, read.Bots["sales"].Scope!.ConnectorIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_text_is_an_empty_manifest(string? json)
    {
        var m = FolderChatbotManifest.Parse(json);

        Assert.Empty(m.Bots);
        Assert.Null(m.StorageId);
    }

    [Fact]
    public void Dictionaries_read_back_case_sensitively_and_null_entries_in_a_hand_edited_file_count_as_absent()
    {
        var json = """{"bots":{"Sales":{"files":{"A.pdf":{"remoteId":"00000000-0000-0000-0000-000000000001","size":1,"lastWrite":"2026-11-01T00:00:00+00:00","sha256":"aa"},"b.pdf":null}},"gone":null}}""";

        var m = FolderChatbotManifest.Parse(json);

        Assert.True(m.Bots.ContainsKey("Sales"));
        Assert.False(m.Bots.ContainsKey("sales"));            // ordinal
        Assert.False(m.Bots.ContainsKey("gone"));             // a null bot is dropped
        Assert.Single(m.Bots["Sales"].Files);                 // a null file is dropped
    }

    [Fact]
    public void A_clone_does_not_change_when_the_original_does()
    {
        var m = new FolderChatbotManifest();
        m.Bots["sales"] = new BotState();

        var clone = m.Clone();
        m.Bots["sales"].Files["new.pdf"] = new FileState(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, "aa");

        Assert.Empty(clone.Bots["sales"].Files);
    }
}

public class FolderChatbotFolderRulesTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData(".obsidian/app.json", true)]
    [InlineData("docs/.hidden/a.md", true)]
    [InlineData("docs/a.md", false)]
    [InlineData("~$lock.docx", true)]
    [InlineData("docs/~$lock.docx", true)]
    [InlineData("docs/Thumbs.db", true)]
    [InlineData("docs\\DESKTOP.INI", true)]
    [InlineData("sales", false)]
    public void Dot_segments_office_locks_and_system_files_are_excluded(string path, bool excluded)
    {
        Assert.Equal(excluded, FolderChatbotFolderRules.IsExcluded(path));
    }

    [Theory]
    [InlineData("AGENTS.md", true)]
    [InlineData("agents.MD", true)]
    [InlineData("mcp.json", true)]
    [InlineData("/Mcp.json", true)]
    [InlineData("docs/AGENTS.md", false)]     // deeper down it is a document
    [InlineData("readme.md", false)]
    public void Only_the_two_files_directly_under_a_bots_folder_are_its_settings(string path, bool settings)
    {
        Assert.Equal(settings, FolderChatbotFolderRules.IsBotSettingsFile(path));
    }

    [Theory]
    [InlineData("AGENTS.md", true)]            // the root's
    [InlineData("sales/AGENTS.md", true)]
    [InlineData("sales/mcp.json", true)]
    [InlineData("mcp.json", false)]            // no root-level mcp.json
    [InlineData("sales/docs/AGENTS.md", false)]
    [InlineData(".git/AGENTS.md", false)]      // inside an excluded folder
    public void A_watcher_can_tell_a_settings_path_from_the_root(string path, bool settings)
    {
        Assert.Equal(settings, FolderChatbotFolderRules.IsSettingsPath(path));
    }

    [Fact]
    public void The_built_in_instructions_come_first_and_are_never_replaced()
    {
        var withRoot = FolderChatbotFolderRules.ComposeAgentInstructions("  Answer in French.  ");

        Assert.StartsWith(FolderChatbotFolderRules.DefaultInstructions.Trim(), withRoot);
        Assert.EndsWith("\n\nAnswer in French.", withRoot);
        Assert.Equal(FolderChatbotFolderRules.DefaultInstructions.Trim(), FolderChatbotFolderRules.ComposeAgentInstructions(null));
        Assert.Equal(FolderChatbotFolderRules.DefaultInstructions.Trim(), FolderChatbotFolderRules.ComposeAgentInstructions("   \n "));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("  Be brief.\n", "Be brief.")]
    public void A_scopes_instructions_are_the_bots_file_trimmed_or_none(string? file, string? expected)
    {
        Assert.Equal(expected, FolderChatbotFolderRules.ComposeScopeInstructions(file));
    }

    /// <summary>Existing agents run with these words; changing them changes what an installed agent does the next time its configuration is applied.</summary>
    [Fact]
    public void The_built_in_instructions_are_kept_as_they_are()
    {
        Assert.StartsWith("당신은 이 챗봇에 연결된 저장소의 문서만 근거로 답한다.", FolderChatbotFolderRules.DefaultInstructions);
        Assert.Contains("확신이 없는 조치를 단정하지 않는다.", FolderChatbotFolderRules.DefaultInstructions);
        Assert.Equal(6, FolderChatbotFolderRules.DefaultInstructions.Trim().Split('\n').Length);
    }
}
