using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

public interface IAgentTool
{
    string Name { get; }

    ToolDefinition Definition { get; }

    Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken);
}

public sealed class ToolDefinition
{
    public ToolDefinition(string name, string description, JsonNode parametersSchema)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(name));
        }

        ParametersSchema = parametersSchema ?? throw new ArgumentNullException(nameof(parametersSchema));

        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonNode ParametersSchema { get; }

    internal OpenAiToolDefinition ToOpenAiDefinition()
        => new()
        {
            Function = new OpenAiFunctionDefinition
            {
                Name = Name,
                Description = Description,
                Parameters = ParametersSchema,
            },
        };
}

public sealed class ToolInvocationContext
{
    public ToolInvocationContext(AgentToolCall toolCall, Func<string, AgentRequest, CancellationToken, Task<AgentWorkflowResult>> agentCaller)
    {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        AgentCaller = agentCaller ?? throw new ArgumentNullException(nameof(agentCaller));
    }

    public AgentToolCall ToolCall { get; }

    private Func<string, AgentRequest, CancellationToken, Task<AgentWorkflowResult>> AgentCaller { get; }

    public Task<AgentWorkflowResult> CallAgentAsync(string agentName, AgentRequest request, CancellationToken cancellationToken)
        => AgentCaller(agentName, request, cancellationToken);
}
