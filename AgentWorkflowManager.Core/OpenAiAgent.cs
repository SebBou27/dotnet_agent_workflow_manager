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
    private readonly Dictionary<string, string> _toolNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingResponseIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _submittedToolOutputs = new(StringComparer.OrdinalIgnoreCase);
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
        _toolNameMap.Clear();
        var openAiTools = tools.Count > 0 ? SanitizeTools(tools) : null;

        var (inputItems, toolCallIdsIncluded) = BuildInput(conversation);
        string? previousResponseId = null;

        if (toolCallIdsIncluded.Count > 0)
        {
            var responseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var callId in toolCallIdsIncluded)
            {
                if (!_pendingResponseIds.TryGetValue(callId, out var responseId) || string.IsNullOrWhiteSpace(responseId))
                {
                    throw new InvalidOperationException($"No OpenAI response id recorded for tool call '{callId}'.");
                }

                responseIds.Add(responseId);
            }

            if (responseIds.Count != 1)
            {
                throw new InvalidOperationException("Tool outputs reference multiple response ids, which is not supported.");
            }

            previousResponseId = responseIds.First();
        }

        if (!string.IsNullOrWhiteSpace(Descriptor.SystemPrompt))
        {
            inputItems.Insert(0, new OpenAiMessageInputItem
            {
                Role = "system",
                Content = new List<OpenAiInputContent>
                {
                    new() { Type = "input_text", Text = Descriptor.SystemPrompt },
                },
            });
        }

        if (inputItems.Count == 0)
        {
            throw new InvalidOperationException("Cannot create OpenAI response without input items.");
        }

        var request = new OpenAiResponseRequest
        {
            Model = Descriptor.Model,
            Instructions = Descriptor.FunctionDescription,
            Input = inputItems,
            PreviousResponseId = previousResponseId,
            Temperature = Descriptor.Temperature,
            TopP = Descriptor.TopP,
            MaxOutputTokens = Descriptor.MaxOutputTokens,
            Tools = openAiTools,
            Reasoning = Descriptor.ReasoningEffort is null ? null : new OpenAiReasoningOptions { Effort = Descriptor.ReasoningEffort },
            Text = Descriptor.Verbosity is null ? null : new OpenAiTextOptions { Verbosity = Descriptor.Verbosity },
        };

        if (_attemptNumber <= 1)
        {
            var serializedRequest = JsonSerializer.Serialize(request, RequestLoggingOptions);
            Console.WriteLine("[OpenAI] Sending request payload (attempt #1):");
            Console.WriteLine(serializedRequest);

            var renamedTools = _toolNameMap.Where(kvp => !string.Equals(kvp.Key, kvp.Value, StringComparison.Ordinal)).ToList();
            if (renamedTools.Count > 0)
            {
                Console.WriteLine("[OpenAI] Tool name mapping (sanitized -> original):");
                foreach (var mapping in renamedTools)
                {
                    Console.WriteLine($" - {mapping.Key} => {mapping.Value}");
                }
            }
        }
        else
        {
            Console.WriteLine($"[OpenAI] Retry attempt #{_attemptNumber}: reusing request payload from attempt #1.");
        }

        var response = await _client.CreateResponseAsync(request, cancellationToken).ConfigureAwait(false);
        var serializedResponse = JsonSerializer.Serialize(response, RequestLoggingOptions);
        Console.WriteLine("[OpenAI] Received response envelope:");
        Console.WriteLine(serializedResponse);
        foreach (var callId in toolCallIdsIncluded)
        {
            _submittedToolOutputs.Add(callId);
            _pendingResponseIds.Remove(callId);
        }

        return TranslateResponse(response);
    }

    private List<OpenAiToolDefinition> SanitizeTools(IReadOnlyList<ToolDefinition> tools)
    {
        var sanitizedDefinitions = new List<OpenAiToolDefinition>(tools.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
        {
            var sanitizedName = SanitizeToolName(tool.Name);
            sanitizedName = EnsureUniqueName(sanitizedName, usedNames);

            _toolNameMap[sanitizedName] = tool.Name;
            sanitizedDefinitions.Add(tool.ToOpenAiDefinition(sanitizedName));
        }

        return sanitizedDefinitions;
    }

    private static string SanitizeToolName(string name)
    {
        var buffer = new char[name.Length];
        var index = 0;

        foreach (var ch in name)
        {
            buffer[index++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_';
        }

        var sanitized = new string(buffer, 0, index);
        return string.IsNullOrWhiteSpace(sanitized.Replace("_", string.Empty)) ? "tool" : sanitized;
    }

    private static string EnsureUniqueName(string baseName, HashSet<string> usedNames)
    {
        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        } while (!usedNames.Add(candidate));

        return candidate;
    }

    private (List<OpenAiInputItem> InputItems, List<string> ToolCallIds) BuildInput(IReadOnlyList<AgentMessage> conversation)
    {
        var inputItems = new List<OpenAiInputItem>(conversation.Count);
        var toolCallIds = new List<string>();

        void AppendToolOutputs(IEnumerable<AgentToolResultContent> toolResults)
        {
            foreach (var result in toolResults)
            {
                if (_submittedToolOutputs.Contains(result.ToolCallId))
                {
                    continue;
                }

                inputItems.Add(new OpenAiFunctionCallOutputItem
                {
                    CallId = result.ToolCallId,
                    Output = result.Output,
                });
                toolCallIds.Add(result.ToolCallId);
            }
        }

        foreach (var message in conversation)
        {
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                AppendToolOutputs(message.Content.OfType<AgentToolResultContent>());
                continue;
            }

            var contentItems = new List<OpenAiInputContent>();
            foreach (var content in message.Content)
            {
                if (content is AgentToolResultContent toolResultContent)
                {
                    AppendToolOutputs(new[] { toolResultContent });
                    continue;
                }

                contentItems.Add(ConvertContent(content));
            }

            if (contentItems.Count == 0)
            {
                continue;
            }

            inputItems.Add(new OpenAiMessageInputItem { Role = NormalizeRole(message.Role), Content = contentItems });
        }

        return (inputItems, toolCallIds);
    }

    private static OpenAiInputContent ConvertContent(AgentContent content)
        => content switch
        {
            AgentTextContent text => new OpenAiInputContent
            {
                Type = "input_text",
                Text = text.Text,
            },
            AgentToolResultContent => throw new InvalidOperationException("Tool result content must be provided via function_call_output items."),
            _ => throw new NotSupportedException($"Content type '{content.GetType().Name}' is not supported."),
        };

    private AgentRunResult TranslateResponse(OpenAiResponseEnvelope response)
    {
        if (response.Output.Count == 0)
        {
            return new AgentRunResult(null, Array.Empty<AgentToolCall>());
        }

        AgentMessage? assistantMessage = null;
        var toolCalls = new List<AgentToolCall>();

        foreach (var output in response.Output)
        {
            var type = output.Type ?? string.Empty;

            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
            {
                assistantMessage ??= ConvertOutputToAgentMessage(output);
                var messageToolCalls = ExtractToolCalls(output.Content, response.Id);
                if (messageToolCalls.Count > 0)
                {
                    toolCalls.AddRange(messageToolCalls);
                }
            }
            else if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                var toolCall = ConvertFunctionCall(output, response.Id);
                if (toolCall is not null)
                {
                    toolCalls.Add(toolCall);
                }
            }
        }

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

    private AgentToolCall? ConvertFunctionCall(OpenAiOutputMessage output, string? responseId)
    {
        if (string.IsNullOrWhiteSpace(output.Name))
        {
            return null;
        }

        var callId = !string.IsNullOrWhiteSpace(output.CallId)
            ? output.CallId
            : !string.IsNullOrWhiteSpace(output.Id)
                ? output.Id!
                : Guid.NewGuid().ToString("N");

        var originalName = _toolNameMap.TryGetValue(output.Name, out var mappedName)
            ? mappedName
            : output.Name;

        var argumentsDocument = ParseArguments(output.Arguments);
        var toolCall = new AgentToolCall(originalName, callId, argumentsDocument);

        if (!string.IsNullOrWhiteSpace(responseId))
        {
            _pendingResponseIds[callId] = responseId;
        }

        return toolCall;
    }

    private IReadOnlyList<AgentToolCall> ExtractToolCalls(IEnumerable<OpenAiOutputContent> contents, string? responseId)
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

            var originalName = _toolNameMap.TryGetValue(content.Name, out var mappedName)
                ? mappedName
                : content.Name;

            var argumentsDocument = ParseArguments(content.Arguments);
            var toolCall = new AgentToolCall(originalName, content.CallId, argumentsDocument);
            if (!string.IsNullOrWhiteSpace(responseId))
            {
                _pendingResponseIds[toolCall.CallId] = responseId;
            }

            toolCalls.Add(toolCall);
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

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "user";
        }

        return role.ToLowerInvariant() switch
        {
            "tool" => "assistant",
            "function" => "assistant",
            "assistant" => "assistant",
            "system" => "system",
            "developer" => "developer",
            "user" => "user",
            _ => "user",
        };
    }
}
