namespace Iyu.VaultAI.FolderChatbots;

/// <summary>A vault-ai resource that has a name and a description (a storage, an agent, a channel, a connector), summarised.</summary>
internal record NamedRef(Guid Id, string Name, string Description);

/// <summary>One row of the storage list: its name and description, and the model rows the storage uses (null when none).</summary>
internal sealed record StorageRef(Guid Id, string Name, string Description, Guid? EmbeddingModel, Guid? LanguageModel,
    Guid? RerankModel) : NamedRef(Id, Name, Description);

/// <summary>
/// One scope of an agent — one agent is divided among several chatbots by scope. A session opened on a scope searches only files that
/// carry one of its <see cref="Tags"/>, appends its <see cref="Instructions"/> (if any) to the agent's, and uses only the connectors in
/// <see cref="ConnectorIds"/> (a subset of the connectors attached to the agent). <see cref="Name"/> is unique within the agent (case-sensitive).
/// </summary>
internal sealed record AgentScope(string Name, IReadOnlyList<string> Tags, string? Instructions, IReadOnlyList<Guid> ConnectorIds);

/// <summary>How far a storage's indexing has got. The keys of <see cref="ByStatus"/> are snake_case status names (pending, indexing, indexed, …).</summary>
internal sealed record StorageSummary(int TotalCount, IReadOnlyDictionary<string, int> ByStatus);

/// <summary>A web channel's short-lived access token and when it expires.</summary>
internal sealed record ChannelToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The server will not take the file: an unsupported kind (HTTP 415) or over the server's upload size limit (HTTP 413). The sync counts the
/// file as skipped and does not send it again until its content changes. Derived from <see cref="HttpRequestException"/> so a general
/// failure path catches it too, while a caller can still catch it on its own.
/// </summary>
internal sealed class VaultAIUnsupportedFileException : HttpRequestException
{
    public VaultAIUnsupportedFileException(string message,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.UnsupportedMediaType)
        : base(message, null, status) { }
}

/// <summary>The resource is being indexed or cancelled and cannot be deleted now (HTTP 409). The caller tries again at the next check.</summary>
internal sealed class VaultAIConflictException : HttpRequestException
{
    public VaultAIConflictException(string message)
        : base(message, null, System.Net.HttpStatusCode.Conflict) { }
}

/// <summary>
/// The vault-ai management operations a folder chatbot needs, and no others: storage, files, agent, web channel, MCP connectors and the
/// workspace's models. A non-success response is thrown as <see cref="HttpRequestException"/> with its status code set.
/// </summary>
/// <remarks>
/// What it leaves out on purpose: a management console's operations (workspace secrets, simulator sessions, search) — a folder chatbot
/// does not use them, and a port that offered them would hand every caller of the engine a wider key than it needs.
/// </remarks>
internal interface IFolderChatbotAdmin
{
    /// <summary>Only those whose name starts with <paramref name="namePrefix"/> (case-sensitive). A null or blank prefix throws <see cref="ArgumentException"/> (storages, agents and channels alike).</summary>
    Task<IReadOnlyList<StorageRef>> ListStoragesAsync(string namePrefix, CancellationToken ct = default);
    Task<IReadOnlyList<NamedRef>> ListAgentsAsync(string namePrefix, CancellationToken ct = default);
    Task<IReadOnlyList<NamedRef>> ListChannelsAsync(string namePrefix, CancellationToken ct = default);
    /// <summary>The connectors. With a null <paramref name="namePrefix"/> the whole workspace; with blank text, <see cref="ArgumentException"/>.</summary>
    Task<IReadOnlyList<NamedRef>> ListConnectorsAsync(string? namePrefix = null, CancellationToken ct = default);

    /// <summary>An MCP connector (address and headers). A <c>{{KEY}}</c> in a header value is replaced by vault-ai with a workspace secret.</summary>
    Task<Guid> CreateMcpConnectorAsync(string name, string description, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
    Task UpdateMcpConnectorAsync(Guid id, string name, string description, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
    Task DeleteConnectorAsync(Guid id, CancellationToken ct = default);

    /// <summary>The workspace's models (<c>GET api/models</c>), from which the engine picks the ones to use.</summary>
    Task<IReadOnlyList<ModelRef>> ListModelsAsync(CancellationToken ct = default);

    Task<Guid> CreateStorageAsync(string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default);
    /// <summary>A full replacement: a null model id empties it on the server.</summary>
    Task UpdateStorageAsync(Guid id, string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default);
    /// <summary>On 409, <see cref="VaultAIConflictException"/>.</summary>
    Task DeleteStorageAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Whether the object exists, asked directly by id (404 = no). It tells an object missing from a list that has really gone from a list
    /// that failed. Any other failure is thrown.
    /// </summary>
    Task<bool> StorageExistsAsync(Guid id, CancellationToken ct = default);
    Task<bool> AgentExistsAsync(Guid id, CancellationToken ct = default);
    Task<bool> ChannelExistsAsync(Guid id, CancellationToken ct = default);
    /// <summary>How indexing stands. With <paramref name="tags"/>, only files carrying at least one of them are counted.</summary>
    Task<StorageSummary> GetStorageSummaryAsync(Guid storageId, IReadOnlyList<string>? tags = null, CancellationToken ct = default);

    /// <summary><paramref name="scopes"/> is the agent's whole set of scopes (empty = none).</summary>
    Task<Guid> CreateAgentAsync(string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default);
    /// <summary>
    /// A full replacement — <paramref name="scopes"/>, <paramref name="connectors"/> and <paramref name="storages"/> are always the whole list.
    /// An update is "read what is there, merge, write it all back", never "write only what I know of": sending a short list removes the rest.
    /// </summary>
    Task UpdateAgentAsync(Guid id, string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default);
    Task DeleteAgentAsync(Guid id, CancellationToken ct = default);

    Task<Guid> CreateWebChannelAsync(string name, Guid agentId, CancellationToken ct = default);
    Task DeleteChannelAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// A web channel's short-lived access token (<c>POST api/channels/{id}/token</c>), used by the browser embed. The workspace token must
    /// never reach a browser, so the application server hands the user only this one. A missing channel is thrown as 404.
    /// </summary>
    Task<ChannelToken> IssueChannelTokenAsync(Guid channelId, CancellationToken ct = default);

    /// <summary>Uploads a file with its tags and returns the new file's id. The <paramref name="content"/> stream is closed once sent (do not reuse it). On 415 or 413, <see cref="VaultAIUnsupportedFileException"/>.</summary>
    Task<Guid> UploadFileAsync(Guid storageId, string fileName, IReadOnlyList<string> tags, Stream content, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFile>> ListFilesAsync(Guid storageId, CancellationToken ct = default);
    /// <summary>On 409, <see cref="VaultAIConflictException"/>.</summary>
    Task DeleteFileAsync(Guid storageId, Guid fileId, CancellationToken ct = default);
    /// <summary>Runs the file's indexing pipeline again from the start (a file that could not be indexed or failed). On 409 (being processed), <see cref="VaultAIConflictException"/>.</summary>
    Task RetryFilePipelineAsync(Guid storageId, Guid fileId, CancellationToken ct = default);
}
