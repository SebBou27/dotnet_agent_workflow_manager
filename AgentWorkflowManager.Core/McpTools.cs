using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

public sealed class McpToolDescriptor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("server")]
    public string? Server { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("process")]
    public string? ProcessPath { get; init; }

    [JsonPropertyName("args")]
    public List<string>? Arguments { get; init; }

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; init; }
}

public sealed class McpToolConfiguration
{
    [JsonPropertyName("tools")]
    public List<McpToolDescriptor> Tools { get; init; } = new();
}

public static class McpToolConfigLoader
{
    public static IReadOnlyList<McpToolDescriptor> LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MCP tool configuration file not found.", path);
        }

        using var stream = File.OpenRead(path);
        var configuration = JsonSerializer.Deserialize<McpToolConfiguration>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new McpToolConfiguration();

        foreach (var descriptor in configuration.Tools)
        {
            ValidateDescriptor(descriptor);
        }

        return configuration.Tools;
    }

    public static bool TryLoadFromFile(string path, out IReadOnlyList<McpToolDescriptor> descriptors)
    {
        if (!File.Exists(path))
        {
            descriptors = Array.Empty<McpToolDescriptor>();
            return false;
        }

        descriptors = LoadFromFile(path);
        return descriptors.Count > 0;
    }

    private static void ValidateDescriptor(McpToolDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            throw new InvalidOperationException("MCP tool descriptor is missing a name.");
        }

        if (descriptor.Parameters is null)
        {
            throw new InvalidOperationException($"MCP tool '{descriptor.Name}' is missing a parameters schema.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Endpoint) && string.IsNullOrWhiteSpace(descriptor.ProcessPath))
        {
            throw new InvalidOperationException($"MCP tool '{descriptor.Name}' must specify either an endpoint or a process definition.");
        }
    }
}

public interface IMcpToolClient
{
    Task<string> InvokeAsync(McpToolDescriptor descriptor, JsonDocument arguments, CancellationToken cancellationToken);
}

public sealed class McpAgentTool : IAgentTool
{
    private readonly McpToolDescriptor _descriptor;
    private readonly IMcpToolClient _client;

    public McpAgentTool(McpToolDescriptor descriptor, IMcpToolClient client)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _client = client ?? throw new ArgumentNullException(nameof(client));

        Definition = new ToolDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.Parameters ?? JsonNode.Parse("""{"type":"object"}""")!);
    }

    public string Name => _descriptor.Name;

    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var output = await _client.InvokeAsync(_descriptor, context.ToolCall.Arguments, cancellationToken).ConfigureAwait(false);
            return new AgentToolExecutionResult(context.ToolCall.CallId, output);
        }
        catch (Exception ex)
        {
            var message = $"MCP tool '{Name}' failed: {ex.Message}";
            return new AgentToolExecutionResult(context.ToolCall.CallId, message, isError: true);
        }
    }
}
