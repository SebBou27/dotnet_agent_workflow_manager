using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

public interface IAgent
{
    AgentDescriptor Descriptor { get; }

    Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken);
}

public sealed class AgentWorkflowResult
{
    public AgentWorkflowResult(AgentMessage? finalMessage, IReadOnlyList<AgentMessage> conversation, AgentWorkflowMetrics? metrics = null)
    {
        FinalMessage = finalMessage;
        Conversation = conversation;
        Metrics = metrics;
    }

    public AgentMessage? FinalMessage { get; }

    public IReadOnlyList<AgentMessage> Conversation { get; }

    public AgentWorkflowMetrics? Metrics { get; }
}

public sealed class AgentWorkflowMetrics
{
    public int Turns { get; init; }
    public int ToolCallsRequested { get; init; }
    public int ToolCallsSucceeded { get; init; }
    public int ToolCallsFailed { get; init; }
    public long DurationMs { get; init; }
}
