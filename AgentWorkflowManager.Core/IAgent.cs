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
    public AgentWorkflowResult(AgentMessage? finalMessage, IReadOnlyList<AgentMessage> conversation)
    {
        FinalMessage = finalMessage;
        Conversation = conversation;
    }

    public AgentMessage? FinalMessage { get; }

    public IReadOnlyList<AgentMessage> Conversation { get; }
}
