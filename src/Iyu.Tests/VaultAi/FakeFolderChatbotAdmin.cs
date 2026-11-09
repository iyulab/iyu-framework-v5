using Iyu.VaultAi.FolderChatbots;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// An in-memory vault-ai for the engine's tests: it keeps what the management port is given the way the server does (an agent update
/// is a full replacement, a list is filtered by name prefix) and records the calls that change something, so a test can say what the
/// engine did and did not do.
/// </summary>
internal sealed class FakeFolderChatbotAdmin : IFolderChatbotAdmin
{
    public sealed record StorageRow(Guid Id, string Name, string Description, Guid? Embedding, Guid? Language, Guid? Rerank);
    public sealed record AgentRow(Guid Id, string Name, string Description, Guid? Model, string Instructions,
        IReadOnlyList<Guid> Connectors, IReadOnlyList<Guid> Storages, IReadOnlyList<AgentScope> Scopes);
    public sealed record ChannelRow(Guid Id, string Name, Guid AgentId);
    public sealed record ConnectorRow(Guid Id, string Name, string Description, string Url, IReadOnlyDictionary<string, string> Headers);

    public Dictionary<Guid, StorageRow> Storages { get; } = [];
    public Dictionary<Guid, AgentRow> Agents { get; } = [];
    public Dictionary<Guid, ChannelRow> Channels { get; } = [];
    public Dictionary<Guid, ConnectorRow> Connectors { get; } = [];
    public Dictionary<Guid, List<RemoteFile>> Files { get; } = [];

    /// <summary>Calls that change something, in order: <c>create-storage</c>, <c>update-agent</c>, <c>delete-connector</c> …</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Ids a list leaves out although a lookup by id finds them — the case "the list cannot be trusted".</summary>
    public HashSet<Guid> HiddenFromLists { get; } = [];

    public int Count(string call) => Calls.Count(c => c == call);

    // ---- seeding (test arrangement; not recorded as calls)

    public Guid AddStorage(string name, string description = "", Guid? embedding = null, Guid? language = null)
    {
        var id = Guid.NewGuid();
        Storages[id] = new StorageRow(id, name, description, embedding, language, null);
        Files[id] = [];
        return id;
    }

    public Guid AddAgent(string name)
    {
        var id = Guid.NewGuid();
        Agents[id] = new AgentRow(id, name, "", null, "", [], [], []);
        return id;
    }

    public Guid AddConnector(string name, string description, string url = "https://example.test/mcp")
    {
        var id = Guid.NewGuid();
        Connectors[id] = new ConnectorRow(id, name, description, url, new Dictionary<string, string>());
        return id;
    }

    private static void RequirePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("A prefix is required.", nameof(prefix));
    }

    private static HttpRequestException NotFound() => new("not found", null, System.Net.HttpStatusCode.NotFound);

    private IEnumerable<T> Visible<T>(IEnumerable<T> rows, Func<T, Guid> id, Func<T, string> name, string? prefix)
        => rows.Where(r => !HiddenFromLists.Contains(id(r)) && (prefix is null || name(r).StartsWith(prefix, StringComparison.Ordinal)));

    // ---- listing

    public Task<IReadOnlyList<StorageRef>> ListStoragesAsync(string namePrefix, CancellationToken ct = default)
    {
        RequirePrefix(namePrefix);
        return Task.FromResult<IReadOnlyList<StorageRef>>(Visible(Storages.Values, r => r.Id, r => r.Name, namePrefix)
            .Select(r => new StorageRef(r.Id, r.Name, r.Description, r.Embedding, r.Language, r.Rerank)).ToList());
    }

    public Task<IReadOnlyList<NamedRef>> ListAgentsAsync(string namePrefix, CancellationToken ct = default)
    {
        RequirePrefix(namePrefix);
        return Task.FromResult<IReadOnlyList<NamedRef>>(Visible(Agents.Values, r => r.Id, r => r.Name, namePrefix)
            .Select(r => new NamedRef(r.Id, r.Name, r.Description)).ToList());
    }

    public Task<IReadOnlyList<NamedRef>> ListChannelsAsync(string namePrefix, CancellationToken ct = default)
    {
        RequirePrefix(namePrefix);
        return Task.FromResult<IReadOnlyList<NamedRef>>(Visible(Channels.Values, r => r.Id, r => r.Name, namePrefix)
            .Select(r => new NamedRef(r.Id, r.Name, "")).ToList());
    }

    public Task<IReadOnlyList<NamedRef>> ListConnectorsAsync(string? namePrefix = null, CancellationToken ct = default)
    {
        if (namePrefix is not null) RequirePrefix(namePrefix);
        return Task.FromResult<IReadOnlyList<NamedRef>>(Visible(Connectors.Values, r => r.Id, r => r.Name, namePrefix)
            .Select(r => new NamedRef(r.Id, r.Name, r.Description)).ToList());
    }

    public Task<IReadOnlyList<ModelRef>> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelRef>>([]);

    // ---- connectors

    public Task<Guid> CreateMcpConnectorAsync(string name, string description, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        Calls.Add("create-connector");
        var id = Guid.NewGuid();
        Connectors[id] = new ConnectorRow(id, name, description, url, headers);
        return Task.FromResult(id);
    }

    public Task UpdateMcpConnectorAsync(Guid id, string name, string description, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        Calls.Add("update-connector");
        if (!Connectors.ContainsKey(id)) throw NotFound();
        Connectors[id] = new ConnectorRow(id, name, description, url, headers);
        return Task.CompletedTask;
    }

    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("delete-connector");
        if (!Connectors.Remove(id)) throw NotFound();
        return Task.CompletedTask;
    }

    // ---- storage

    public Task<Guid> CreateStorageAsync(string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default)
    {
        Calls.Add("create-storage");
        var id = Guid.NewGuid();
        Storages[id] = new StorageRow(id, name, description, embedding, language, rerank);
        Files[id] = [];
        return Task.FromResult(id);
    }

    public Task UpdateStorageAsync(Guid id, string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default)
    {
        Calls.Add("update-storage");
        if (!Storages.ContainsKey(id)) throw NotFound();
        Storages[id] = new StorageRow(id, name, description, embedding, language, rerank);
        return Task.CompletedTask;
    }

    public Task DeleteStorageAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("delete-storage");
        if (!Storages.Remove(id)) throw NotFound();
        return Task.CompletedTask;
    }

    public Task<bool> StorageExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Storages.ContainsKey(id));
    public Task<bool> AgentExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Agents.ContainsKey(id));
    public Task<bool> ChannelExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Channels.ContainsKey(id));

    public Task<StorageSummary> GetStorageSummaryAsync(Guid storageId, IReadOnlyList<string>? tags = null, CancellationToken ct = default)
        => Task.FromResult(new StorageSummary(0, new Dictionary<string, int>()));

    // ---- agent

    public Task<Guid> CreateAgentAsync(string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default)
    {
        Calls.Add("create-agent");
        var id = Guid.NewGuid();
        Agents[id] = new AgentRow(id, name, description, modelId, instructions, connectors, storages, scopes);
        return Task.FromResult(id);
    }

    /// <summary>When set, updating the agent fails with it.</summary>
    public Exception? UpdateAgentFailure { get; set; }

    public Task UpdateAgentAsync(Guid id, string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default)
    {
        Calls.Add("update-agent");
        if (UpdateAgentFailure is { } failure) throw failure;
        if (!Agents.ContainsKey(id)) throw NotFound();
        Agents[id] = new AgentRow(id, name, description, modelId, instructions, connectors, storages, scopes);
        return Task.CompletedTask;
    }

    public Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("delete-agent");
        if (!Agents.Remove(id)) throw NotFound();
        return Task.CompletedTask;
    }

    // ---- channel

    public Task<Guid> CreateWebChannelAsync(string name, Guid agentId, CancellationToken ct = default)
    {
        Calls.Add("create-channel");
        var id = Guid.NewGuid();
        Channels[id] = new ChannelRow(id, name, agentId);
        return Task.FromResult(id);
    }

    public Task DeleteChannelAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("delete-channel");
        if (!Channels.Remove(id)) throw NotFound();
        return Task.CompletedTask;
    }

    public Task<ChannelToken> IssueChannelTokenAsync(Guid channelId, CancellationToken ct = default)
        => throw new NotSupportedException();

    // ---- files

    /// <summary>Makes an upload of that remote file name fail with the exception it returns (null: succeeds).</summary>
    public Func<string, Exception?>? UploadFailure { get; set; }

    /// <summary>Makes the delete of that remote file fail with the exception it returns (null: succeeds).</summary>
    public Func<RemoteFile, Exception?>? DeleteFileFailure { get; set; }

    /// <summary>When set, listing the files of a storage fails with it.</summary>
    public Exception? ListFilesFailure { get; set; }

    public Task<Guid> UploadFileAsync(Guid storageId, string fileName, IReadOnlyList<string> tags, Stream content, CancellationToken ct = default)
    {
        Calls.Add("upload:" + fileName);
        if (UploadFailure?.Invoke(fileName) is { } failure) throw failure;
        if (!Files.TryGetValue(storageId, out var files)) throw NotFound();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var id = Guid.NewGuid();
        files.Add(new RemoteFile(id, fileName, buffer.Length, "indexed") { Tags = tags });
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<RemoteFile>> ListFilesAsync(Guid storageId, CancellationToken ct = default)
    {
        if (ListFilesFailure is { } failure) throw failure;
        return Task.FromResult<IReadOnlyList<RemoteFile>>(Files.TryGetValue(storageId, out var files) ? files.ToList() : []);
    }

    public Task DeleteFileAsync(Guid storageId, Guid fileId, CancellationToken ct = default)
    {
        if (!Files.TryGetValue(storageId, out var files)) throw NotFound();
        var file = files.FirstOrDefault(f => f.Id == fileId) ?? throw NotFound();
        Calls.Add("delete-file:" + file.FileName);
        if (DeleteFileFailure?.Invoke(file) is { } failure) throw failure;
        files.Remove(file);
        return Task.CompletedTask;
    }

    public Task RetryFilePipelineAsync(Guid storageId, Guid fileId, CancellationToken ct = default)
    {
        Calls.Add("retry-file");
        return Task.CompletedTask;
    }
}
