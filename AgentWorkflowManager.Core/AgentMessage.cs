using System;
using System.Collections.Generic;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Represents a single piece of content within a message.
/// </summary>
public abstract class AgentContent
{
    private protected AgentContent(string type) => Type = type;

    public string Type { get; }
}

public sealed class AgentTextContent : AgentContent
{
    public AgentTextContent(string text) : base("text")
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text { get; }
}

public sealed class AgentToolResultContent : AgentContent
{
    public AgentToolResultContent(string toolCallId, string output, bool isError = false) : base("tool_result")
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            throw new ArgumentException("Tool call id cannot be null or whitespace.", nameof(toolCallId));
        }

        ToolCallId = toolCallId;
        Output = output ?? throw new ArgumentNullException(nameof(output));
        IsError = isError;
    }

    public string ToolCallId { get; }

    public string Output { get; }

    public bool IsError { get; }
}

/// <summary>
/// Wrapper for an agent message suitable for the Responses API.
/// </summary>
public sealed class AgentMessage
{
    public AgentMessage(string role, IReadOnlyList<AgentContent> content, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(role));
        }

        Role = role;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Author = author;
    }

    public string Role { get; }

    public IReadOnlyList<AgentContent> Content { get; }

    public string? Author { get; }

    public static AgentMessage FromText(string role, string text, string? author = null)
        => new(role, new AgentContent[] { new AgentTextContent(text) }, author);

    public static AgentMessage FromToolResult(string toolCallId, string output, bool isError = false)
        => new("tool", new AgentContent[] { new AgentToolResultContent(toolCallId, output, isError) }, author: null);
}
