using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Tool that forwards work to another agent and returns its response.
/// </summary>
public sealed class AgentProxyTool : IAgentTool
{
    private static readonly JsonNode DefaultSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "prompt": {
          "type": "string",
          "description": "User prompt that will be forwarded to the delegated agent."
        }
      },
      "required": ["prompt"],
      "additionalProperties": false
    }
    """)!;

    private readonly string _targetAgentName;

    public AgentProxyTool(string name, string description, string targetAgentName, JsonNode? parameterSchema = null)
    {
        if (string.IsNullOrWhiteSpace(targetAgentName))
        {
            throw new ArgumentException("Target agent name cannot be null or whitespace.", nameof(targetAgentName));
        }

        _targetAgentName = targetAgentName;
        Definition = new ToolDefinition(name, description, parameterSchema ?? DefaultSchema);
    }

    public ToolDefinition Definition { get; }

    public string Name => Definition.Name;

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var prompt = ExtractPrompt(context.ToolCall.Arguments);
        var request = new AgentRequest(new[]
        {
            AgentMessage.FromText("user", prompt),
        });

        var result = await context.CallAgentAsync(_targetAgentName, request, cancellationToken).ConfigureAwait(false);
        var finalMessage = result.FinalMessage;
        var output = finalMessage is null
            ? "Agent completed without a final message."
            : string.Join(Environment.NewLine, finalMessage.Content.OfType<AgentTextContent>().Select(c => c.Text));

        return new AgentToolExecutionResult(context.ToolCall.CallId, output);
    }

    private static string ExtractPrompt(JsonDocument arguments)
    {
        if (!arguments.RootElement.TryGetProperty("prompt", out var promptElement) || promptElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The delegated agent tool requires a string 'prompt' argument.");
        }

        return promptElement.GetString() ?? string.Empty;
    }
}
