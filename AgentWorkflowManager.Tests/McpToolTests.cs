using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

namespace AgentWorkflowManager.Tests;

public sealed class McpToolTests
{
    [Fact]
    public void McpToolConfigLoader_ReadsDescriptorsFromFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mcp-tools-{Guid.NewGuid():N}.json");
        var json = """
        {
          "tools": [
            {
              "name": "demo_tool",
              "description": "Demo MCP tool.",
              "server": "demo",
              "endpoint": "https://demo.example.com/mcp",
              "command": "demo.do",
              "parameters": {
                "type": "object",
                "properties": {
                  "value": { "type": "string" }
                },
                "required": ["value"]
              }
            }
          ]
        }
        """;

        File.WriteAllText(tempFile, json);

        try
        {
            var descriptors = McpToolConfigLoader.LoadFromFile(tempFile);
            Assert.Single(descriptors);
            var descriptor = descriptors[0];
            Assert.Equal("demo_tool", descriptor.Name);
            Assert.Equal("demo", descriptor.Server);
            Assert.NotNull(descriptor.Parameters);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Workflow_InvokesMcpToolClient()
    {
        var manager = new WorkflowManager(maxTurns: 4);

        var agent = new ToolRespondingAgent(
            "analyst",
            new[]
            {
                CreateToolRun("assistant", "Je consulte le fichier via MCP.", "spreadsheet_reader", "tool-call-1", """{"file_path":"report.xlsx","sheet":"Budget"}"""),
                CreateMessage("assistant", "Analyse terminée.")
            },
            new[] { "spreadsheet_reader" });

        manager.RegisterAgent(agent);

        var descriptor = new McpToolDescriptor
        {
            Name = "spreadsheet_reader",
            Description = "Lit un fichier Excel via MCP.",
            Server = "spreadsheet-service",
            Endpoint = "https://demo.example.com/mcp",
            Command = "excel.read",
            Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file_path": { "type": "string" },
                "sheet": { "type": "string" }
              },
              "required": ["file_path", "sheet"]
            }
            """)
        };

        var client = new FakeMcpToolClient();
        manager.RegisterTool(new McpAgentTool(descriptor, client));

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "Analyse le budget via MCP."),
        });

        var result = await manager.RunAgentAsync("analyst", request);

        Assert.Single(client.Invocations);
        var invocation = client.Invocations[0];
        Assert.Equal("spreadsheet_reader", invocation.Descriptor.Name);
        Assert.Contains("\"sheet\":\"Budget\"", invocation.ArgumentsJson);

        var toolMessage = result.Conversation.FirstOrDefault(message => message.Role == "tool");
        Assert.NotNull(toolMessage);

        var toolContent = Assert.IsType<AgentToolResultContent>(toolMessage!.Content[0]);
        Assert.Contains("MCP result", toolContent.Output);
    }

    private static AgentRunResult CreateMessage(string role, string text)
        => new(AgentMessage.FromText(role, text), Array.Empty<AgentToolCall>());

    private static AgentRunResult CreateToolRun(string role, string text, string toolName, string callId, string argumentsJson)
    {
        return new AgentRunResult(
            AgentMessage.FromText(role, text),
            new[]
            {
                new AgentToolCall(toolName, callId, JsonDocument.Parse(argumentsJson)),
            });
    }

    private sealed class ToolRespondingAgent : IAgent, IToolAwareAgent
    {
        private readonly Queue<AgentRunResult> _responses;

        public ToolRespondingAgent(string name, IEnumerable<AgentRunResult> responses, IEnumerable<string> toolNames)
        {
            Descriptor = new AgentDescriptor(name, $"{name} description", "gpt-5-nano");
            _responses = new Queue<AgentRunResult>(responses);
            ToolNames = new List<string>(toolNames);
        }

        public AgentDescriptor Descriptor { get; }

        public IReadOnlyCollection<string> ToolNames { get; }

        public Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                return Task.FromResult(new AgentRunResult(null, Array.Empty<AgentToolCall>()));
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeMcpToolClient : IMcpToolClient
    {
        public List<(McpToolDescriptor Descriptor, string ArgumentsJson)> Invocations { get; } = new();

        public Task<string> InvokeAsync(McpToolDescriptor descriptor, JsonDocument arguments, CancellationToken cancellationToken)
        {
            Invocations.Add((descriptor, arguments.RootElement.GetRawText()));
            return Task.FromResult($"MCP result for {descriptor.Name}: {arguments.RootElement.GetRawText()}");
        }
    }

    [Fact]
    public async Task McpAgentTool_ReturnsErrorWhenClientThrows()
    {
        var descriptor = new McpToolDescriptor
        {
            Name = "analysis.get_tab_data",
            Description = "Analyse",
            Server = "analysis",
            Endpoint = "https://demo.example.com/mcp",
            Parameters = JsonNode.Parse("""{"type":"object"}"""),
        };

        var client = new ThrowingClient();
        var tool = new McpAgentTool(descriptor, client);
        using var arguments = JsonDocument.Parse("{}");
        var toolCall = new AgentToolCall(descriptor.Name, "call-1", JsonDocument.Parse("{}"));
        var context = new ToolInvocationContext(toolCall, (_, _, _) => Task.FromResult(new AgentWorkflowResult(null, Array.Empty<AgentMessage>())));

        var result = await tool.InvokeAsync(context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("failed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingClient : IMcpToolClient
    {
        public Task<string> InvokeAsync(McpToolDescriptor descriptor, JsonDocument arguments, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }
}
