using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class RuleBasedAgentRouterTests
{
    [Theory]
    [InlineData("/plan Draft architecture", "planner")]
    [InlineData("What is the roadmap for week 2?", "planner")]
    [InlineData("/exec run tests", "executor")]
    [InlineData("Please implement this endpoint", "executor")]
    [InlineData("Peux-tu corrige ce bug ?", "executor")]
    public void ResolveAgent_UsesExpectedRoute(string input, string expected)
    {
        var router = new RuleBasedAgentRouter();

        var target = router.ResolveAgent(input);

        Assert.Equal(expected, target);
    }
}
