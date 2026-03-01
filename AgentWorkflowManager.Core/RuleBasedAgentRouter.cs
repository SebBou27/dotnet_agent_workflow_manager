using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentWorkflowManager.Core;

public sealed class AgentRoutingOptions
{
    public string PlannerAgentName { get; init; } = "planner";
    public string ExecutorAgentName { get; init; } = "executor";

    /// <summary>
    /// If user message contains one of these keywords, route to executor.
    /// </summary>
    public IReadOnlyCollection<string> ExecutorKeywords { get; init; } = new[]
    {
        "code",
        "implement",
        "fix",
        "bug",
        "test",
        "run",
        "build",
        "refactor",
        "écris",
        "corrige",
        "implémente",
        "teste",
        "commit"
    };
}

public sealed class RuleBasedAgentRouter
{
    private readonly AgentRoutingOptions _options;

    public RuleBasedAgentRouter(AgentRoutingOptions? options = null)
    {
        _options = options ?? new AgentRoutingOptions();
    }

    public string ResolveAgent(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return _options.PlannerAgentName;
        }

        var input = userInput.Trim();

        if (input.StartsWith("/exec", StringComparison.OrdinalIgnoreCase))
        {
            return _options.ExecutorAgentName;
        }

        if (input.StartsWith("/plan", StringComparison.OrdinalIgnoreCase))
        {
            return _options.PlannerAgentName;
        }

        return ContainsExecutorKeyword(input)
            ? _options.ExecutorAgentName
            : _options.PlannerAgentName;
    }

    private bool ContainsExecutorKeyword(string input)
    {
        return _options.ExecutorKeywords.Any(keyword =>
            input.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
