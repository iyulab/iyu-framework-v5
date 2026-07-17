using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iyu.VaultAi;

public class VaultAiClient : IVaultAiClient
{
    private readonly HttpClient _http;
    private readonly VaultAiSettings _settings;

    public VaultAiClient(IOptions<VaultAiSettings> options)
    {
        _settings = options.Value;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.Token);
        _http.BaseAddress = new Uri(_settings.Url.TrimEnd('/') + "/");
    }

    public async Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/agents/{agentId}/messages",
            new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        id = Guid.NewGuid().ToString(),
                        content = new[] { new { type = "text", text = prompt } },
                        createdAt = DateTimeOffset.UtcNow.ToString("o")
                    }
                },
                stream = false
            }, ct);

        // 비성공 시 응답 본문을 예외에 포함 — vault-ai가 돌려준 실제 사유(프롬프트 과대·
        // 컨텍스트 초과·모델 오류 등)를 호출자 로그에서 추적할 수 있도록.
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var snippet   = errorBody.Length > 1000 ? errorBody[..1000] + "…(중략)" : errorBody;
            throw new HttpRequestException(
                $"vault-ai 응답 {(int)response.StatusCode} ({response.StatusCode}): {snippet}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // 신규 스키마: { "message": { "content": [ { "type": "text", "text": ... } ] } }
        // message.content 자체가 없으면 스키마 불일치 — 빈 리포트가 조용히 생성되는 것을
        // 막기 위해 예외로 끌어올린다(silent-failure 방지).
        if (!doc.RootElement.TryGetProperty("message", out var messageProp)
            || !messageProp.TryGetProperty("content", out var contentProp))
        {
            var snippet = json.Length > 1000 ? json[..1000] + "…(중략)" : json;
            throw new HttpRequestException($"vault-ai 응답에 message.content가 없습니다: {snippet}");
        }

        // text 타입 파트 중 마지막을 채택 — reasoning/tool 파트가 앞에 섞여 와도 최종 답변을 취함.
        string? text = null;
        foreach (var item in contentProp.EnumerateArray())
            if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text"
                && item.TryGetProperty("text", out var textProp))
                text = textProp.GetString();

        return text ?? string.Empty;
    }

    public async Task<JsonNode> GetStructuredMessageAsync(
        Guid agentId,
        string prompt,
        JsonNode outputSchema,
        IReadOnlyList<VaultAiImage>? images = null,
        CancellationToken ct = default)
    {
        // content 파트 조립 — 텍스트 + (선택) 이미지. 이미지는 vault-ai wire 계약상
        // "file" 콘텐츠의 data 블록([{type:"image", mimeType, data(base64)}])으로 실린다
        // (VaultAI.WebServer VaultFileContent/ImageBlock — 채널 파일업로드 경유 없이 인라인 전달).
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var img in images ?? [])
        {
            content.Add(new
            {
                type = "file",
                id = Guid.NewGuid().ToString(),
                fileName = img.FileName ?? "image",
                contentType = img.MimeType,
                contentSize = img.Data.LongLength,
                data = new object[]
                {
                    new { type = "image", mimeType = img.MimeType, data = Convert.ToBase64String(img.Data) },
                },
            });
        }

        var response = await _http.PostAsJsonAsync(
            $"api/agents/{agentId}/messages",
            new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        id = Guid.NewGuid().ToString(),
                        content = (object)content,
                        createdAt = DateTimeOffset.UtcNow.ToString("o")
                    }
                },
                stream = false,
                output = outputSchema,   // JSON schema → 응답 output으로 구조화 수신
            }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var snippet   = errorBody.Length > 1000 ? errorBody[..1000] + "…(중략)" : errorBody;
            throw new HttpRequestException(
                $"vault-ai 응답 {(int)response.StatusCode} ({response.StatusCode}): {snippet}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);

        // 1순위: 응답 최상위 output(서버가 파싱해 준 구조화 결과).
        var output = root?["output"];
        if (output is not null) return output;

        // 폴백: 마지막 text 파트를 JSON으로 파싱(구버전 서버·output 미주입 경로 호환).
        var contentArr = root?["message"]?["content"] as JsonArray;
        var lastText = contentArr?
            .OfType<JsonObject>()
            .Where(o => (string?)o["type"] == "text")
            .Select(o => (string?)o["text"])
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (lastText is not null)
        {
            try { return JsonNode.Parse(lastText) ?? throw new JsonException("null JSON"); }
            catch (JsonException ex)
            {
                var snippet = lastText.Length > 500 ? lastText[..500] + "…(중략)" : lastText;
                throw new HttpRequestException($"vault-ai 응답 text가 유효한 JSON이 아닙니다: {ex.Message} — {snippet}");
            }
        }

        // output도 text도 없음 — 스키마 불일치를 조용히 흘리지 않는다(silent-failure 방지).
        var bodySnippet = json.Length > 1000 ? json[..1000] + "…(중략)" : json;
        throw new HttpRequestException($"vault-ai 응답에 output/text가 없습니다: {bodySnippet}");
    }
}
