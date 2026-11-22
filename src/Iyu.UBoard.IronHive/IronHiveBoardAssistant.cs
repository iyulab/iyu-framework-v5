using System.Text.Json;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronProw.Core;
using IronProw.IronHive;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard.IronHive;

/// <summary>
/// <see cref="IBoardAssistant"/> over <c>IronHive.Agent</c>: the loop runs here, the model service is reached through IronProw, and the
/// tools the caller declared are offered to the model as tools only the caller can run — so an answer is either the model's words or the
/// tool calls the screen now has to make.
/// </summary>
/// <remarks>
/// The model service is kept to this adapter: it is built on a service provider of its own, so it does not become the host's
/// <c>IChatClient</c> or compete with one the host registers for something else.
/// </remarks>
internal sealed class IronHiveBoardAssistant : IBoardAssistant, IDisposable
{
    // The conversation travels as the messages of Microsoft.Extensions.AI in the wire shape that library gives them.
    private static readonly JsonSerializerOptions Wire = AIJsonUtilities.DefaultOptions;

    private readonly ServiceProvider _models;
    private readonly IChatClient _chat;
    private readonly int _maxOutputTokens;

    public IronHiveBoardAssistant(IOptions<IronHiveAssistantOptions> options)
    {
        var settings = options.Value;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIronProw().AddIronHiveOpenAICompatible("model", 10, settings.Model!, config =>
        {
            config.BaseUrl = settings.BaseUrl;
            config.ApiKey = settings.ApiKey;
            config.Headers = new Dictionary<string, string>(settings.Headers) { ["User-Agent"] = settings.UserAgent };
        });
        _models = services.BuildServiceProvider();
        _chat = _models.GetRequiredService<IChatClient>();
        _maxOutputTokens = settings.MaxOutputTokens;
    }

    public async Task<BoardAssistantAnswer> TakeTurnAsync(BoardAssistantTurn turn, CancellationToken cancellationToken = default)
    {
        var agent = new AgentOptions
        {
            SystemPrompt = turn.System ?? "",
            Tools = [.. turn.Tools.Select(t => AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.Schema))],
            // A model that thinks first spends the reply's tokens on it, and a long think can pass the time a gateway will wait.
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            MaxTokens = _maxOutputTokens,
        };
        var loop = new AgentLoop(_chat.AsBuilder().UseFunctionInvocation().Build(), agent);

        List<ChatMessage> history;
        try
        {
            history = [.. turn.History.Select(m => m.Deserialize<ChatMessage>(Wire) ?? throw new JsonException("an empty message"))];
        }
        catch (JsonException ex)
        {
            throw new BoardAssistantException(BoardAssistantException.BadRequest, "the history is not a conversation this assistant gave", ex);
        }

        loop.InitializeHistory(history);
        try
        {
            if (turn.Results is not null)
                loop.InitializeHistory([.. loop.History, new ChatMessage(ChatRole.Tool, [.. turn.Results.Select(r => new FunctionResultContent(r.CallId, r.Output))])]);

            // The screen ended the turn itself: the results it owes are recorded with the words it said, and the model is not asked again.
            if (turn.Final is not null)
            {
                loop.InitializeHistory([.. loop.History, new ChatMessage(ChatRole.Assistant, turn.Final)]);
                return new BoardAssistantAnswer(BoardAssistantAnswer.Completed, turn.Final, [], Conversation(loop));
            }

            TurnRecord? record = null;
            await foreach (var chunk in turn.Message is { } message ? loop.RunStreamingAsync(message, cancellationToken) : loop.ContinueStreamingAsync(cancellationToken))
                record = chunk.Turn ?? record;

            return new BoardAssistantAnswer(
                record?.StopReason.ToString() ?? BoardAssistantAnswer.Completed,
                record?.Content,
                [.. (record?.ToolCalls ?? []).Select(c => new BoardToolCall(c.CallId ?? throw new InvalidOperationException("the model asked for a tool without an id for the call"), c.ToolName, c.Arguments))],
                Conversation(loop),
                record?.Usage is { } usage ? JsonSerializer.SerializeToElement(usage, Wire) : null);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or BoardAssistantException))
        {
            // The kind the agent's own classifier found; what the service said (it may name the service) stays with the host.
            var kind = ex is GuardException ? "guard" : new ErrorRecoveryService(new ErrorRecoveryConfig(), []).AnalyzeException(ex, "turn").Error.Category.ToString().ToLowerInvariant();
            throw new BoardAssistantException(kind, "the model could not answer a turn", ex);
        }
    }

    public void Dispose() => _models.Dispose();

    private static List<JsonElement> Conversation(IAgentLoop loop) =>
        [.. loop.History.Where(m => m.Role != ChatRole.System).Select(m => JsonSerializer.SerializeToElement(m, Wire))];
}
