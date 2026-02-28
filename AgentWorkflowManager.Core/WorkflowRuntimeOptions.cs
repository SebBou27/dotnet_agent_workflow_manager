using System;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Runtime controls for workflow execution safety and resilience.
/// </summary>
public sealed record WorkflowRuntimeOptions
{
    public static WorkflowRuntimeOptions Default { get; } = new();

    /// <summary>
    /// Max time allowed for one agent generation call.
    /// </summary>
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Max time allowed for one tool invocation.
    /// </summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(45);
}
