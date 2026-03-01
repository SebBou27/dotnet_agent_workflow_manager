using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class SqlServerQueryToolTests
{
    [Fact]
    public async Task InvokeAsync_TextMode_RejectsNonReadOnlySql_BeforeConnectionUse()
    {
        var tool = new SqlServerQueryTool("Server=localhost;Database=master;User Id=sa;Password=Fake123!;TrustServerCertificate=True;");
        using var args = JsonDocument.Parse("""{"commandType":"text","sql":"DELETE FROM Users"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(Ctx("c1", "db.sqlserver.query", args), CancellationToken.None));
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_StoredProcedureMode_UsesAllowlist()
    {
        var tool = new SqlServerQueryTool(
            "Server=localhost;Database=master;User Id=sa;Password=Fake123!;TrustServerCertificate=True;",
            storedProcedureAllowlist: new[] { "dbo.ReadDashboard" });

        using var args = JsonDocument.Parse("""{"commandType":"storedProcedure","procedure":"dbo.DeleteAll"}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(Ctx("c1", "db.sqlserver.query", args), CancellationToken.None));
        Assert.Contains("allowlist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_InvalidCommandType_Throws()
    {
        var tool = new SqlServerQueryTool("Server=localhost;Database=master;User Id=sa;Password=Fake123!;TrustServerCertificate=True;");
        using var args = JsonDocument.Parse("""{"commandType":"raw","sql":"SELECT 1"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(Ctx("c1", "db.sqlserver.query", args), CancellationToken.None));
    }

    private static ToolInvocationContext Ctx(string callId, string toolName, JsonDocument args)
    {
        var call = new AgentToolCall(toolName, callId, args);
        return new ToolInvocationContext(call, (_, _, _) => Task.FromResult(new AgentWorkflowResult(null, Array.Empty<AgentMessage>())));
    }
}
