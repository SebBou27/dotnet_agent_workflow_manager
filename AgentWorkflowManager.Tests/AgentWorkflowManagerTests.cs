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

    [Fact]
    public async Task RunAgentAsync_RetriesOnFailureAndSucceeds()
    {
        var retryPolicy = new AgentRetryPolicy(maxAttempts: 3, delayBetweenAttempts: TimeSpan.Zero);
        var manager = new WorkflowManager(maxTurns: 4, retryPolicy);

        var flakyAgent = new FlakyAgent("flaky", failuresBeforeSuccess: 2, () => AgentRunResultWithMessage("assistant", "Finally succeeded."));
        manager.RegisterAgent(flakyAgent);

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "Please answer after retries."),
        });

        var result = await manager.RunAgentAsync("flaky", request);

        Assert.Equal(3, flakyAgent.AttemptCount);
        Assert.NotNull(result.FinalMessage);
        Assert.Equal("Finally succeeded.", result.FinalMessage!.Content.OfType<AgentTextContent>().Single().Text);
        Assert.Equal(2, result.Conversation.Count); // user + assistant success
    }

    [Fact]
    public async Task RunAgentAsync_ReturnsErrorMessageAfterRetryExhaustion()
    {
        var retryPolicy = new AgentRetryPolicy(maxAttempts: 2, delayBetweenAttempts: TimeSpan.Zero);
        var manager = new WorkflowManager(maxTurns: 4, retryPolicy);

        var failingAgent = new FlakyAgent("failing", failuresBeforeSuccess: int.MaxValue, () => AgentRunResultWithMessage("assistant", "Should not reach here."));
        manager.RegisterAgent(failingAgent);

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "Trigger failure."),
        });

        var result = await manager.RunAgentAsync("failing", request);

        Assert.Equal(2, failingAgent.AttemptCount);
        Assert.NotNull(result.FinalMessage);

        var textContent = result.FinalMessage!.Content.OfType<AgentTextContent>().Single().Text;
        Assert.Contains("Erreur", textContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failing", textContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Conversation.Count); // user + error assistant
    }

    [Fact]
    public async Task Workflow_SupportsNestedAgentDelegation()
    {
        var manager = new WorkflowManager(maxTurns: 6);

        var tabAgent = new ScriptedAgent(
            "tab_analyst",
            new[]
            {
                AgentRunResultWithMessage("assistant", "L'onglet Budget montre un total de 2 500 €."),
            });

        var fileAgent = new ToolAwareScriptedAgent(
            "file_analyst",
            new[]
            {
                AgentRunResultWithTool("assistant", "Consultons l'onglet Budget.", "tab_lookup", "tab-call-1", """{"sheet":"Budget"}"""),
                AgentRunResultWithMessage("assistant", "Synthèse: Budget total 2 500 €."),
            },
            new[] { "tab_lookup" });

        var analystAgent = new ToolAwareScriptedAgent(
            "analyst",
            new[]
            {
                AgentRunResultWithTool("assistant", "Je vais consulter le fichier Finances Q1.", "file_lookup", "file-call-1", """{"file":"Finances_Q1.xlsx"}"""),
                AgentRunResultWithMessage("assistant", "Analyse terminée."),
            },
            new[] { "file_lookup" });

        manager.RegisterAgent(tabAgent);
        manager.RegisterAgent(fileAgent);
        manager.RegisterAgent(analystAgent);

        manager.RegisterTool(new AgentDelegatingTool("file_lookup", "file_analyst", """{"type":"object","properties":{"file":{"type":"string"}}}""", "file"));
        manager.RegisterTool(new AgentDelegatingTool("tab_lookup", "tab_analyst", """{"type":"object","properties":{"sheet":{"type":"string"}}}""", "sheet"));

        var result = await manager.RunAgentAsync("analyst", new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "Analyse le budget trimestriel."),
        }));

        Assert.NotNull(result.FinalMessage);
        Assert.Contains(result.Conversation, message => message.Role == "tool" && message.Content.OfType<AgentToolResultContent>().Any(content => content.Output.Contains("Budget")));
        Assert.Equal("Analyse terminée.", result.FinalMessage!.Content.OfType<AgentTextContent>().Single().Text);
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

    private sealed class FlakyAgent : IAgent
    {
        private readonly Func<AgentRunResult> _successFactory;
        private readonly int _failuresBeforeSuccess;

        public FlakyAgent(string name, int failuresBeforeSuccess, Func<AgentRunResult> successFactory)
        {
            Descriptor = new AgentDescriptor(name, $"{name} description", "gpt-5-nano");
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _successFactory = successFactory;
        }

        public int AttemptCount { get; private set; }

        public AgentDescriptor Descriptor { get; }

        public Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (AttemptCount <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"Attempt {AttemptCount} failed.");
            }

            return Task.FromResult(_successFactory());
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

    private sealed class ToolAwareScriptedAgent : IAgent, IToolAwareAgent
    {
        private readonly Queue<AgentRunResult> _responses;

        public ToolAwareScriptedAgent(string name, IEnumerable<AgentRunResult> responses, IEnumerable<string> toolNames)
        {
            Descriptor = new AgentDescriptor(name, $"{name} description", "gpt-5-nano");
            _responses = new Queue<AgentRunResult>(responses);
            ToolNames = toolNames.ToList();
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

    private sealed class AgentDelegatingTool : IAgentTool
    {
        private readonly string _targetAgent;
        private readonly string _argumentKey;

        public AgentDelegatingTool(string name, string targetAgent, string jsonSchema, string argumentKey)
        {
            Name = name;
            _targetAgent = targetAgent;
            _argumentKey = argumentKey;
            Definition = new ToolDefinition(name, $"Delegates to {_targetAgent}", JsonNode.Parse(jsonSchema)!);
        }

        public string Name { get; }

        public ToolDefinition Definition { get; }

        public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
        {
            var request = new AgentRequest(new[]
            {
                AgentMessage.FromText("user", BuildPrompt(context.ToolCall.Arguments)),
            });

            var result = await context.CallAgentAsync(_targetAgent, request, cancellationToken).ConfigureAwait(false);
            var text = result.FinalMessage is null
                ? "Aucune réponse."
                : string.Join(Environment.NewLine, result.FinalMessage.Content.OfType<AgentTextContent>().Select(c => c.Text));

            return new AgentToolExecutionResult(context.ToolCall.CallId, text);
        }

        private string BuildPrompt(JsonDocument arguments)
        {
            if (arguments.RootElement.TryGetProperty(_argumentKey, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return $"Merci d'analyser {_argumentKey} '{value.GetString()}'.";
            }

            return "Analyse requise.";
        }
    }
}

