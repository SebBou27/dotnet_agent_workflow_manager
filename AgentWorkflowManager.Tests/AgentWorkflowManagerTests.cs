using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

namespace AgentWorkflowManager.Tests;

public sealed class AgentWorkflowManagerTests
{
    [Fact]
    public async Task RunAgentAsync_Throws_WhenAgentNotRegistered()
    {
        var manager = new WorkflowManager();
        var request = new AgentRequest(new[] { AgentMessage.FromText("user", "ping") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAgentAsync("unknown", request));
    }

    [Fact]
    public async Task RunAgentAsync_AppendsToolOutputsAndReturnsFinalMessage()
    {
        var manager = new WorkflowManager(maxTurns: 4);

        var primaryAgent = new ScriptedAgent(
            "primary",
            new[]
            {
                AgentRunResultWithTool("assistant", "Calling helper", "helper_tool", "call-1", """{"topic":"productivity"}"""),
                AgentRunResultWithMessage("assistant", "Here is your answer."),
            });

        var helperAgent = new ScriptedAgent(
            "helper",
            new[]
            {
                AgentRunResultWithMessage("assistant", "Helper advice response."),
            });

        manager.RegisterAgent(primaryAgent);
        manager.RegisterAgent(helperAgent);

        var proxyTool = new DelegatingTestTool("helper_tool", "helper");
        manager.RegisterTool(proxyTool);

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "I need help."),
        });

        var result = await manager.RunAgentAsync("primary", request);

        Assert.NotNull(result.FinalMessage);
        Assert.Equal("assistant", result.FinalMessage!.Role);

        var toolMessage = result.Conversation.Single(message => message.Role == "tool");
        var toolContent = Assert.IsType<AgentToolResultContent>(toolMessage.Content.Single());
        Assert.Contains("Helper advice response", toolContent.Output);
    }

    [Fact]
    public async Task RunAgentAsync_ContinuesWhenToolThrowsAndMarksError()
    {
        var manager = new WorkflowManager(maxTurns: 4);

        var primaryAgent = new ScriptedAgent(
            "primary",
            new[]
            {
                AgentRunResultWithTool("assistant", "Calling failing tool", "failing_tool", "call-1", """{"topic":"error"}"""),
                AgentRunResultWithMessage("assistant", "Tool failed, but continuing."),
            });

        manager.RegisterAgent(primaryAgent);
        manager.RegisterTool(new FailingTestTool("failing_tool"));

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "trigger a failure"),
        });

        var result = await manager.RunAgentAsync("primary", request);

        var toolMessage = result.Conversation.First(message => message.Role == "tool");
        var toolContent = Assert.IsType<AgentToolResultContent>(toolMessage.Content.Single());

        Assert.True(toolContent.IsError);
        Assert.Contains("failure", toolContent.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentRunResult AgentRunResultWithTool(string role, string text, string toolName, string callId, string argumentsJson)
    {
        return new AgentRunResult(
            AgentMessage.FromText(role, text),
            new[]
            {
                new AgentToolCall(toolName, callId, JsonDocument.Parse(argumentsJson)),
            });
    }

    private static AgentRunResult AgentRunResultWithMessage(string role, string text)
        => new(AgentMessage.FromText(role, text), Array.Empty<AgentToolCall>());

    private sealed class ScriptedAgent : IAgent
    {
        private readonly Queue<AgentRunResult> _responses;

        public ScriptedAgent(string name, IEnumerable<AgentRunResult> responses)
        {
            Descriptor = new AgentDescriptor(name, $"{name} description", "gpt-5-nano");
            _responses = new Queue<AgentRunResult>(responses);
        }

        public AgentDescriptor Descriptor { get; }

        public Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                return Task.FromResult(new AgentRunResult(null, Array.Empty<AgentToolCall>()));
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class DelegatingTestTool : IAgentTool
    {
        public DelegatingTestTool(string name, string targetAgent)
        {
            Name = name;
            Definition = new ToolDefinition(name, $"Delegates to {targetAgent}", JsonNode.Parse("""{"type":"object"}""")!);
            TargetAgent = targetAgent;
        }

        public string Name { get; }

        public ToolDefinition Definition { get; }

        private string TargetAgent { get; }

        public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
        {
            var downstreamRequest = new AgentRequest(new[]
            {
                AgentMessage.FromText("user", "help"),
            });

            var downstreamResult = await context.CallAgentAsync(TargetAgent, downstreamRequest, cancellationToken).ConfigureAwait(false);
            var finalText = downstreamResult.FinalMessage is null
                ? "No response."
                : string.Join(Environment.NewLine, downstreamResult.FinalMessage.Content.OfType<AgentTextContent>().Select(c => c.Text));

            return new AgentToolExecutionResult(context.ToolCall.CallId, finalText);
        }
    }

    private sealed class FailingTestTool : IAgentTool
    {
        public FailingTestTool(string name)
        {
            Name = name;
            Definition = new ToolDefinition(name, "Always fails.", JsonNode.Parse("""{"type":"object"}""")!);
        }

        public string Name { get; }

        public ToolDefinition Definition { get; }

        public Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated tool failure.");
    }
}
