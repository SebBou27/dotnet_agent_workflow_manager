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

public sealed class AgentSessionTests
{
    [Fact]
    public async Task Session_AppendsConversationAcrossTurns()
    {
        var manager = new WorkflowManager(maxTurns: 4);
        var scriptedAgent = new ScriptedAgent(
            "primary",
            new[]
            {
                AgentRunResultWithMessage("assistant", "Bonjour."),
                AgentRunResultWithMessage("assistant", "Je vais bien."),
            });

        manager.RegisterAgent(scriptedAgent);

        var session = new AgentSession(manager, "primary");

        var result1 = await session.SendAsync("Salut");
        Assert.Equal(2, result1.Conversation.Count);

        var result2 = await session.SendAsync("Comment vas-tu ?");

        Assert.Equal(4, session.Conversation.Count);
        Assert.Equal("user", session.Conversation[0].Role);
        Assert.Equal("assistant", session.Conversation[1].Role);
        Assert.Equal("user", session.Conversation[2].Role);
        Assert.Equal("assistant", session.Conversation[3].Role);

        Assert.Equal("Je vais bien.", session.GetLatestAssistantText());
        Assert.Equal("Je vais bien.", string.Join(Environment.NewLine, result2.FinalMessage!.Content.OfType<AgentTextContent>().Select(c => c.Text)));
    }

    [Fact]
    public async Task Session_PersistsToolResultsInConversation()
    {
        var manager = new WorkflowManager(maxTurns: 4);

        var scriptedAgent = new ScriptedAgent(
            "primary",
            new[]
            {
                AgentRunResultWithTool("assistant", "Utilisation d'un outil.", "delegate", "call-1", """{"query":"Aide"}"""),
                AgentRunResultWithMessage("assistant", "Résultat final."),
            });

        manager.RegisterAgent(scriptedAgent);
        manager.RegisterAgent(new ScriptedAgent("helper", new[] { AgentRunResultWithMessage("assistant", "Réponse de l'aide.") }));
        manager.RegisterTool(new DelegatingTestTool("delegate", "helper"));

        var session = new AgentSession(manager, "primary");
        var result = await session.SendAsync("Peux-tu demander de l'aide ?");

        Assert.Contains(session.Conversation, message => message.Role == "tool");

        var toolMessage = session.Conversation.First(m => m.Role == "tool");
        var toolContent = Assert.IsType<AgentToolResultContent>(toolMessage.Content.Single());
        Assert.Contains("Réponse de l'aide", toolContent.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Résultat final.", session.GetLatestAssistantText());
        Assert.NotNull(result.FinalMessage);
    }

    private static AgentRunResult AgentRunResultWithMessage(string role, string text)
        => new(AgentMessage.FromText(role, text), Array.Empty<AgentToolCall>());

    private static AgentRunResult AgentRunResultWithTool(string role, string text, string toolName, string callId, string argumentsJson)
    {
        return new AgentRunResult(
            AgentMessage.FromText(role, text),
            new[]
            {
                new AgentToolCall(toolName, callId, JsonDocument.Parse(argumentsJson)),
            });
    }

    private sealed class ScriptedAgent : IAgent
    {
        private readonly Queue<AgentRunResult> _responses;

        public ScriptedAgent(string name, IEnumerable<AgentRunResult> responses)
        {
            Descriptor = new AgentDescriptor(name, $"Scripted agent {name}", "gpt-5-nano");
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
        public DelegatingTestTool(string name, string agentName)
        {
            Name = name;
            Definition = new ToolDefinition(name, $"Delegates to {agentName}", JsonNode.Parse("""{"type":"object"}""")!);
            AgentName = agentName;
        }

        public string Name { get; }

        public ToolDefinition Definition { get; }

        private string AgentName { get; }

        public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
        {
            var downstream = new AgentRequest(new[]
            {
                AgentMessage.FromText("user", "Fais une réponse."),
            });

            var result = await context.CallAgentAsync(AgentName, downstream, cancellationToken).ConfigureAwait(false);

            var text = result.FinalMessage is null
                ? "Sans réponse."
                : string.Join(Environment.NewLine, result.FinalMessage.Content.OfType<AgentTextContent>().Select(c => c.Text));

            return new AgentToolExecutionResult(context.ToolCall.CallId, text);
        }
    }
}
