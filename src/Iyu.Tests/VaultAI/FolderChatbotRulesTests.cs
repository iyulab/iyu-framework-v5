using Iyu.VaultAI.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The names and the stored line a folder chatbot writes on the vault-ai side are a data format that installations already hold,
/// so these tests pin the exact text, not just a round trip: a different spelling would make a new engine see every existing
/// file as foreign.
/// </summary>
public class FolderChatbotNamesTests
{
    [Fact]
    public void The_shared_objects_are_named_prefix_all()
    {
        Assert.Equal("acmeall", FolderChatbotNames.SharedName("acme"));
        Assert.Equal("all", FolderChatbotNames.SharedSuffix);
    }

    [Fact]
    public void A_bots_files_carry_the_tag_bot_colon_name_and_are_named_bot_slash_path()
    {
        Assert.Equal("bot:sales", FolderChatbotNames.TagOf("sales"));
        Assert.Equal("sales/2026/q3.pdf", FolderChatbotNames.RemoteFileName("sales", "2026/q3.pdf"));
    }

    [Fact]
    public void A_remote_file_belongs_to_the_bot_its_tag_names_before_the_bot_its_name_starts_with()
    {
        var file = new RemoteFile(Guid.NewGuid(), "old/x.pdf", 1, "indexed") { Tags = ["other", "bot:sales"] };

        Assert.Equal("sales", FolderChatbotNames.BotOf(file));
    }

    [Fact]
    public void Without_a_bot_tag_the_first_segment_of_the_name_is_the_bot()
    {
        var file = new RemoteFile(Guid.NewGuid(), "sales/x.pdf", 1, "indexed");

        Assert.Equal("sales", FolderChatbotNames.BotOf(file));
    }

    [Theory]
    [InlineData("x.pdf")]          // no segment
    [InlineData("/x.pdf")]         // empty first segment
    public void A_file_that_neither_tag_nor_name_places_belongs_to_no_bot(string name)
    {
        Assert.Null(FolderChatbotNames.BotOf(new RemoteFile(Guid.NewGuid(), name, 1, "indexed")));
    }

    [Fact]
    public void A_bare_bot_prefix_tag_does_not_count_as_a_bot()
    {
        var file = new RemoteFile(Guid.NewGuid(), "x.pdf", 1, "indexed") { Tags = ["bot:"] };

        Assert.Null(FolderChatbotNames.BotOf(file));
    }

    [Fact]
    public void The_relative_path_is_what_follows_the_bot_and_a_slash()
    {
        Assert.Equal("2026/q3.pdf", FolderChatbotNames.RelativeOf("sales", "sales/2026/q3.pdf"));
        Assert.Null(FolderChatbotNames.RelativeOf("sales", "salesx/q3.pdf"));
        Assert.Null(FolderChatbotNames.RelativeOf("sales", "other/q3.pdf"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(96, true)]    // vault-ai's 100-character tag limit minus "bot:"
    [InlineData(97, false)]
    public void A_bot_name_must_fit_a_tag(int length, bool usable)
    {
        Assert.Equal(usable, FolderChatbotNames.IsUsableBotName(new string('a', length)));
    }
}

public class FolderChatbotStorageMetaTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void The_line_is_the_prefix_and_one_line_of_json_with_the_stored_keys()
    {
        var meta = new FolderChatbotStorageMeta(Owner, new Dictionary<string, FolderChatbotBotMeta>
        {
            ["sales"] = new(null, 1, 2),
            ["hr"] = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 0, 0),
        });

        Assert.Equal(
            "chatbot-meta:{\"owner\":\"11111111-2222-3333-4444-555555555555\",\"bots\":{\"hr\":{\"p\":\"2026-11-01T00:00:00+00:00\",\"s\":0,\"f\":0},\"sales\":{\"s\":1,\"f\":2}}}",
            meta.Format());
    }

    [Fact]
    public void A_bot_at_the_default_is_not_written_and_no_bots_at_all_leaves_only_the_owner()
    {
        var meta = new FolderChatbotStorageMeta(Owner, new Dictionary<string, FolderChatbotBotMeta> { ["sales"] = new(null, 0, 0) });

        Assert.Equal("chatbot-meta:{\"owner\":\"11111111-2222-3333-4444-555555555555\"}", meta.Format());
    }

    [Fact]
    public void A_line_it_wrote_reads_back_the_same()
    {
        var meta = new FolderChatbotStorageMeta(Owner, new Dictionary<string, FolderChatbotBotMeta>
        {
            ["sales"] = new(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero), 3, 4),
        });

        Assert.True(FolderChatbotStorageMeta.TryParse(meta.Format(), out var read));
        Assert.Equal(Owner, read!.Owner);
        Assert.Equal(meta.Bots["sales"], read.Bots["sales"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some other description")]
    [InlineData("chatbot-meta:{not json")]
    public void A_description_that_is_not_the_line_does_not_parse(string? description)
    {
        Assert.False(FolderChatbotStorageMeta.TryParse(description, out var meta));
        Assert.Null(meta);
    }

    [Fact]
    public void Past_the_length_vault_ai_allows_pending_deletion_is_kept_first_then_failures_then_skips()
    {
        var bots = new Dictionary<string, FolderChatbotBotMeta>
        {
            ["pending"] = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 0, 0),
            ["failing"] = new(null, 0, 9),
            ["skipping"] = new(null, 9, 0),
        };
        for (var i = 0; i < 200; i++) bots[$"bot-with-a-long-enough-name-to-fill-the-line-{i:D3}"] = new(null, 1, 0);

        var line = new FolderChatbotStorageMeta(Owner, bots).Format(out var dropped);

        Assert.True(line.Length <= FolderChatbotStorageMeta.MaxLength);
        Assert.True(dropped > 0);
        Assert.Contains("\"pending\"", line);
        Assert.Contains("\"failing\"", line);
        Assert.Contains("\"skipping\"", line);
    }
}

public class FolderChatbotSupportedFilesTests
{
    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("Report.PDF", true)]
    [InlineData("sheet.xlsx", true)]
    [InlineData("notes/readme.md", true)]
    [InlineData("scan.TIFF", true)]
    [InlineData("program.exe", false)]
    [InlineData("archive.zip", false)]
    [InlineData("noextension", false)]
    [InlineData("trailingdot.", false)]
    [InlineData(".pdf", true)]   // a dotfile named like an extension has one
    public void Only_the_extensions_the_server_can_parse_or_read_as_text_are_uploaded(string name, bool supported)
    {
        Assert.Equal(supported, FolderChatbotSupportedFiles.IsSupported(name));
    }
}

public class FolderChatbotModelPickerTests
{
    private static readonly Guid Lang1 = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Lang2 = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid SmallLang = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid Emb1 = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Emb2 = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid Rerank1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");

    private static ModelRef Language(Guid id, int context = 128_000) => new(id, "language", "lang-" + id.ToString("N")[^2..], "", context);
    private static ModelRef Embedding(Guid id) => new(id, "embedding", "emb-" + id.ToString("N")[^2..], "", null);

    private static readonly VaultAIFolderChatbotModelOptions Nothing = new();

    [Fact]
    public void One_candidate_of_each_kind_is_picked_and_the_answer_is_the_index_model()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, FolderChatbotModels.None, [Language(Lang1), Embedding(Emb1)]);

        Assert.Equal(new FolderChatbotModels(Lang1, Lang1, Emb1, null), pick.Models);
        Assert.True(pick.Models.IsComplete);
        Assert.Empty(pick.Warnings);
    }

    [Fact]
    public void A_language_model_below_the_minimum_context_is_not_a_candidate()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, FolderChatbotModels.None,
            [Language(Lang1), Language(SmallLang, context: 8_000), Embedding(Emb1)]);

        Assert.Equal(Lang1, pick.Models.Index);   // the small one does not make it "several"
    }

    [Fact]
    public void The_minimum_context_can_be_lowered_by_the_options()
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { MinLanguageContextLength = 4_000 },
            FolderChatbotModels.None, [Language(SmallLang, context: 8_000), Embedding(Emb1)]);

        Assert.Equal(SmallLang, pick.Models.Index);
    }

    [Fact]
    public void Several_candidates_and_nothing_in_use_picks_none_and_says_which_to_name()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, FolderChatbotModels.None,
            [Language(Lang1), Language(Lang2), Embedding(Emb1)]);

        Assert.Null(pick.Models.Index);
        Assert.False(pick.Models.IsComplete);
        var warning = Assert.Single(pick.Warnings);
        Assert.Contains("several index model candidates", warning);
        Assert.Contains("Models.Index", warning);
        Assert.Contains(Lang1.ToString(), warning);
        Assert.Contains(Lang2.ToString(), warning);
    }

    [Fact]
    public void A_model_already_in_use_is_kept_while_it_is_still_a_candidate()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, new FolderChatbotModels(Lang2, null, null, null),
            [Language(Lang1), Language(Lang2), Embedding(Emb1)]);

        Assert.Equal(Lang2, pick.Models.Index);
        Assert.Empty(pick.Warnings);
    }

    [Fact]
    public void An_option_wins_over_what_is_in_use()
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { Index = Lang1.ToString() },
            new FolderChatbotModels(Lang2, null, null, null), [Language(Lang1), Language(Lang2), Embedding(Emb1)]);

        Assert.Equal(Lang1, pick.Models.Index);
    }

    /// <summary>Changing the embedding model makes vault-ai embed the whole storage again, so it is never changed by itself.</summary>
    [Fact]
    public void The_embedding_model_in_use_is_not_replaced_when_it_stops_being_a_candidate()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, new FolderChatbotModels(null, null, Emb1, null),
            [Language(Lang1), Embedding(Emb2)]);   // the only candidate is not the one in use

        Assert.Null(pick.Models.Embedding);
        Assert.False(pick.EmbeddingExplicit);
        var warning = Assert.Single(pick.Warnings);
        Assert.Contains("not replaced by itself", warning);
    }

    [Fact]
    public void Naming_the_embedding_model_in_the_options_is_the_way_to_change_it()
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { Embedding = Emb2.ToString() },
            new FolderChatbotModels(null, null, Emb1, null), [Language(Lang1), Embedding(Emb1), Embedding(Emb2)]);

        Assert.Equal(Emb2, pick.Models.Embedding);
        Assert.True(pick.EmbeddingExplicit);
    }

    [Fact]
    public void An_answer_model_of_its_own_is_used_when_set()
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { Answer = Lang2.ToString() },
            FolderChatbotModels.None, [Language(Lang1), Language(Lang2), Embedding(Emb1)]);

        Assert.Equal(Lang2, pick.Models.Answer);
        Assert.Null(pick.Models.Index);     // several candidates and the options settle only the answer
    }

    [Theory]
    [InlineData("not-a-guid", "is not a model row id")]
    [InlineData("99999999-9999-9999-9999-999999999999", "is not in the workspace's model list")]
    public void A_wrong_option_is_never_filled_in_by_guessing(string value, string expectedWarning)
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { Index = value },
            FolderChatbotModels.None, [Language(Lang1), Embedding(Emb1)]);

        Assert.Null(pick.Models.Index);       // the single fitting candidate is not substituted for a wrong option
        Assert.Contains(pick.Warnings, w => w.Contains(expectedWarning));
    }

    [Fact]
    public void An_option_naming_a_model_of_the_wrong_kind_is_refused()
    {
        var pick = FolderChatbotModelPicker.Pick(new VaultAIFolderChatbotModelOptions { Embedding = Lang1.ToString() },
            FolderChatbotModels.None, [Language(Lang1), Embedding(Emb1)]);

        Assert.Null(pick.Models.Embedding);
        Assert.Contains(pick.Warnings, w => w.Contains("is of kind language (it must be embedding)"));
    }

    [Fact]
    public void A_missing_required_kind_is_said_and_rerank_alone_is_optional()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, FolderChatbotModels.None, [Language(Lang1)]);

        Assert.Null(pick.Models.Embedding);
        Assert.Null(pick.Models.Rerank);
        var warning = Assert.Single(pick.Warnings);   // embedding is required and said; rerank is not
        Assert.Contains("no embedding model candidate", warning);
    }

    [Fact]
    public void A_single_rerank_candidate_is_picked()
    {
        var pick = FolderChatbotModelPicker.Pick(Nothing, FolderChatbotModels.None,
            [Language(Lang1), Embedding(Emb1), new ModelRef(Rerank1, "rerank", "rr", "", null)]);

        Assert.Equal(Rerank1, pick.Models.Rerank);
    }
}
