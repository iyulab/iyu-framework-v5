using Iyu.VaultAI.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The folders on a real file system: which first-level folders are bots, which files count, how settings files are found and read, and
/// that a folder that cannot be read is reported as unreadable instead of as empty (an empty folder deletes remotely).
/// </summary>
public sealed class FileSystemFolderChatbotSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "iyu-folder-source-" + Guid.NewGuid().ToString("n")[..8])).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private FolderSnapshot Snapshot(FolderChatbotManifest? manifest = null)
        => new FileSystemFolderChatbotSource(_root).ReadSnapshot(manifest ?? new FolderChatbotManifest());

    [Fact]
    public void Each_first_level_folder_is_a_bot_and_its_files_are_listed_with_relative_slash_paths()
    {
        Write("handbook/intro.md");
        Write("handbook/deep/guide.pdf");
        Write("faq/q.txt");
        Write("loose-file-in-root.md");

        var snapshot = Snapshot();

        Assert.True(snapshot.RootReadable);
        Assert.Equal(["faq", "handbook"], snapshot.Bots.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["deep/guide.pdf", "intro.md"], snapshot.Bots["handbook"].Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(snapshot.Bots.Values.SelectMany(f => f), f => Assert.Equal(64, f.Sha256.Length));
    }

    [Fact]
    public void Dot_folders_office_lock_files_and_a_bots_two_settings_files_are_not_documents()
    {
        Write("handbook/intro.md");
        Write("handbook/.git/config");
        Write("handbook/~$intro.docx");
        Write("handbook/Thumbs.db");
        Write("handbook/AGENTS.md");
        Write("handbook/MCP.JSON");
        Write("handbook/deeper/AGENTS.md");   // the same name deeper down is a document
        Write(".hidden-bot/a.md");

        var snapshot = Snapshot();

        Assert.Equal(["handbook"], snapshot.Bots.Keys);
        Assert.Equal(["deeper/AGENTS.md", "intro.md"], snapshot.Bots["handbook"].Select(f => f.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_missing_root_is_unreadable_not_empty()
    {
        var snapshot = new FileSystemFolderChatbotSource(Path.Combine(_root, "no-such-folder")).ReadSnapshot(new FolderChatbotManifest());

        Assert.False(snapshot.RootReadable);
        Assert.NotNull(snapshot.UnreadableReason);
    }

    [Fact]
    public void A_file_whose_size_and_time_match_the_record_is_not_hashed_again()
    {
        Write("handbook/intro.md", "hello");
        var info = new FileInfo(Path.Combine(_root, "handbook", "intro.md"));
        var manifest = new FolderChatbotManifest();
        manifest.Bots["handbook"] = new BotState
        {
            Files = { ["intro.md"] = new FileState(Guid.NewGuid(), info.Length, new DateTimeOffset(info.LastWriteTimeUtc), "recorded-hash") },
        };

        var file = Assert.Single(Snapshot(manifest).Bots["handbook"]);

        Assert.Equal("recorded-hash", file.Sha256);   // reused, so a large unchanged tree is cheap to scan
    }

    [Fact]
    public void A_file_that_changed_is_hashed_again()
    {
        Write("handbook/intro.md", "hello");
        var manifest = new FolderChatbotManifest();
        manifest.Bots["handbook"] = new BotState
        {
            Files = { ["intro.md"] = new FileState(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, "stale") },
        };

        var file = Assert.Single(Snapshot(manifest).Bots["handbook"]);

        Assert.NotEqual("stale", file.Sha256);
    }

    [Fact]
    public void A_file_locked_by_a_writer_is_left_out_if_new_and_kept_as_recorded_if_already_uploaded()
    {
        Write("handbook/new.md", "n");
        Write("handbook/old.md", "o");
        var manifest = new FolderChatbotManifest();
        manifest.Bots["handbook"] = new BotState
        {
            Files = { ["old.md"] = new FileState(Guid.NewGuid(), 99, DateTimeOffset.UnixEpoch, "recorded") },
        };

        using var lockNew = new FileStream(Path.Combine(_root, "handbook", "new.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lockOld = new FileStream(Path.Combine(_root, "handbook", "old.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var files = Snapshot(manifest).Bots["handbook"];

        Assert.DoesNotContain(files, f => f.RelativePath == "new.md");   // not seen yet: looked at again next check
        var old = Assert.Single(files, f => f.RelativePath == "old.md");
        Assert.Equal("recorded", old.Sha256);                              // leaving it out would delete it remotely
    }

    [Fact]
    public void Settings_files_are_found_whatever_their_case_and_empty_ones_count_as_none()
    {
        Write("AGENTS.md", "  Answer in Korean.  \n");
        Write("handbook/agents.MD", "Be brief.");
        Write("faq/AGENTS.md", "   \n");
        Write("handbook/Mcp.Json", "{\"mcpServers\":{}}");
        var source = new FileSystemFolderChatbotSource(_root);

        Assert.Equal("Answer in Korean.", source.ReadAgents(null, out _).Text);
        Assert.Equal("Be brief.", source.ReadAgents("handbook", out _).Text);
        Assert.Null(source.ReadAgents("faq", out _).Text);
        Assert.Null(source.ReadAgents("missing-bot", out _).Text);
        Assert.Contains("mcpServers", source.ReadMcp("handbook", out _).Text);
        Assert.Null(source.ReadMcp("faq", out _).Text);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_is_an_error_not_an_absence()
    {
        Write("handbook/AGENTS.md", "rules");
        using var hold = new FileStream(Path.Combine(_root, "handbook", "AGENTS.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var source = new FileSystemFolderChatbotSource(_root);

        var result = source.ReadAgents("handbook", out _);

        Assert.Null(result.Text);
        Assert.NotNull(result.Error);   // the engine then keeps the instructions as last applied instead of treating them as removed
    }

    [Fact]
    public void A_file_is_opened_by_bot_and_relative_path()
    {
        Write("handbook/deep/guide.md", "body");
        var source = new FileSystemFolderChatbotSource(_root);

        using var stream = source.OpenRead("handbook", "deep/guide.md");
        using var reader = new StreamReader(stream);

        Assert.Equal("body", reader.ReadToEnd());
    }

    // ---- links: a link inside a bot's folder must never lead the engine out of it

    /// <summary>A directory link: a symbolic link, or on Windows a junction (which needs no special privilege).</summary>
    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && OperatingSystem.IsWindows()) { }

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "this environment cannot make a directory link, which the test needs");
    }

    /// <summary>A file symbolic link, or false where the environment does not allow making one (Windows without the privilege).</summary>
    private static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private string Outside()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root + "-outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not for the chatbot");
        return outside;
    }

    [Fact]
    public void A_folder_link_inside_a_bot_is_not_followed_so_files_outside_the_bot_are_never_listed()
    {
        var outside = Outside();
        try
        {
            Write("handbook/intro.md");
            LinkDirectory(Path.Combine(_root, "handbook", "shortcut"), outside);

            var files = Snapshot().Bots["handbook"].Select(f => f.RelativePath).ToList();

            Assert.Equal(["intro.md"], files);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void A_file_link_inside_a_bot_is_not_listed_where_links_can_be_made()
    {
        var outside = Outside();
        try
        {
            Write("handbook/intro.md");
            if (!TryLinkFile(Path.Combine(_root, "handbook", "stolen.md"), Path.Combine(outside, "secret.txt"))) return;   // no privilege here; the folder-link test covers the same rule

            Assert.Equal(["intro.md"], Snapshot().Bots["handbook"].Select(f => f.RelativePath));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void A_path_that_became_a_link_after_the_scan_is_not_opened()
    {
        var outside = Outside();
        try
        {
            Write("handbook/intro.md");
            LinkDirectory(Path.Combine(_root, "handbook", "swapped"), outside);
            var source = new FileSystemFolderChatbotSource(_root);

            Assert.Throws<IOException>(() => source.OpenRead("handbook", "swapped/secret.txt"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Theory]
    [InlineData("../other/secret.md")]
    [InlineData("a/../../other/secret.md")]
    public void A_path_that_leaves_the_bots_folder_is_not_opened(string relative)
    {
        Write("handbook/intro.md");
        Write("other/secret.md");
        var source = new FileSystemFolderChatbotSource(_root);

        Assert.Throws<IOException>(() => source.OpenRead("handbook", relative));
    }

    [Fact]
    public void A_settings_file_that_is_a_link_is_not_read_as_the_bots_instructions()
    {
        var outside = Outside();
        try
        {
            Write("handbook/intro.md");
            if (!TryLinkFile(Path.Combine(_root, "handbook", "AGENTS.md"), Path.Combine(outside, "secret.txt"))) return;

            var result = new FileSystemFolderChatbotSource(_root).ReadAgents("handbook", out _);

            Assert.Null(result.Text);
            Assert.NotNull(result.Error);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void A_bot_folder_that_is_itself_a_link_set_up_by_the_administrator_still_works()
    {
        var shared = Outside();
        try
        {
            File.WriteAllText(Path.Combine(shared, "policy.md"), "policy");
            LinkDirectory(Path.Combine(_root, "shared-docs"), shared);
            var source = new FileSystemFolderChatbotSource(_root);

            var files = source.ReadSnapshot(new FolderChatbotManifest()).Bots["shared-docs"];

            Assert.Contains(files, f => f.RelativePath == "policy.md");
            using var stream = source.OpenRead("shared-docs", "policy.md");
            Assert.True(stream.Length > 0);
        }
        finally { Directory.Delete(shared, recursive: true); }
    }

    [Fact]
    public void A_settings_file_in_a_bot_folder_that_is_itself_a_link_is_still_read()
    {
        var shared = Outside();
        try
        {
            File.WriteAllText(Path.Combine(shared, "AGENTS.md"), "  answer briefly  ");
            LinkDirectory(Path.Combine(_root, "shared-docs"), shared);

            var result = new FileSystemFolderChatbotSource(_root).ReadAgents("shared-docs", out _);

            Assert.Null(result.Error);
            Assert.Equal("answer briefly", result.Text);
        }
        finally { Directory.Delete(shared, recursive: true); }
    }

    [Fact]
    public void A_folder_swapped_for_a_link_only_for_the_instant_of_the_open_is_caught_by_where_the_open_file_really_is()
    {
        var outside = Outside();
        try
        {
            Write("handbook/docs/secret.txt", "inside");
            var docs = Path.Combine(_root, "handbook", "docs");
            var aside = docs + ".real";
            var source = new FileSystemFolderChatbotSource(_root)
            {
                // After the path was found clean: the folder is replaced by a link to the outside, the file opens through it ...
                AfterFirstLinkCheck = () => { Directory.Move(docs, aside); LinkDirectory(docs, outside); },
                // ... and the folder is put back before the path is looked at again, so only the open handle tells.
                AfterOpen = () => { Directory.Delete(docs); Directory.Move(aside, docs); },
            };

            Assert.Throws<IOException>(() => source.OpenRead("handbook", "docs/secret.txt"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void A_settings_file_swapped_for_a_link_only_for_the_instant_of_the_open_is_not_read_as_the_bots_instructions()
    {
        var outside = Outside();
        try
        {
            Write("handbook/AGENTS.md", "the real instructions");
            var agents = Path.Combine(_root, "handbook", "AGENTS.md");
            var aside = agents + ".real";
            var swapped = false;
            var source = new FileSystemFolderChatbotSource(_root)
            {
                AfterFirstLinkCheck = () =>
                {
                    File.Move(agents, aside);
                    swapped = TryLinkFile(agents, Path.Combine(outside, "secret.txt"));
                    if (!swapped) File.Move(aside, agents);   // no privilege to make a file link here; the folder-swap test covers the rule
                },
                AfterOpen = () =>
                {
                    if (!swapped) return;
                    File.Delete(agents);
                    File.Move(aside, agents);
                },
            };

            var result = source.ReadAgents("handbook", out _);

            if (!swapped) return;
            Assert.Null(result.Text);
            Assert.NotNull(result.Error);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void The_real_path_of_an_open_file_is_where_it_lives_whatever_path_opened_it()
    {
        var shared = Outside();
        try
        {
            File.WriteAllText(Path.Combine(shared, "policy.md"), "policy");
            LinkDirectory(Path.Combine(_root, "shared-docs"), shared);

            using var stream = new FileStream(Path.Combine(_root, "shared-docs", "policy.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = FolderChatbotRealPath.OfOpenFile(stream.SafeFileHandle);

            if (actual is null) return;   // an operating system that cannot say where an open handle is
            Assert.Equal(Path.Combine(FolderChatbotRealPath.OfDirectory(shared)!, "policy.md"), actual, ignoreCase: OperatingSystem.IsWindows());
        }
        finally { Directory.Delete(shared, recursive: true); }
    }

    [Fact]
    public void Resolving_a_path_follows_every_link_on_the_way_including_a_link_that_points_at_a_link()
    {
        Write("real/policy.md");
        LinkDirectory(Path.Combine(_root, "second"), Path.Combine(_root, "real"));
        LinkDirectory(Path.Combine(_root, "first"), Path.Combine(_root, "second"));

        var resolved = FolderChatbotRealPath.Resolve(Path.Combine(_root, "first", "policy.md"));

        Assert.Equal(Path.Combine(_root, "real", "policy.md"), resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void The_file_manifest_store_writes_atomically_reads_back_and_keeps_a_copy_on_request()
    {
        var path = Path.Combine(_root, "state", "manifest.json");
        var store = new FileFolderChatbotManifestStore(path);

        Assert.Null(store.Read());
        store.Write("{\"a\":1}");
        store.Backup();
        store.Write("{\"a\":2}");

        Assert.Equal("{\"a\":2}", store.Read());
        Assert.Equal("{\"a\":1}", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));   // no temporary file is left behind
    }
}
