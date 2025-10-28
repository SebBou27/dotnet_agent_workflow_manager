using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentWorkflowManager.Core;

internal sealed class OpenAiResponseRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("input")]
    public required List<OpenAiInputMessage> Input { get; init; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("tools")]
    public List<OpenAiToolDefinition>? Tools { get; init; }

    [JsonPropertyName("reasoning")]
    public OpenAiReasoningOptions? Reasoning { get; init; }

    [JsonPropertyName("text")]
    public OpenAiTextOptions? Text { get; init; }
}

internal sealed class OpenAiReasoningOptions
{
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }
}

internal sealed class OpenAiTextOptions
{
    [JsonPropertyName("verbosity")]
    public string? Verbosity { get; init; }
}

internal sealed class OpenAiInputMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required List<OpenAiInputContent> Content { get; init; }
}

internal sealed class OpenAiInputContent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("output")]
    public string? Output { get; init; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }
}

internal sealed class OpenAiToolDefinition
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("input_schema")]
    public JsonNode? InputSchema { get; init; }

    [JsonPropertyName("function")]
    public OpenAiFunctionDefinition? Function { get; init; }
}

internal sealed class OpenAiFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; init; }
}

internal sealed class OpenAiResponseEnvelope
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("output")]
    public List<OpenAiOutputMessage> Output { get; init; } = new();
}

internal sealed class OpenAiOutputMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public List<OpenAiOutputContent> Content { get; init; } = new();
}

internal sealed class OpenAiOutputContent
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}
