using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// <see cref="IFolderChatbotAdmin"/> over vault-ai's REST API. Every operation runs under one time limit
/// (<see cref="VaultAIDeadline"/>, the module's per-call limit): the limit expiring is a <see cref="VaultAITimeoutException"/>, the
/// caller's own token firing stays the caller's cancellation.
/// </summary>
internal sealed class FolderChatbotAdminClient : IFolderChatbotAdmin
{
    private const int PageSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>For production: the same bearer token and certificate handling as <see cref="VaultAIClient"/>.</summary>
    public static FolderChatbotAdminClient Create(VaultAISettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // The limit of a call is the module's own; the HttpClient's built-in one would cut in first whenever a longer limit is asked for.
        var http = new HttpClient(VaultAIClient.CreateHandler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        http.BaseAddress = new Uri(settings.Url.TrimEnd('/') + "/");
        return new FolderChatbotAdminClient(http, settings.RequestTimeout);
    }

    /// <summary>The caller passes an <see cref="HttpClient"/> that already has its base address and credentials (tests do).</summary>
    public FolderChatbotAdminClient(HttpClient http, TimeSpan timeout)
    {
        _http = http;
        _timeout = timeout;
    }

    private Task<T> Run<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
        => VaultAIDeadline.RunAsync(_timeout, ct, call);

    private Task Run(CancellationToken ct, Func<CancellationToken, Task> call)
        => VaultAIDeadline.RunAsync<object?>(_timeout, ct, async t => { await call(t).ConfigureAwait(false); return null; });

    // ---- lists ----

    public Task<IReadOnlyList<StorageRef>> ListStoragesAsync(string namePrefix, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            RequirePrefix(namePrefix);
            var items = await ListPagedAsync<StorageDto>("api/storages", "name^" + EscapeFilterValue(namePrefix), "createdAt", t);
            // The server's ^ ignores case — only exact matches are kept (as ListNamedAsync does).
            return (IReadOnlyList<StorageRef>)items
                .Where(i => (i.Name ?? "").StartsWith(namePrefix, StringComparison.Ordinal))
                .Select(i => new StorageRef(i.Id, i.Name ?? "", i.Description ?? "", i.EmbeddingModelId, i.LanguageModelId, i.RerankModelId))
                .ToList();
        });

    public Task<IReadOnlyList<NamedRef>> ListAgentsAsync(string namePrefix, CancellationToken ct = default)
        => Run(ct, t => ListNamedAsync("api/agents", RequirePrefix(namePrefix), t));

    public Task<IReadOnlyList<NamedRef>> ListChannelsAsync(string namePrefix, CancellationToken ct = default)
        => Run(ct, t => ListNamedAsync("api/channels", RequirePrefix(namePrefix), t));

    public Task<IReadOnlyList<NamedRef>> ListConnectorsAsync(string? namePrefix = null, CancellationToken ct = default)
        => Run(ct, t => ListNamedAsync("api/connectors", namePrefix is null ? null : RequirePrefix(namePrefix), t));

    private static string RequirePrefix(string namePrefix)
        => string.IsNullOrWhiteSpace(namePrefix)
            ? throw new ArgumentException("The name prefix cannot be empty.", nameof(namePrefix))
            : namePrefix;

    private async Task<IReadOnlyList<NamedRef>> ListNamedAsync(string path, string? namePrefix, CancellationToken ct)
    {
        // Called without a prefix this lists the whole workspace — a prefixed list must always stay inside its prefix.
        var filter = namePrefix is null ? null : "name^" + EscapeFilterValue(namePrefix);
        var items = await ListPagedAsync<NamedDto>(path, filter, "createdAt", ct);
        // The server's ^ ignores case (the database collation) — only exact matches are kept.
        return items
            .Where(i => namePrefix is null || (i.Name ?? "").StartsWith(namePrefix, StringComparison.Ordinal))
            .Select(i => new NamedRef(i.Id, i.Name ?? "", i.Description ?? "")).ToList();
    }

    public Task<IReadOnlyList<ModelRef>> ListModelsAsync(CancellationToken ct = default)
        => Run(ct, async t =>
        {
            var items = await ListPagedAsync<ModelDto>("api/models", null, "createdAt", t);
            return (IReadOnlyList<ModelRef>)items.Select(i => new ModelRef(i.Id, (i.Type ?? "").ToLowerInvariant(), i.ModelId ?? "",
                i.DisplayName ?? "", ContextLengthOf(i.Metadata))).ToList();
        });

    // contextLength in a model's metadata — a number or a numeric string. Missing or unreadable: null (not a language model candidate).
    private static int? ContextLengthOf(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } m) return null;
        foreach (var p in m.EnumerateObject())
        {
            if (!string.Equals(p.Name, "contextLength", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) return n;
            if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out n)) return n;
            return null;
        }
        return null;
    }

    public Task<IReadOnlyList<RemoteFile>> ListFilesAsync(Guid storageId, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            var items = await ListPagedAsync<FileDto>($"api/storages/{storageId}/files", null, "fileName", t);
            return (IReadOnlyList<RemoteFile>)items
                .Select(i => new RemoteFile(i.Id, i.FileName ?? "", i.ContentSize, i.Status ?? "") { Tags = i.Tags ?? [] })
                .ToList();
        });

    /// <summary>Reads a Gridify page wrapper <c>{count, data}</c> to its end.</summary>
    private async Task<List<T>> ListPagedAsync<T>(string path, string? filter, string orderBy, CancellationToken ct)
    {
        var result = new List<T>();
        for (var page = 1; ; page++)
        {
            var query = new StringBuilder();
            if (filter is not null) query.Append("filter=").Append(Uri.EscapeDataString(filter)).Append('&');
            query.Append("pageSize=").Append(PageSize).Append("&page=").Append(page)
                 .Append("&orderBy=").Append(orderBy); // a fixed order — the default (updatedAt desc) shifts page boundaries if it changes mid-list

            using var response = await _http.GetAsync($"{path}?{query}", ct);
            await EnsureSuccessAsync(response, ct);
            var paged = await response.Content.ReadFromJsonAsync<Paged<T>>(Json, ct)
                        ?? throw new HttpRequestException("The vault-ai list response is empty.");
            var data = paged.Data ?? [];
            result.AddRange(data);
            // The end is not judged by a short page — if the server caps pageSize lower, the list would be cut off.
            if (data.Count == 0 || result.Count >= paged.Count) return result;
        }
    }

    /// <summary>Gridify value escaping: a backslash before <c>\ , | ( )</c>.</summary>
    private static string EscapeFilterValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or ',' or '|' or '(' or ')') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- storage ----

    public Task<Guid> CreateStorageAsync(string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default)
        => Run(ct, t => CreateAsync("api/storages", StorageBody(name, description, embedding, language, rerank, audio: null), t));

    public Task UpdateStorageAsync(Guid id, string name, string description, Guid? embedding, Guid? language, Guid? rerank, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            // The storage has an audio model too, which a folder chatbot neither sets nor uses. The server's PUT replaces the whole storage, so
            // leaving it out would clear one an operator set by hand — it is read and sent back, like the other settings this client does not own.
            using var get = await _http.GetAsync($"api/storages/{id}", t);
            await EnsureSuccessAsync(get, t);
            var current = await get.Content.ReadFromJsonAsync<StorageDto>(Json, t)
                          ?? throw new HttpRequestException("The vault-ai storage response is empty.");

            using var response = await _http.PutAsJsonAsync(
                $"api/storages/{id}", StorageBody(name, description, embedding, language, rerank, current.AudioModelId), Json, t);
            await EnsureSuccessAsync(response, t);
        });

    // PUT replaces everything — a null model id empties it on the server. The description must be non-null.
    private static object StorageBody(string name, string? description, Guid? embedding, Guid? language, Guid? rerank, Guid? audio) => new
    {
        name,
        description = description ?? "",
        embeddingModelId = embedding,
        languageModelId = language,
        audioModelId = audio,
        rerankModelId = rerank,
    };

    public Task DeleteStorageAsync(Guid id, CancellationToken ct = default) => Run(ct, t => DeleteAsync($"api/storages/{id}", t));

    public Task<bool> StorageExistsAsync(Guid id, CancellationToken ct = default) => Run(ct, t => ExistsAsync($"api/storages/{id}", t));

    public Task<bool> AgentExistsAsync(Guid id, CancellationToken ct = default) => Run(ct, t => ExistsAsync($"api/agents/{id}", t));

    public Task<bool> ChannelExistsAsync(Guid id, CancellationToken ct = default) => Run(ct, t => ExistsAsync($"api/channels/{id}", t));

    private async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public Task<StorageSummary> GetStorageSummaryAsync(Guid storageId, IReadOnlyList<string>? tags = null, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            using var response = await _http.GetAsync($"api/storages/{storageId}/summary{TagsQuery(tags)}", t);
            await EnsureSuccessAsync(response, t);
            var dto = await response.Content.ReadFromJsonAsync<SummaryDto>(Json, t)
                      ?? throw new HttpRequestException("The vault-ai summary response is empty.");
            return new StorageSummary(dto.TotalCount, dto.ByStatus ?? new Dictionary<string, int>());
        });

    /// <summary><c>?tags=a&amp;tags=b</c> — empty text when there are none.</summary>
    private static string TagsQuery(IReadOnlyList<string>? tags)
        => tags is not { Count: > 0 } ? "" : "?" + string.Join("&", tags.Select(t => "tags=" + Uri.EscapeDataString(t)));

    // ---- connectors ----

    public Task<Guid> CreateMcpConnectorAsync(string name, string description, string url,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
        => Run(ct, t => CreateAsync("api/connectors", McpConnectorBody(name, description, url, headers), t));

    public Task UpdateMcpConnectorAsync(Guid id, string name, string description, string url,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            using var response = await _http.PutAsJsonAsync($"api/connectors/{id}", McpConnectorBody(name, description, url, headers), Json, t);
            await EnsureSuccessAsync(response, t);
        });

    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default) => Run(ct, t => DeleteAsync($"api/connectors/{id}", t));

    // config is a polymorphic body — it needs the discriminator type (= mcp). An MCP connector supports only an address and headers.
    private static object McpConnectorBody(string name, string? description, string url, IReadOnlyDictionary<string, string> headers) => new
    {
        name,
        description = description ?? "",
        config = new { type = "mcp", url, headers },
    };

    // ---- agent ----

    public Task<Guid> CreateAgentAsync(string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default)
        // thinkingEffort and webSearch are left out — the server's default (none).
        => Run(ct, t => CreateAsync("api/agents", new
        {
            name,
            description = description ?? "",
            modelId,
            instructions,
            connectors,
            storages,
            scopes = ScopesBody(scopes),
        }, t));

    public Task UpdateAgentAsync(Guid id, string name, string description, Guid? modelId, string instructions,
        IReadOnlyList<Guid> connectors, IReadOnlyList<Guid> storages, IReadOnlyList<AgentScope> scopes, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            // The server's PUT DTO has non-null value types thinkingEffort and webSearch — leaving them out would overwrite them with none,
            // so the current values are read and sent back. The same holds for the sub-agents the agent has: the server replaces that mapping
            // with whatever the request carries, and a request that says nothing about it carries an empty list (checked against the
            // server's AgentService) — so a sub-agent an operator attached to this agent by hand would be silently detached.
            using var get = await _http.GetAsync($"api/agents/{id}", t);
            await EnsureSuccessAsync(get, t);
            var current = await get.Content.ReadFromJsonAsync<AgentDto>(Json, t)
                          ?? throw new HttpRequestException("The vault-ai agent response is empty.");

            // connectors, storages and sub-agents replace the whole mapping (leaving them out removes them all), so the whole list is always sent.
            using var response = await _http.PutAsJsonAsync($"api/agents/{id}", new
            {
                name,
                description = description ?? "",
                modelId,
                instructions,
                thinkingEffort = current.ThinkingEffort ?? "none",
                webSearch = current.WebSearch ?? "none",
                connectors,
                storages,
                subAgents = (current.SubAgents ?? []).Select(s => s.Id).ToArray(),
                scopes = ScopesBody(scopes), // null means "do not change" — it is always sent, even as an empty list
            }, Json, t);
            await EnsureSuccessAsync(response, t);
        });

    private static object[] ScopesBody(IReadOnlyList<AgentScope> scopes)
        => scopes.Select(s => (object)new { name = s.Name, tags = s.Tags, instructions = s.Instructions, connectorIds = s.ConnectorIds })
            .ToArray();

    public Task DeleteAgentAsync(Guid id, CancellationToken ct = default) => Run(ct, t => DeleteAsync($"api/agents/{id}", t));

    // ---- channel ----

    public Task<Guid> CreateWebChannelAsync(string name, Guid agentId, CancellationToken ct = default)
        => Run(ct, t => CreateAsync("api/channels", new { name, agentId, config = new { type = "web" } }, t));

    public Task DeleteChannelAsync(Guid id, CancellationToken ct = default) => Run(ct, t => DeleteAsync($"api/channels/{id}", t));

    public Task<ChannelToken> IssueChannelTokenAsync(Guid channelId, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            using var response = await _http.PostAsync($"api/channels/{channelId}/token", null, t);
            await EnsureSuccessAsync(response, t, includeBody: false); // the body may hold the token, so a failure message does not carry it
            var dto = await response.Content.ReadFromJsonAsync<TokenDto>(Json, t);
            if (dto is null || string.IsNullOrEmpty(dto.Token))
                throw new HttpRequestException("The vault-ai channel token response is empty.");
            return new ChannelToken(dto.Token, dto.ExpiresAt);
        });

    // ---- files ----

    public Task<Guid> UploadFileAsync(Guid storageId, string fileName, IReadOnlyList<string> tags, Stream content, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            using var form = new MultipartFormDataContent
            {
                // The relative path (Korean names included) goes as UTF-8 as it is — the default encoding turns a non-ASCII file name into a form the server cannot read.
                HeaderEncodingSelector = (_, _) => Encoding.UTF8,
            };
            var file = new StreamContent(content);
            file.Headers.TryAddWithoutValidation("Content-Disposition",
                $"form-data; name=\"files\"; filename=\"{fileName.Replace("\"", "%22")}\"");
            form.Add(file);
            // The server's form model has List<string> Tags — one field of the same name for each tag.
            foreach (var tag in tags) form.Add(new StringContent(tag, Encoding.UTF8), "tags");

            using var response = await _http.PostAsync($"api/storages/{storageId}/files", form, t);
            await EnsureSuccessAsync(response, t, uploading: true);
            var files = await response.Content.ReadFromJsonAsync<List<FileDto>>(Json, t);
            if (files is not { Count: > 0 })
                throw new HttpRequestException("The vault-ai upload response has no file information.");
            return files[0].Id;
        });

    public Task DeleteFileAsync(Guid storageId, Guid fileId, CancellationToken ct = default)
        => Run(ct, t => DeleteAsync($"api/storages/{storageId}/files/{fileId}", t));

    public Task RetryFilePipelineAsync(Guid storageId, Guid fileId, CancellationToken ct = default)
        => Run(ct, async t =>
        {
            using var response = await _http.PostAsync($"api/storages/{storageId}/files/{fileId}/pipeline/retry", null, t);
            await EnsureSuccessAsync(response, t, conflictIsBusy: true);
        });

    // ---- common ----

    private async Task<Guid> CreateAsync(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureSuccessAsync(response, ct);
        var dto = await response.Content.ReadFromJsonAsync<NamedDto>(Json, ct)
                  ?? throw new HttpRequestException("The vault-ai create response is empty.");
        return dto.Id;
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // On failure the response body goes into the exception, in the same form as VaultAIClient (status code + the first 1000 characters).
    // 415 and 413 are an unsupported kind / over the size limit only for an upload (a general failure elsewhere); 409 is "being processed"
    // for a delete or a reprocess — the caller tells them apart.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct, bool uploading = false,
        bool conflictIsBusy = false, bool includeBody = true)
    {
        if (response.IsSuccessStatusCode) return;

        var message = $"vault-ai responded {(int)response.StatusCode} ({response.StatusCode})";
        if (includeBody)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 1000 ? body[..1000] + "…(truncated)" : body;
            message += ": " + snippet;
        }
        var method = response.RequestMessage?.Method;

        throw response.StatusCode switch
        {
            HttpStatusCode.UnsupportedMediaType or HttpStatusCode.RequestEntityTooLarge when uploading
                => new VaultAIUnsupportedFileException(message, response.StatusCode),
            HttpStatusCode.Conflict when method == HttpMethod.Delete || conflictIsBusy => new VaultAIConflictException(message),
            _ => new HttpRequestException(message, null, response.StatusCode),
        };
    }

    private sealed record Paged<T>(int Count, List<T>? Data);
    private sealed record NamedDto(Guid Id, string? Name, string? Description);
    private sealed record StorageDto(Guid Id, string? Name, string? Description, Guid? EmbeddingModelId, Guid? LanguageModelId,
        Guid? RerankModelId, Guid? AudioModelId = null);
    private sealed record ModelDto(Guid Id, string? ModelId, string? DisplayName, string? Type, JsonElement? Metadata);
    private sealed record FileDto(Guid Id, string? FileName, long ContentSize, string? Status, List<string>? Tags);
    private sealed record TokenDto(string? Token, DateTimeOffset ExpiresAt);
    private sealed record SummaryDto(int TotalCount, Dictionary<string, int>? ByStatus);
    private sealed record AgentDto(string? ThinkingEffort, string? WebSearch, List<IdDto>? SubAgents);
    private sealed record IdDto(Guid Id);
}
