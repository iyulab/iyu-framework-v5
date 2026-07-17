using System.Text.Json.Nodes;

namespace Iyu.VaultAi;

/// <summary>agent 메시지에 첨부할 이미지(멀티모달 입력). MimeType: image/png·jpeg·gif·webp.</summary>
public sealed record VaultAiImage(string MimeType, byte[] Data, string? FileName = null);

public interface IVaultAiClient
{
    Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default);

    /// <summary>
    /// structured output 호출 — vault-ai agent에 JSON schema(<paramref name="outputSchema"/>)를 지정해
    /// 스키마 준수 JSON을 응답 output으로 직접 수신한다. 이미지(멀티모달)를 함께 보낼 수 있다.
    /// 응답에 유효한 output이 없으면 예외를 던진다(silent-failure 방지).
    /// </summary>
    Task<JsonNode> GetStructuredMessageAsync(
        Guid agentId,
        string prompt,
        JsonNode outputSchema,
        IReadOnlyList<VaultAiImage>? images = null,
        CancellationToken ct = default);
}
