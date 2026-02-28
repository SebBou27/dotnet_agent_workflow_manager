using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AgentWorkflowManager.Core;

public sealed class AgentSessionMemoryStore
{
    private readonly string _directory;

    public AgentSessionMemoryStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory cannot be null or whitespace.", nameof(directory));
        }

        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<AgentMessage> LoadConversation(string sessionKey)
    {
        var path = BuildPath(sessionKey);
        if (!File.Exists(path))
        {
            return Array.Empty<AgentMessage>();
        }

        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(json);
        if (snapshot?.Messages is null)
        {
            return Array.Empty<AgentMessage>();
        }

        return snapshot.Messages.Select(ToAgentMessage).ToArray();
    }

    public void SaveConversation(string sessionKey, IReadOnlyList<AgentMessage> conversation)
    {
        var path = BuildPath(sessionKey);
        var snapshot = new SessionSnapshot
        {
            SavedAtUtc = DateTime.UtcNow,
            Messages = conversation.Select(FromAgentMessage).ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private string BuildPath(string sessionKey)
    {
        var sanitized = string.Concat(sessionKey.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(_directory, $"{sanitized}.json");
    }

    private static StoredMessage FromAgentMessage(AgentMessage message)
    {
        return new StoredMessage
        {
            Role = message.Role,
            Author = message.Author,
            Content = message.Content.Select(content => content switch
            {
                AgentTextContent text => new StoredContent { Type = "text", Text = text.Text },
                AgentToolResultContent tool => new StoredContent { Type = "tool_result", ToolCallId = tool.ToolCallId, Output = tool.Output, IsError = tool.IsError },
                _ => new StoredContent { Type = "text", Text = string.Empty },
            }).ToList(),
        };
    }

    private static AgentMessage ToAgentMessage(StoredMessage message)
    {
        var content = message.Content.Select(item => item.Type switch
        {
            "tool_result" => (AgentContent)new AgentToolResultContent(item.ToolCallId ?? "unknown", item.Output ?? string.Empty, item.IsError),
            _ => new AgentTextContent(item.Text ?? string.Empty),
        }).ToArray();

        return new AgentMessage(message.Role ?? "user", content, message.Author);
    }

    private sealed class SessionSnapshot
    {
        public DateTime SavedAtUtc { get; set; }
        public List<StoredMessage> Messages { get; set; } = new();
    }

    private sealed class StoredMessage
    {
        public string? Role { get; set; }
        public string? Author { get; set; }
        public List<StoredContent> Content { get; set; } = new();
    }

    private sealed class StoredContent
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? ToolCallId { get; set; }
        public string? Output { get; set; }
        public bool IsError { get; set; }
    }
}
