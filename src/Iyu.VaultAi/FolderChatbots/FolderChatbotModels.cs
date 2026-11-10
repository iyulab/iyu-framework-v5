namespace Iyu.VaultAi.FolderChatbots;

/// <summary>The vault-ai model rows every folder chatbot shares. Index, answer and embedding are required, rerank is optional.</summary>
internal sealed record FolderChatbotModels(Guid? Index, Guid? Answer, Guid? Embedding, Guid? Rerank)
{
    public static FolderChatbotModels None { get; } = new(null, null, null, null);

    /// <summary>Whether index, answer and embedding are all settled — if not, nothing is synced (a file kept in a storage without models cannot be indexed and sets that way).</summary>
    public bool IsComplete => Index is not null && Answer is not null && Embedding is not null;
}

/// <summary>
/// The models to use, as model row ids (GUID strings). All are optional and an empty one means "not decided". Write one only when
/// the workspace has several candidates of the same kind and the engine cannot choose, or to change a model already in use.
/// </summary>
/// <remarks>The ids are rows of the vault-ai workspace's models, not model names: a name is not an identifier there.</remarks>
internal sealed class VaultAiFolderChatbotModelOptions
{
    /// <summary>The language model that indexes files (the storage's language model).</summary>
    public string Index { get; set; } = "";

    /// <summary>The language model that answers (the agent's model). When empty, the index model answers too.</summary>
    public string Answer { get; set; } = "";

    /// <summary>The embedding model. Changing the one in use makes vault-ai embed the whole storage again, so only this setting can change it.</summary>
    public string Embedding { get; set; } = "";

    /// <summary>The rerank model (optional).</summary>
    public string Rerank { get; set; } = "";

    /// <summary>
    /// The shortest context length a language model needs to be a candidate for indexing and answering — what vault-ai's indexer
    /// requires of one. Raise it only if the server's requirement is raised.
    /// </summary>
    public int MinLanguageContextLength { get; set; } = FolderChatbotModelPicker.DefaultMinLanguageContextLength;
}

/// <summary>One model row of the workspace. <see cref="Type"/> is vault-ai's snake_case kind (language, embedding, rerank, audio).</summary>
internal sealed record ModelRef(Guid Id, string Type, string ModelId, string DisplayName, int? ContextLength)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == ModelId
        ? $"{ModelId} ({Id})"
        : $"{DisplayName} [{ModelId}] ({Id})";
}

/// <summary>
/// What one picking came to. <see cref="Warnings"/> are logged once per check. <see cref="EmbeddingExplicit"/> says whether the
/// embedding model came from the options — only then may the embedding model of an existing storage be changed.
/// </summary>
internal sealed record ModelPick(
    FolderChatbotModels Models, IReadOnlyList<string> Warnings, bool EmbeddingExplicit, IReadOnlyList<ModelRef> Available)
{
    /// <summary>A model row id as a label (the id itself when it is not in the list).</summary>
    public string LabelOf(Guid? id)
        => id is not { } v ? "" : Available.FirstOrDefault(m => m.Id == v)?.Label ?? v.ToString();
}

/// <summary>
/// Picks, from the workspace's model list, the models a folder chatbot uses, one kind at a time. No side effects.
/// </summary>
/// <remarks>
/// <para>Order of precedence: what the options name &gt; what is already in use (the manifest, the existing storage) &gt; the single
/// candidate that fits &gt; nothing.</para>
/// <list type="bullet">
/// <item>Index and answer: <c>language</c> with a context length of at least the option's minimum. The answer is the index model unless set.</item>
/// <item>Embedding: <c>embedding</c>. Rerank (optional): <c>rerank</c>.</item>
/// </list>
/// <para>The embedding model in use is never replaced by itself when the candidates change — vault-ai would embed the whole storage
/// again. The one way to change it is to name it in the options. With several candidates and none in use nothing is picked and a
/// warning says so; a wrong id or kind in the options is never guessed around.</para>
/// <para>Whether a model takes image input is not in its row, so it cannot be told apart here.</para>
/// </remarks>
internal static class FolderChatbotModelPicker
{
    /// <summary>The context length vault-ai's indexer requires of a language model.</summary>
    public const int DefaultMinLanguageContextLength = 65_536;

    private const string OptionPrefix = "the folder chatbot option Models.";

    /// <param name="configured">What the options name.</param>
    /// <param name="inUse">The models already in use (recorded in the manifest, else read from the existing storage).</param>
    /// <param name="models">The workspace's model list.</param>
    public static ModelPick Pick(VaultAiFolderChatbotModelOptions configured, FolderChatbotModels inUse, IReadOnlyList<ModelRef> models)
    {
        var minContext = configured.MinLanguageContextLength;
        var warnings = new List<string>();
        var language = models.Where(m => IsLanguageCandidate(m, minContext)).ToList();
        var embedding = models.Where(m => m.Type == "embedding").ToList();
        var rerank = models.Where(m => m.Type == "rerank").ToList();

        var index = Choose("index", nameof(VaultAiFolderChatbotModelOptions.Index), configured.Index, "language", inUse.Index,
            language, models, minContext, required: true, protectInUse: false, warnings);

        // The answer is the index model unless set — one language model does both jobs.
        Guid? answer;
        if (!string.IsNullOrWhiteSpace(configured.Answer))
            answer = Choose("answer", nameof(VaultAiFolderChatbotModelOptions.Answer), configured.Answer, "language", null,
                language, models, minContext, required: true, protectInUse: false, warnings);
        else if (inUse.Answer is { } a && language.Any(m => m.Id == a))
            answer = a;
        else
            answer = index;

        var embeddingExplicit = !string.IsNullOrWhiteSpace(configured.Embedding);
        var emb = Choose("embedding", nameof(VaultAiFolderChatbotModelOptions.Embedding), configured.Embedding, "embedding",
            inUse.Embedding, embedding, models, minContext, required: true, protectInUse: true, warnings);

        var rr = Choose("rerank", nameof(VaultAiFolderChatbotModelOptions.Rerank), configured.Rerank, "rerank", inUse.Rerank,
            rerank, models, minContext, required: false, protectInUse: false, warnings);

        return new ModelPick(new FolderChatbotModels(index, answer, emb, rr), warnings, embeddingExplicit, models);
    }

    public static bool IsLanguageCandidate(ModelRef m, int minContextLength)
        => m.Type == "language" && m.ContextLength >= minContextLength;

    private static Guid? Choose(string label, string key, string configured, string type, Guid? inUse,
        IReadOnlyList<ModelRef> candidates, IReadOnlyList<ModelRef> all, int minContext, bool required, bool protectInUse,
        List<string> warnings)
    {
        var setting = OptionPrefix + key;

        // 1. What the options name — a wrong one is not filled in by guessing (left undecided, with a warning).
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Guid.TryParse(configured.Trim(), out var id))
            {
                warnings.Add($"{setting} is '{configured}', which is not a model row id (a GUID).");
                return null;
            }
            var row = all.FirstOrDefault(m => m.Id == id);
            if (row is null)
            {
                warnings.Add($"The model {id} named by {setting} is not in the workspace's model list.");
                return null;
            }
            if (row.Type != type)
            {
                warnings.Add($"The model {row.Label} named by {setting} is of kind {row.Type} (it must be {type}).");
                return null;
            }
            if (type == "language" && !(row.ContextLength >= minContext))
                warnings.Add($"The model {row.Label} named by {setting} has a context length below {minContext:N0}, so vault-ai may not accept it as an indexing model.");
            return id;
        }

        // 2. What is already in use.
        if (inUse is { } used)
        {
            if (candidates.Any(m => m.Id == used)) return used;
            if (protectInUse)
            {
                warnings.Add($"The {label} model {used} in use is not among the workspace's candidates. Changing it would redo the whole {label} step, " +
                             $"so it is not replaced by itself: name the model row id to use in {setting}. Candidates: {Labels(candidates)}");
                return null;
            }
        }

        // 3. The only candidate.
        if (candidates.Count == 1) return candidates[0].Id;
        if (candidates.Count == 0)
        {
            if (required)
                warnings.Add($"The workspace has no {label} model candidate — register one {Condition(type, minContext)} in vault-ai.");
            return null;
        }
        warnings.Add($"The workspace has several {label} model candidates, so none is picked: name the model row id to use in {setting}. Candidates: {Labels(candidates)}");
        return null;
    }

    private static string Condition(string type, int minContext) => type == "language"
        ? $"of kind language with a context length of at least {minContext:N0}"
        : $"of kind {type}";

    private static string Labels(IEnumerable<ModelRef> models)
    {
        var list = models.Select(m => m.Label).ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }
}
