namespace Iyu.VaultAi;

public interface IVaultAiClient
{
    Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default);
}
