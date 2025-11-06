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
    private readonly AgentRetryPolicy _retryPolicy;

    public AgentWorkflowManager(int maxTurns = 8, AgentRetryPolicy? retryPolicy = null)
    {
        if (maxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Max turns must be greater than zero.");
        }

        _maxTurns = maxTurns;
        _retryPolicy = retryPolicy ?? AgentRetryPolicy.Default;
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

            var runResult = await ExecuteWithRetryAsync(agent, conversation, availableTools, cancellationToken).ConfigureAwait(false);

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

    private async Task<AgentRunResult> ExecuteWithRetryAsync(IAgent agent, IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (agent is IRetryAwareAgent retryAwareAgent)
                {
                    retryAwareAgent.OnRetryAttempt(attempt);
                }

                if (attempt > 1 && _retryPolicy.DelayBetweenAttempts > TimeSpan.Zero)
                {
                    await Task.Delay(_retryPolicy.DelayBetweenAttempts, cancellationToken).ConfigureAwait(false);
                }

                return await agent.GenerateAsync(conversation, tools, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_retryPolicy.ShouldRetryOn(ex))
            {
                lastException = ex;
            }
        }

        if (lastException is null)
        {
            throw new InvalidOperationException("Agent execution failed without an exception.");
        }

        var errorMessage = new AgentMessage(
            "assistant",
            new AgentContent[]
            {
                new AgentTextContent($"Error: agent '{agent.Descriptor.Name}' failed after {_retryPolicy.MaxAttempts} attempts. Details: {lastException.Message}"),
            });

        return new AgentRunResult(errorMessage, Array.Empty<AgentToolCall>());
    }

    private static async Task<AgentToolExecutionResult> InvokeToolSafeAsync(IAgentTool tool, ToolInvocationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var argumentsJson = context.ToolCall.Arguments.RootElement.GetRawText();
            Console.WriteLine($"[Tool] Invoking '{tool.Name}' (callId={context.ToolCall.CallId}) with arguments: {argumentsJson}");

            var result = await tool.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Tool] '{tool.Name}' (callId={context.ToolCall.CallId}) returned (error={result.IsError}): {result.Output}");
            return result;
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException or ArgumentException
                ? ex.Message
                : $"Tool '{tool.Name}' failed: {ex.Message}";

            Console.Error.WriteLine($"[Tool] '{tool.Name}' (callId={context.ToolCall.CallId}) threw: {ex}");
            return new AgentToolExecutionResult(context.ToolCall.CallId, message, isError: true);
        }
    }
}

public interface IToolAwareAgent
{
    IReadOnlyCollection<string> ToolNames { get; }
}

public interface IRetryAwareAgent
{
    void OnRetryAttempt(int attemptNumber);
}
