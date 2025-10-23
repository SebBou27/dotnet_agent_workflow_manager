using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgentWorkflowManager.Core;

public sealed class AgentRequest
{
    public AgentRequest(IReadOnlyList<AgentMessage> messages)
    {
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
    }

    public IReadOnlyList<AgentMessage> Messages { get; }
}

public sealed class AgentRunResult
{
    public AgentRunResult(AgentMessage? assistantMessage, IReadOnlyList<AgentToolCall> toolCalls)
    {
        AssistantMessage = assistantMessage;
        ToolCalls = toolCalls ?? throw new ArgumentNullException(nameof(toolCalls));
    }

    public AgentMessage? AssistantMessage { get; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; }
}

public sealed class AgentToolCall
{
    public AgentToolCall(string name, string callId, JsonDocument arguments)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(callId))
        {
            throw new ArgumentException("Call id cannot be null or whitespace.", nameof(callId));
        }

        Name = name;
        CallId = callId;
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    public string Name { get; }

    public string CallId { get; }

    public JsonDocument Arguments { get; }
}

public sealed class AgentToolExecutionResult
{
    public AgentToolExecutionResult(string callId, string output, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            throw new ArgumentException("Call id cannot be null or whitespace.", nameof(callId));
        }

        CallId = callId;
        Output = output ?? throw new ArgumentNullException(nameof(output));
        IsError = isError;
    }

    public string CallId { get; }

    public string Output { get; }

    public bool IsError { get; }

    public AgentMessage ToAgentMessage()
    {
        var content = new AgentToolResultContent(CallId, Output, IsError);
        return new AgentMessage("tool", new AgentContent[] { content });
    }
}
