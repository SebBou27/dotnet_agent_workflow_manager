using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Coordinates agent execution, tools, and inter-agent calls.
/// </summary>
public sealed class AgentWorkflowManager
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxTurns;

    public AgentWorkflowManager(int maxTurns = 8)
    {
        if (maxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Max turns must be greater than zero.");
        }

        _maxTurns = maxTurns;
    }

    public void RegisterAgent(IAgent agent)
    {
        if (agent is null)
        {
            throw new ArgumentNullException(nameof(agent));
        }

        _agents[agent.Descriptor.Name] = agent;
    }

    public void RegisterTool(IAgentTool tool)
    {
        if (tool is null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        _tools[tool.Name] = tool;
    }

    public Task<AgentWorkflowResult> RunAgentAsync(string agentName, AgentRequest request, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentName, out var agent))
        {
            throw new InvalidOperationException($"Agent '{agentName}' is not registered.");
        }

        return RunAgentInternalAsync(agent, request, cancellationToken);
    }

    private async Task<AgentWorkflowResult> RunAgentInternalAsync(IAgent agent, AgentRequest request, CancellationToken cancellationToken)
    {
        var conversation = request.Messages.ToList();
        var availableTools = ResolveToolsForAgent(agent);

        for (var turn = 0; turn < _maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runResult = await agent.GenerateAsync(conversation, availableTools, cancellationToken).ConfigureAwait(false);

            if (runResult.AssistantMessage is not null)
            {
                conversation.Add(runResult.AssistantMessage);
            }

            if (runResult.ToolCalls.Count == 0)
            {
                return new AgentWorkflowResult(runResult.AssistantMessage, conversation);
            }

            var toolInvocationTasks = runResult.ToolCalls.Select(toolCall =>
            {
                if (!_tools.TryGetValue(toolCall.Name, out var tool))
                {
                    throw new InvalidOperationException($"Tool '{toolCall.Name}' is not registered.");
                }

                var context = new ToolInvocationContext(toolCall, CallAgentAsync);
                return InvokeToolSafeAsync(tool, context, cancellationToken);
            }).ToList();

            var toolResults = await Task.WhenAll(toolInvocationTasks).ConfigureAwait(false);

            foreach (var toolResult in toolResults)
            {
                conversation.Add(toolResult.ToAgentMessage());
            }
        }

        throw new InvalidOperationException("Maximum number of agent turns reached.");
    }

    private IReadOnlyList<ToolDefinition> ResolveToolsForAgent(IAgent agent)
    {
        IReadOnlyCollection<string> toolNames = agent is IToolAwareAgent aware && aware.ToolNames.Count > 0
            ? aware.ToolNames
            : _tools.Keys.ToArray();

        return toolNames
            .Where(_tools.ContainsKey)
            .Select(name => _tools[name].Definition)
            .ToArray();
    }

    private Task<AgentWorkflowResult> CallAgentAsync(string agentName, AgentRequest request, CancellationToken cancellationToken)
        => RunAgentAsync(agentName, request, cancellationToken);

    private static async Task<AgentToolExecutionResult> InvokeToolSafeAsync(IAgentTool tool, ToolInvocationContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await tool.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException or ArgumentException
                ? ex.Message
                : $"Tool '{tool.Name}' failed: {ex.Message}";

            return new AgentToolExecutionResult(context.ToolCall.CallId, message, isError: true);
        }
    }
}

public interface IToolAwareAgent
{
    IReadOnlyCollection<string> ToolNames { get; }
}
