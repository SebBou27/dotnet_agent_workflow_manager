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

        var request = new OpenAiResponseRequest
        {
            Model = Descriptor.Model,
            Instructions = Descriptor.FunctionDescription,
            Input = BuildInput(conversation),
            Temperature = Descriptor.Temperature,
            TopP = Descriptor.TopP,
            MaxOutputTokens = Descriptor.MaxOutputTokens,
            Tools = openAiTools,
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

    private AgentRunResult TranslateResponse(OpenAiResponseEnvelope response)
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

    private IReadOnlyList<AgentToolCall> ExtractToolCalls(IEnumerable<OpenAiOutputContent> contents)
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
            toolCalls.Add(new AgentToolCall(originalName, content.CallId, argumentsDocument));
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
