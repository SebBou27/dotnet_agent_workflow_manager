using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Agent implementation backed by the OpenAI Responses API.
/// </summary>
public sealed class OpenAiAgent : IAgent, IToolAwareAgent, IRetryAwareAgent
{
    private readonly IOpenAiResponseClient _client;
    private readonly HashSet<string> _toolNames;
    private static readonly JsonSerializerOptions RequestLoggingOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private int _attemptNumber = 1;

    public OpenAiAgent(OpenAiResponseClient client, AgentDescriptor descriptor, IEnumerable<string>? toolNames = null)
        : this((IOpenAiResponseClient)client, descriptor, toolNames)
    {
    }

    internal OpenAiAgent(IOpenAiResponseClient client, AgentDescriptor descriptor, IEnumerable<string>? toolNames = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _toolNames = toolNames is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
    }

    public AgentDescriptor Descriptor { get; }

    public IReadOnlyCollection<string> ToolNames => _toolNames;

    public Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        if (conversation is null)
        {
            throw new ArgumentNullException(nameof(conversation));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        return GenerateInternalAsync(conversation, tools, cancellationToken);
    }

    public void OnRetryAttempt(int attemptNumber)
    {
        _attemptNumber = attemptNumber;
    }

    private async Task<AgentRunResult> GenerateInternalAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        var request = new OpenAiResponseRequest
        {
            Model = Descriptor.Model,
            Instructions = Descriptor.FunctionDescription,
            Input = BuildInput(conversation),
            Temperature = Descriptor.Temperature,
            TopP = Descriptor.TopP,
            MaxOutputTokens = Descriptor.MaxOutputTokens,
            Tools = tools.Count > 0 ? tools.Select(t => t.ToOpenAiDefinition()).ToList() : null,
            Reasoning = Descriptor.ReasoningEffort is null ? null : new OpenAiReasoningOptions { Effort = Descriptor.ReasoningEffort },
            Text = Descriptor.Verbosity is null ? null : new OpenAiTextOptions { Verbosity = Descriptor.Verbosity },
        };

        if (!string.IsNullOrWhiteSpace(Descriptor.SystemPrompt))
        {
            request.Input.Insert(0, new OpenAiInputMessage
            {
                Role = "system",
                Content = new List<OpenAiInputContent>
                {
                    new() { Type = "input_text", Text = Descriptor.SystemPrompt },
                },
            });
        }

        if (_attemptNumber <= 1)
        {
            var serializedRequest = JsonSerializer.Serialize(request, RequestLoggingOptions);
            Console.WriteLine("[OpenAI] Sending request payload (attempt #1):");
            Console.WriteLine(serializedRequest);
        }
        else
        {
            Console.WriteLine($"[OpenAI] Retry attempt #{_attemptNumber}: reusing request payload from attempt #1.");
        }

        var response = await _client.CreateResponseAsync(request, cancellationToken).ConfigureAwait(false);
        return TranslateResponse(response);
    }

    private static List<OpenAiInputMessage> BuildInput(IReadOnlyList<AgentMessage> conversation)
    {
        var inputMessages = new List<OpenAiInputMessage>(conversation.Count);

        foreach (var message in conversation)
        {
            var content = message.Content.Select(ConvertContent).ToList();
            inputMessages.Add(new OpenAiInputMessage { Role = message.Role, Content = content });
        }

        return inputMessages;
    }

    private static OpenAiInputContent ConvertContent(AgentContent content)
        => content switch
        {
            AgentTextContent text => new OpenAiInputContent
            {
                Type = "input_text",
                Text = text.Text,
            },
            AgentToolResultContent toolResult => new OpenAiInputContent
            {
                Type = "tool_result",
                ToolCallId = toolResult.ToolCallId,
                Output = toolResult.Output,
                IsError = toolResult.IsError,
            },
            _ => throw new NotSupportedException($"Content type '{content.GetType().Name}' is not supported."),
        };

    private static AgentRunResult TranslateResponse(OpenAiResponseEnvelope response)
    {
        if (response.Output.Count == 0)
        {
            return new AgentRunResult(null, Array.Empty<AgentToolCall>());
        }

        var primaryMessage = response.Output.FirstOrDefault(output => string.Equals(output.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            ?? response.Output[0];

        var assistantMessage = ConvertOutputToAgentMessage(primaryMessage);
        var toolCalls = ExtractToolCalls(primaryMessage.Content);

        return new AgentRunResult(assistantMessage, toolCalls);
    }

    private static AgentMessage? ConvertOutputToAgentMessage(OpenAiOutputMessage message)
    {
        var textSegments = message.Content
            .Where(content => string.Equals(content.Type, "output_text", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (textSegments.Count == 0)
        {
            return null;
        }

        var combined = string.Join(Environment.NewLine, textSegments);
        return AgentMessage.FromText(message.Role ?? "assistant", combined);
    }

    private static IReadOnlyList<AgentToolCall> ExtractToolCalls(IEnumerable<OpenAiOutputContent> contents)
    {
        var toolCalls = new List<AgentToolCall>();

        foreach (var content in contents)
        {
            if (!string.Equals(content.Type, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content.Name) || string.IsNullOrWhiteSpace(content.CallId))
            {
                continue;
            }

            var argumentsDocument = ParseArguments(content.Arguments);
            toolCalls.Add(new AgentToolCall(content.Name, content.CallId, argumentsDocument));
        }

        return toolCalls;
    }

    private static JsonDocument ParseArguments(JsonElement? element)
    {
        if (element is null)
        {
            return JsonDocument.Parse("{}");
        }

        if (element.Value.ValueKind == JsonValueKind.String)
        {
            var payload = element.Value.GetString() ?? "{}";
            return JsonDocument.Parse(payload);
        }

        return JsonDocument.Parse(element.Value.GetRawText());
    }
}
