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
    private readonly WorkflowRuntimeOptions _runtimeOptions;

    public AgentWorkflowManager(int maxTurns = 8, AgentRetryPolicy? retryPolicy = null, WorkflowRuntimeOptions? runtimeOptions = null)
    {
        if (maxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Max turns must be greater than zero.");
        }

        _maxTurns = maxTurns;
        _retryPolicy = retryPolicy ?? AgentRetryPolicy.Default;
        _runtimeOptions = runtimeOptions ?? WorkflowRuntimeOptions.Default;
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

            IReadOnlyCollection<string> declaredAllowedTools = agent is IToolAwareAgent aware && aware.ToolNames.Count > 0
                ? aware.ToolNames
                : _tools.Keys.ToArray();

            var allowedToolNames = new HashSet<string>(declaredAllowedTools, StringComparer.OrdinalIgnoreCase);

            var toolInvocationTasks = runResult.ToolCalls.Select(toolCall =>
            {
                if (!_tools.TryGetValue(toolCall.Name, out var tool))
                {
                    var missing = $"Tool '{toolCall.Name}' is not registered.";
                    WorkflowLog.Error($"[Tool] missing callId={toolCall.CallId}: {missing}");
                    return Task.FromResult(new AgentToolExecutionResult(toolCall.CallId, missing, isError: true));
                }

                if (!allowedToolNames.Contains(toolCall.Name))
                {
                    var denied = $"Tool '{toolCall.Name}' is not allowed for agent '{agent.Descriptor.Name}'.";
                    WorkflowLog.Error($"[Tool] denied callId={toolCall.CallId}: {denied}");
                    return Task.FromResult(new AgentToolExecutionResult(toolCall.CallId, denied, isError: true));
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

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_runtimeOptions.AgentTimeout);
                return await agent.GenerateAsync(conversation, tools, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Agent '{agent.Descriptor.Name}' timed out after {_runtimeOptions.AgentTimeout}.", ex);
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

    private async Task<AgentToolExecutionResult> InvokeToolSafeAsync(IAgentTool tool, ToolInvocationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var argumentsJson = context.ToolCall.Arguments.RootElement.GetRawText();
            WorkflowLog.Debug($"[Tool] Invoking '{tool.Name}' (callId={context.ToolCall.CallId}) with arguments: {WorkflowLog.SafePayload(argumentsJson)}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_runtimeOptions.ToolTimeout);
            var result = await tool.InvokeAsync(context, timeoutCts.Token).ConfigureAwait(false);
            WorkflowLog.Debug($"[Tool] '{tool.Name}' (callId={context.ToolCall.CallId}) returned (error={result.IsError}): {WorkflowLog.SafePayload(result.Output)}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutMessage = $"Tool '{tool.Name}' timed out after {_runtimeOptions.ToolTimeout}.";
            WorkflowLog.Error($"[Tool] '{tool.Name}' (callId={context.ToolCall.CallId}) timeout.");
            return new AgentToolExecutionResult(context.ToolCall.CallId, timeoutMessage, isError: true);
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException or ArgumentException
                ? ex.Message
                : $"Tool '{tool.Name}' failed: {ex.Message}";

            WorkflowLog.Error($"[Tool] '{tool.Name}' (callId={context.ToolCall.CallId}) threw: {ex.Message}");
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
