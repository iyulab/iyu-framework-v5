using System.Net;
using System.Security.Cryptography;
using System.Text;
using Iyu.VaultAI.FolderChatbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// One whole check - plan, then execute - against an in-memory folder and an in-memory vault-ai. What matters in production: a file is
/// uploaded once and not again, a copy already there is adopted rather than uploaded twice, a failure leaves only that item for the next
/// check, a refused file is not sent again until its content changes, and nothing is deleted on a hunch.
/// </summary>
public class FolderChatbotSyncExecutorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    private sealed class Rig
    {
        public FakeFolderChatbotAdmin Admin { get; } = new();
        public FakeFolderChatbotSource Source { get; } = new();
        public FolderChatbotManifest Manifest { get; set; } = new() { Owner = Guid.NewGuid() };
        public FolderChatbotModels Models { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public HashSet<string> Intents { get; } = [];
        public long MaxFileBytes { get; set; }
        public List<string> Deleted { get; } = [];

        public async Task<FolderChatbotSyncExecutor> CheckAsync(bool roundTripManifest = true)
        {
            var snapshot = Source.ReadSnapshot(Manifest);
            var exec = new FolderChatbotSyncExecutor(Admin, "kb-", Source, new FolderChatbotConfig(Source, Models), Manifest, () => { },
                NullLogger.Instance, maxFileBytes: MaxFileBytes, deleteCompleted: Deleted.Add, embeddingExplicit: true);
            var (stop, remote) = await exec.PrepareAsync(default);
            if (stop is not null) return exec;
            var actions = FolderChatbotPlanner.Plan(snapshot, Manifest, remote, Now, Grace, FolderChatbotSupportedFiles.IsSupported, Intents, out _);
            await exec.ExecuteAsync(snapshot, actions, default);
            // A real host keeps the manifest as JSON between checks; what the engine records must survive that.
            if (roundTripManifest) Manifest = FolderChatbotManifest.Parse(Manifest.ToJson());
            return exec;
        }

        public IEnumerable<string> Uploads => Admin.Calls.Where(c => c.StartsWith("upload:", StringComparison.Ordinal));
        public FakeFolderChatbotAdmin.AgentRow Agent => Admin.Agents[Manifest.AgentId!.Value];
        public List<RemoteFile> RemoteFiles => Admin.Files[Manifest.StorageId!.Value];
    }

    private static Exception Http(HttpStatusCode code) => new HttpRequestException(code.ToString(), null, code);

    // ---------------- the first check and the ones after ----------------

    [Fact]
    public async Task The_first_check_makes_the_shared_objects_uploads_each_file_once_and_gives_each_bot_a_scope()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        rig.Source.Put("handbook", "deep/guide.pdf", "pdf");
        rig.Source.Put("faq", "q.txt", "question");

        var exec = await rig.CheckAsync();

        Assert.Equal(0, exec.Failed);
        Assert.Equal(["upload:faq/q.txt", "upload:handbook/deep/guide.pdf", "upload:handbook/intro.md"], rig.Uploads.Order(StringComparer.Ordinal));
        Assert.Equal(3, rig.RemoteFiles.Count);
        Assert.All(rig.RemoteFiles, f => Assert.Single(f.Tags));
        Assert.Contains(rig.RemoteFiles, f => f.FileName == "handbook/intro.md" && f.Tags.SequenceEqual([FolderChatbotNames.TagOf("handbook")]));
        Assert.Equal(["faq", "handbook"], rig.Agent.Scopes.Select(s => s.Name));
        Assert.Equal(2, rig.Manifest.Bots.Count);
        Assert.Equal(2, rig.Manifest.Bots["handbook"].Files.Count);
    }

    [Fact]
    public async Task A_second_check_with_nothing_changed_changes_nothing()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.CheckAsync();
        rig.Admin.Calls.Clear();

        var exec = await rig.CheckAsync();

        Assert.Equal(0, exec.Applied);
        Assert.Equal(0, exec.Failed);
        Assert.Empty(rig.Admin.Calls);
    }

    [Fact]
    public async Task A_file_whose_content_changed_is_replaced_and_one_whose_timestamp_only_changed_is_not_uploaded_again()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        rig.Source.Put("handbook", "same.md", "same");
        await rig.CheckAsync();
        rig.Admin.Calls.Clear();

        rig.Source.Put("handbook", "intro.md", "hello, changed", at: rig.Now);
        rig.Source.Put("handbook", "same.md", "same", at: rig.Now);   // touched, not edited
        await rig.CheckAsync();

        Assert.Equal(["delete-file:handbook/intro.md", "upload:handbook/intro.md"], rig.Admin.Calls);
        Assert.Contains(rig.RemoteFiles, f => f.FileName == "handbook/intro.md" && f.ContentSize == 14);
    }

    [Fact]
    public async Task A_file_deleted_from_the_folder_is_deleted_remotely()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("handbook", "b.md", "b");
        await rig.CheckAsync();

        rig.Source.Bots["handbook"].Remove("b.md");
        await rig.CheckAsync();

        Assert.DoesNotContain(rig.RemoteFiles, f => f.FileName == "handbook/b.md");
        Assert.Single(rig.Manifest.Bots["handbook"].Files);
    }

    // ---------------- adopting, orphans, unsupported, oversize ----------------

    [Fact]
    public async Task With_the_manifest_lost_a_copy_already_in_the_storage_is_adopted_not_uploaded_again()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.CheckAsync();
        rig.Manifest = new FolderChatbotManifest { Owner = rig.Manifest.Owner };   // lost
        rig.Admin.Calls.Clear();

        var exec = await rig.CheckAsync();

        Assert.Empty(rig.Uploads);
        Assert.Equal(0, exec.Failed);
        Assert.Single(rig.Manifest.Bots["handbook"].Files);
        Assert.Single(rig.RemoteFiles);
    }

    [Fact]
    public async Task A_failed_copy_of_the_same_name_is_replaced_and_extra_copies_are_removed()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "hello");
        await rig.CheckAsync();
        var remote = rig.RemoteFiles;   // the storage's list, taken before the manifest is lost
        rig.Manifest = new FolderChatbotManifest { Owner = rig.Manifest.Owner };
        var tag = FolderChatbotNames.TagOf("handbook");
        remote.Clear();
        remote.Add(new RemoteFile(Guid.NewGuid(), "handbook/intro.md", 5, "failed") { Tags = [tag] });
        remote.Add(new RemoteFile(Guid.NewGuid(), "handbook/intro.md", 5, "cancelled") { Tags = [tag] });
        rig.Admin.Calls.Clear();

        await rig.CheckAsync();

        var only = Assert.Single(remote);
        Assert.Equal("indexed", only.Status);
        Assert.Single(rig.Uploads);
    }

    [Fact]
    public async Task A_remote_file_no_bot_has_is_cleaned_up()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.CheckAsync();
        rig.RemoteFiles.Add(new RemoteFile(Guid.NewGuid(), "ghost/old.md", 3, "indexed") { Tags = [FolderChatbotNames.TagOf("ghost")] });

        await rig.CheckAsync();

        Assert.DoesNotContain(rig.RemoteFiles, f => f.FileName == "ghost/old.md");
    }

    [Fact]
    public async Task A_file_the_server_refuses_is_skipped_not_sent_again_and_tried_once_more_when_its_content_changes()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "odd.md", "v1");
        rig.Admin.UploadFailure = _ => new VaultAIUnsupportedFileException("no");

        var first = await rig.CheckAsync();
        Assert.Equal(1, first.Skipped);
        Assert.Equal(0, first.Failed);
        Assert.Single(rig.Manifest.Bots["handbook"].Unsupported);

        rig.Admin.Calls.Clear();
        var second = await rig.CheckAsync();
        Assert.Empty(rig.Uploads);   // the same content is not sent again
        Assert.Equal(1, second.Skipped);

        rig.Admin.UploadFailure = null;
        rig.Source.Put("handbook", "odd.md", "v2 - now acceptable");
        await rig.CheckAsync();
        Assert.Single(rig.Uploads);
        Assert.Empty(rig.Manifest.Bots["handbook"].Unsupported);
        Assert.Single(rig.RemoteFiles);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_skipped_and_goes_up_once_the_limit_allows_it()
    {
        var rig = new Rig { MaxFileBytes = 10 };
        rig.Source.Put("handbook", "big.md", new string('x', 50));
        rig.Source.Put("handbook", "small.md", "ok");

        var first = await rig.CheckAsync();
        Assert.Equal(1, first.Skipped);
        Assert.Equal(["upload:handbook/small.md"], rig.Uploads);

        rig.MaxFileBytes = 0;
        rig.Admin.Calls.Clear();
        await rig.CheckAsync();
        Assert.Equal(["upload:handbook/big.md"], rig.Uploads);
    }

    // ---------------- failure leaves only that item ----------------

    [Fact]
    public async Task One_upload_failing_does_not_stop_the_others_and_the_next_check_retries_only_it()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("handbook", "b.md", "b");
        rig.Source.Put("handbook", "c.md", "c");
        rig.Admin.UploadFailure = name => name.EndsWith("b.md", StringComparison.Ordinal) ? Http(HttpStatusCode.InternalServerError) : null;

        var first = await rig.CheckAsync();
        Assert.Equal(1, first.Failed);
        Assert.Equal(["a.md", "c.md"], rig.Manifest.Bots["handbook"].Files.Keys.Order(StringComparer.Ordinal));

        rig.Admin.UploadFailure = null;
        rig.Admin.Calls.Clear();
        var second = await rig.CheckAsync();
        Assert.Equal(0, second.Failed);
        Assert.Equal(["upload:handbook/b.md"], rig.Uploads);
    }

    [Fact]
    public async Task A_file_being_indexed_cannot_be_replaced_yet_and_nothing_is_changed_until_the_next_check()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "intro.md", "v1");
        await rig.CheckAsync();
        var recorded = rig.Manifest.Bots["handbook"].Files["intro.md"];

        rig.Source.Put("handbook", "intro.md", "v2", at: rig.Now);
        rig.Admin.DeleteFileFailure = _ => new VaultAIConflictException("indexing");
        var busy = await rig.CheckAsync();

        Assert.Equal(1, busy.Failed);
        Assert.Equal(recorded, rig.Manifest.Bots["handbook"].Files["intro.md"]);   // still the old record: no upload happened without the delete
        Assert.Single(rig.Uploads);   // only the first check's upload

        rig.Admin.DeleteFileFailure = null;
        await rig.CheckAsync();
        Assert.Equal(2, rig.Uploads.Count());
    }

    [Fact]
    public async Task A_storage_lost_during_an_upload_is_recorded_and_the_next_check_makes_a_new_one_and_uploads_again()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.CheckAsync();
        var oldStorage = rig.Manifest.StorageId!.Value;
        rig.Source.Put("handbook", "b.md", "b");
        rig.Admin.UploadFailure = _ => Http(HttpStatusCode.NotFound);

        var broken = await rig.CheckAsync();
        Assert.True(broken.Failed > 0);
        Assert.Null(rig.Manifest.StorageId);

        rig.Admin.UploadFailure = null;
        rig.Admin.Storages.Remove(oldStorage);
        await rig.CheckAsync();
        Assert.NotNull(rig.Manifest.StorageId);
        Assert.NotEqual(oldStorage, rig.Manifest.StorageId);
        Assert.Equal(["a.md", "b.md"], rig.Manifest.Bots["handbook"].Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, rig.RemoteFiles.Count);
    }

    [Fact]
    public async Task Without_a_readable_file_list_the_check_goes_on_with_the_records_and_cleans_up_nothing_remote()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.CheckAsync();
        rig.RemoteFiles.Add(new RemoteFile(Guid.NewGuid(), "ghost/old.md", 3, "indexed") { Tags = [FolderChatbotNames.TagOf("ghost")] });
        rig.Source.Put("handbook", "b.md", "b");
        rig.Admin.ListFilesFailure = Http(HttpStatusCode.BadGateway);

        var exec = await rig.CheckAsync();

        Assert.Equal(0, exec.Failed);
        Assert.Contains(rig.RemoteFiles, f => f.FileName == "ghost/old.md");   // left for a check that can see the list
        Assert.Contains(rig.RemoteFiles, f => f.FileName == "handbook/b.md");
    }

    // ---------------- not deleting on a hunch ----------------

    [Fact]
    public async Task An_unreadable_folder_root_does_nothing_remote()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.CheckAsync();
        rig.Source.Unreadable = true;
        rig.Admin.Calls.Clear();

        await rig.CheckAsync();

        Assert.DoesNotContain(rig.Admin.Calls, c => c.StartsWith("delete", StringComparison.Ordinal));
        Assert.Single(rig.RemoteFiles);
    }

    [Fact]
    public async Task A_bot_whose_folder_is_gone_waits_out_the_grace_period_and_is_then_deleted_completely()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("faq", "q.md", "q");
        rig.Source.Mcp["faq"] = new SettingsText(
            """{"mcpServers":{"search":{"url":"https://mcp.example.test/sse"}}}""", null);
        await rig.CheckAsync();
        Assert.Single(rig.Admin.Connectors);

        rig.Source.Bots.Remove("faq");
        await rig.CheckAsync();   // first sight of the missing folder: only marked
        Assert.NotNull(rig.Manifest.Bots["faq"].PendingDeleteSince);
        Assert.Equal(2, rig.RemoteFiles.Count);

        rig.Now += Grace + TimeSpan.FromMinutes(1);
        await rig.CheckAsync();

        Assert.DoesNotContain("faq", rig.Manifest.Bots.Keys);
        Assert.Equal(["handbook/a.md"], rig.RemoteFiles.Select(f => f.FileName));
        Assert.Equal(["handbook"], rig.Agent.Scopes.Select(s => s.Name));
        Assert.Empty(rig.Admin.Connectors);        // its connector went too, after the agent stopped using it
        Assert.Equal(["faq"], rig.Deleted);         // the host is told
    }

    [Fact]
    public async Task A_removed_bots_connectors_are_not_deleted_while_the_agent_still_points_at_them()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("faq", "q.md", "q");
        rig.Source.Mcp["faq"] = new SettingsText("""{"mcpServers":{"search":{"url":"https://mcp.example.test/sse"}}}""", null);
        await rig.CheckAsync();
        rig.Source.Bots.Remove("faq");
        await rig.CheckAsync();
        rig.Now += Grace + TimeSpan.FromMinutes(1);
        rig.Admin.UpdateAgentFailure = Http(HttpStatusCode.InternalServerError);

        var exec = await rig.CheckAsync();

        Assert.True(exec.Failed > 0);
        Assert.Single(rig.Admin.Connectors);                 // the agent was not updated, so it still names the scope and its connector
        Assert.Contains("faq", rig.Manifest.Bots.Keys);      // and the bot's record stays so the next check finishes the job
        Assert.Empty(rig.Deleted);

        rig.Admin.UpdateAgentFailure = null;
        await rig.CheckAsync();
        Assert.Empty(rig.Admin.Connectors);
        Assert.DoesNotContain("faq", rig.Manifest.Bots.Keys);
        Assert.Equal(["faq"], rig.Deleted);
    }

    [Fact]
    public async Task Deleting_a_bot_also_removes_remote_files_carrying_its_tag_that_its_record_lost()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("faq", "q.md", "q");
        await rig.CheckAsync();
        var leftover = new RemoteFile(Guid.NewGuid(), "faq/lost-record.md", 3, "indexed") { Tags = [FolderChatbotNames.TagOf("faq")] };
        rig.RemoteFiles.Add(leftover);
        var cx = new FolderChatbotSyncContext(rig.Admin, "kb-", new FolderChatbotConfig(rig.Source, rig.Models), rig.Manifest, () => { },
            NullLogger.Instance);
        var files = new FolderChatbotFileSync(cx, rig.Source, rig.RemoteFiles.ToList());

        await files.DeleteAllAsync("faq", rig.Manifest.Bots["faq"], default);

        Assert.DoesNotContain(rig.RemoteFiles, f => f.Id == leftover.Id);
        Assert.DoesNotContain(rig.RemoteFiles, f => f.FileName == "faq/q.md");
        Assert.Contains(rig.RemoteFiles, f => f.FileName == "handbook/a.md");
    }

    [Fact]
    public async Task A_folder_that_comes_back_during_the_grace_period_keeps_its_bot()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("faq", "q.md", "q");
        await rig.CheckAsync();
        var kept = rig.Source.Bots["faq"];
        rig.Source.Bots.Remove("faq");
        await rig.CheckAsync();

        rig.Source.Bots["faq"] = kept;
        rig.Now += Grace + TimeSpan.FromHours(1);
        await rig.CheckAsync();

        Assert.Contains("faq", rig.Manifest.Bots.Keys);
        Assert.Null(rig.Manifest.Bots["faq"].PendingDeleteSince);
        Assert.Equal(2, rig.RemoteFiles.Count);
    }

    // ---------------- shared objects through the executor ----------------

    [Fact]
    public async Task A_storage_of_another_instance_stops_the_check_with_a_warning_and_changes_nothing()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Admin.AddStorage("kb-all", new FolderChatbotStorageMeta(Guid.NewGuid(), new Dictionary<string, FolderChatbotBotMeta>()).Format());

        var exec = await rig.CheckAsync(roundTripManifest: false);

        Assert.Empty(rig.Admin.Calls);
        Assert.Empty(rig.Manifest.Bots);
        Assert.Equal(0, exec.Applied);
    }

    [Fact]
    public async Task A_change_to_the_instructions_alone_updates_the_agent_without_touching_any_file()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        await rig.CheckAsync();
        rig.Admin.Calls.Clear();

        rig.Source.Agents["handbook"] = new SettingsText("Be exact.", null);
        await rig.CheckAsync();

        Assert.Equal(["update-agent"], rig.Admin.Calls);
        Assert.Equal("Be exact.", rig.Agent.Scopes.Single().Instructions);
    }

    [Fact]
    public async Task The_storage_description_carries_each_bots_state_after_the_check()
    {
        var rig = new Rig();
        rig.Source.Put("handbook", "a.md", "a");
        rig.Source.Put("handbook", "odd.md", "x");
        rig.Admin.UploadFailure = name => name.EndsWith("odd.md", StringComparison.Ordinal) ? new VaultAIUnsupportedFileException("no") : null;

        await rig.CheckAsync();

        var description = rig.Admin.Storages[rig.Manifest.StorageId!.Value].Description;
        Assert.True(FolderChatbotStorageMeta.TryParse(description, out var meta));
        Assert.Equal(1, meta!.Bots["handbook"].Skipped);
        Assert.Equal(rig.Manifest.Owner, meta.Owner);
    }
}
