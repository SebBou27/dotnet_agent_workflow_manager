using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

namespace AgentWorkflowManager.Tests;

public sealed class AgentRepoToolsFlowTests
{
    [Fact]
    public async Task AgentWorkflow_UsesAllRepoTools_InSingleRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "awm_agent_repo_flow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Demo\n");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "app.js"), "// TODO: improve\nconst x=1;\n");

        try
        {
            var manager = new WorkflowManager(maxTurns: 6);
            var agent = new SequentialToolAgent("executor", new[]
            {
                ToolCall("repo.list_tree", "c1", """{"path":"src","recursive":true,"maxEntries":20}"""),
                ToolCall("repo.search", "c2", """{"q":"TODO","path":".","maxResults":20}"""),
                ToolCall("repo.read_file", "c3", """{"file":"README.md","offset":1,"limit":10}"""),
                ToolCall("repo.write_file", "c4", """{"filePath":"docs/out.md","text":"ok","createDirs":true}"""),
                new AgentRunResult(AgentMessage.FromText("assistant", "DONE"), Array.Empty<AgentToolCall>()),
            });

            manager.RegisterAgent(agent);
            manager.RegisterTool(new RepoListTreeTool(root));
            manager.RegisterTool(new RepoSearchTool(root));
            manager.RegisterTool(new RepoReadFileTool(root));
            manager.RegisterTool(new RepoWriteFileTool(root));

            var result = await manager.RunAgentAsync("executor", new AgentRequest(new[] { AgentMessage.FromText("user", "go") }));

            var toolMessages = result.Conversation.Where(m => m.Role == "tool").ToList();
            Assert.Equal(4, toolMessages.Count);
            Assert.True(File.Exists(Path.Combine(root, "docs", "out.md")));
            var text = result.FinalMessage?.Content.OfType<AgentTextContent>().FirstOrDefault()?.Text;
            Assert.Equal("DONE", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static AgentRunResult ToolCall(string tool, string callId, string argsJson)
    {
        return new AgentRunResult(
            AgentMessage.FromText("assistant", $"call {tool}"),
            new[] { new AgentToolCall(tool, callId, JsonDocument.Parse(argsJson)) });
    }

    private sealed class SequentialToolAgent : IAgent, IToolAwareAgent
    {
        private readonly Queue<AgentRunResult> _steps;

        public SequentialToolAgent(string name, IEnumerable<AgentRunResult> steps)
        {
            Descriptor = new AgentDescriptor(name, "test", "test-model");
            _steps = new Queue<AgentRunResult>(steps);
            ToolNames = new[] { "repo.list_tree", "repo.search", "repo.read_file", "repo.write_file" };
        }

        public AgentDescriptor Descriptor { get; }

        public IReadOnlyCollection<string> ToolNames { get; }

        public Task<AgentRunResult> GenerateAsync(IReadOnlyList<AgentMessage> conversation, IReadOnlyList<ToolDefinition> availableTools, CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                return Task.FromResult(new AgentRunResult(AgentMessage.FromText("assistant", "DONE"), Array.Empty<AgentToolCall>()));
            }

            return Task.FromResult(_steps.Dequeue());
        }
    }
}
