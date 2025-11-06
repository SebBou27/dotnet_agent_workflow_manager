using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;
using WorkflowManager = global::AgentWorkflowManager.Core.AgentWorkflowManager;

namespace AgentWorkflowManager.Tests;

public sealed class OpenAiAgentTests
{
    [Fact]
    public async Task OpenAiAgent_UsesDescriptorModelAndInstructionsAsync()
    {
        var fakeClient = new FakeResponseClient(CreateTextResponse("ok"));
        var descriptor = new AgentDescriptor("nano-agent", "Provide concise answers.", "gpt-5-nano");
        var agent = new OpenAiAgent(fakeClient, descriptor);
        var conversation = new List<AgentMessage>
        {
            AgentMessage.FromText("user", "Say hi"),
        };

        _ = await agent.GenerateAsync(conversation, new List<ToolDefinition>(), CancellationToken.None);

        Assert.Single(fakeClient.Requests);
        var request = fakeClient.Requests[0];

        Assert.Equal("gpt-5-nano", request.Model);
        Assert.Equal(descriptor.FunctionDescription, request.Instructions);
        Assert.Equal("minimal", request.Reasoning?.Effort);
        Assert.Equal("low", request.Text?.Verbosity);
        var messageItem = Assert.IsType<OpenAiMessageInputItem>(request.Input.Last());
        Assert.Equal("input_text", messageItem.Content.Single().Type);
        Assert.Equal("Say hi", messageItem.Content.Single().Text);
    }

    [Fact]
    public async Task Workflow_AllowsToolToDelegateToSecondaryAgentAsync()
    {
        var helperClient = new FakeResponseClient(CreateTextResponse("Helper result."));
        var helperDescriptor = new AgentDescriptor("helper", "Assist with delegated requests.", "gpt-5-nano");
        var helperAgent = new OpenAiAgent(helperClient, helperDescriptor);

        var toolCallEnvelope = CreateToolCallResponse("delegate", "call_1", """{"prompt":"Need assistance"}""");
        var primaryResponseEnvelope = CreateTextResponse("Primary completed using helper output.");

        var primaryClient = new FakeResponseClient(toolCallEnvelope, primaryResponseEnvelope);
        var primaryDescriptor = new AgentDescriptor("primary", "Coordinate responses with tools.", "gpt-5-nano");
        var primaryAgent = new OpenAiAgent(primaryClient, primaryDescriptor, new[] { "delegate" });

        var manager = new WorkflowManager();
        manager.RegisterAgent(helperAgent);
        manager.RegisterAgent(primaryAgent);

        var proxyTool = new AgentProxyTool("delegate", "Delegate work to helper.", "helper");
        manager.RegisterTool(proxyTool);

        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", "Please handle this request."),
        });

        var result = await manager.RunAgentAsync("primary", request, CancellationToken.None);

        Assert.NotNull(result.FinalMessage);
        var finalText = string.Join("\n", result.FinalMessage!.Content.OfType<AgentTextContent>().Select(c => c.Text));
        Assert.Contains("Primary completed", finalText);

        Assert.Equal(2, primaryClient.Requests.Count);
        Assert.Single(helperClient.Requests);
    }

    private static OpenAiResponseEnvelope CreateTextResponse(string text)
        => new()
        {
            Id = "resp_text",
            Output =
            {
                new OpenAiOutputMessage
                {
                    Type = "message",
                    Role = "assistant",
                    Content =
                    {
                        new OpenAiOutputContent { Type = "output_text", Text = text },
                    },
                },
            },
        };

    private static OpenAiResponseEnvelope CreateToolCallResponse(string toolName, string callId, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var cloned = document.RootElement.Clone();

        return new OpenAiResponseEnvelope
        {
            Id = $"resp_tool_{callId}",
            Output =
            {
                new OpenAiOutputMessage
                {
                    Type = "function_call",
                    Name = toolName,
                    CallId = callId,
                    Arguments = cloned,
                },
            },
        };
    }

    private sealed class FakeResponseClient : IOpenAiResponseClient
    {
        private readonly Queue<OpenAiResponseEnvelope> _responses;

        public FakeResponseClient(params OpenAiResponseEnvelope[] responses)
        {
            _responses = new Queue<OpenAiResponseEnvelope>(responses);
        }

        public List<OpenAiResponseRequest> Requests { get; } = new();
        public Task<OpenAiResponseEnvelope> CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException("No fake responses remaining.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
