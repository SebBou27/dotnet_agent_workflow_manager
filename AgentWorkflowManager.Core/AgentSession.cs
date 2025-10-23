using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Maintains conversation state for repeated interactions with a specific agent.
/// </summary>
public sealed class AgentSession
{
    private readonly AgentWorkflowManager _manager;
    private readonly string _agentName;
    private readonly List<AgentMessage> _conversation;

    public AgentSession(AgentWorkflowManager manager, string agentName, IEnumerable<AgentMessage>? initialConversation = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name cannot be null or whitespace.", nameof(agentName));
        }

        _agentName = agentName;
        _conversation = initialConversation is null
            ? new List<AgentMessage>()
            : new List<AgentMessage>(initialConversation);
    }

    public IReadOnlyList<AgentMessage> Conversation => _conversation;

    public AgentWorkflowResult? LastResult { get; private set; }

    public Task<AgentWorkflowResult> SendAsync(string text, CancellationToken cancellationToken = default)
        => SendAsync(AgentMessage.FromText("user", text), cancellationToken);

    public async Task<AgentWorkflowResult> SendAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        _conversation.Add(message);

        var request = new AgentRequest(_conversation);
        var result = await _manager.RunAgentAsync(_agentName, request, cancellationToken).ConfigureAwait(false);

        _conversation.Clear();
        _conversation.AddRange(result.Conversation);
        LastResult = result;

        return result;
    }

    public AgentMessage? GetLatestAssistantMessage()
        => LastResult?.FinalMessage;

    public string? GetLatestAssistantText()
    {
        var assistantMessage = GetLatestAssistantMessage();
        if (assistantMessage is null)
        {
            return null;
        }

        return string.Join(Environment.NewLine, assistantMessage.Content.OfType<AgentTextContent>().Select(c => c.Text));
    }
}
