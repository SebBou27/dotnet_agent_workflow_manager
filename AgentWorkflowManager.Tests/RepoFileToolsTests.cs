using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class RepoFileToolsTests
{
    [Fact]
    public async Task ReadFile_ReturnsChunkedPayload()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "a.txt");
            await File.WriteAllTextAsync(filePath, "l1\nl2\nl3\nl4");

            var tool = new RepoReadFileTool(tempDir);
            using var args = JsonDocument.Parse("""{"path":"a.txt","offset":2,"limit":2}""");
            var result = await tool.InvokeAsync(CreateContext("c1", "repo.read_file", args), CancellationToken.None);

            using var payload = JsonDocument.Parse(result.Output);
            Assert.Equal("a.txt", payload.RootElement.GetProperty("path").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("offset").GetInt32());
            Assert.Equal(2, payload.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(4, payload.RootElement.GetProperty("totalLines").GetInt32());
            Assert.True(payload.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal("l2\nl3", payload.RootElement.GetProperty("content").GetString());
        }
        finally { SafeDelete(tempDir); }
    }

    [Fact]
    public async Task ReadFile_OutOfRange_ReturnsEmptyContent()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "a.txt");
            await File.WriteAllTextAsync(filePath, "l1\nl2");

            var tool = new RepoReadFileTool(tempDir);
            using var args = JsonDocument.Parse("""{"path":"a.txt","offset":50,"limit":10}""");
            var result = await tool.InvokeAsync(CreateContext("c1", "repo.read_file", args), CancellationToken.None);

            using var payload = JsonDocument.Parse(result.Output);
            Assert.Equal(string.Empty, payload.RootElement.GetProperty("content").GetString());
            Assert.False(payload.RootElement.GetProperty("truncated").GetBoolean());
        }
        finally { SafeDelete(tempDir); }
    }

    [Fact]
    public async Task WriteFile_WritesAndReturnsBytes()
    {
        var tempDir = CreateTempDir();
        try
        {
            var tool = new RepoWriteFileTool(tempDir);
            using var args = JsonDocument.Parse("""{"path":"dir/out.txt","content":"bonjour","createDirs":true}""");
            var result = await tool.InvokeAsync(CreateContext("c1", "repo.write_file", args), CancellationToken.None);

            var fullPath = Path.Combine(tempDir, "dir", "out.txt");
            Assert.True(File.Exists(fullPath));
            Assert.Equal("bonjour", await File.ReadAllTextAsync(fullPath));

            using var payload = JsonDocument.Parse(result.Output);
            Assert.Equal("dir/out.txt", payload.RootElement.GetProperty("path").GetString());
            Assert.True(payload.RootElement.GetProperty("bytes").GetInt32() > 0);
        }
        finally { SafeDelete(tempDir); }
    }

    [Fact]
    public async Task ReadAndWrite_DenyTraversal()
    {
        var tempDir = CreateTempDir();
        try
        {
            var readTool = new RepoReadFileTool(tempDir);
            var writeTool = new RepoWriteFileTool(tempDir);

            using var readArgs = JsonDocument.Parse("""{"path":"../secret.txt"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => readTool.InvokeAsync(CreateContext("c1", "repo.read_file", readArgs), CancellationToken.None));

            using var writeArgs = JsonDocument.Parse("""{"path":"../secret.txt","content":"x"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => writeTool.InvokeAsync(CreateContext("c2", "repo.write_file", writeArgs), CancellationToken.None));
        }
        finally { SafeDelete(tempDir); }
    }

    private static ToolInvocationContext CreateContext(string callId, string toolName, JsonDocument arguments)
    {
        var toolCall = new AgentToolCall(toolName, callId, arguments);
        return new ToolInvocationContext(toolCall, (_, _, _) => Task.FromResult(new AgentWorkflowResult(null, Array.Empty<AgentMessage>())));
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "awm_repo_tools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void SafeDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
