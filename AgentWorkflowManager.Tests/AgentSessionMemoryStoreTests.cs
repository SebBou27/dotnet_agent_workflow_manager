using System;
using System.IO;
using System.Linq;
using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class AgentSessionMemoryStoreTests
{
    [Fact]
    public void SaveAndLoadConversation_RoundTripsMessages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"awm_mem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new AgentSessionMemoryStore(tempDir);
            var conversation = new AgentMessage[]
            {
                AgentMessage.FromText("user", "Bonjour"),
                AgentMessage.FromText("assistant", "Salut"),
                AgentMessage.FromToolResult("call-1", "tool output", isError: false),
            };

            store.SaveConversation("planner", conversation);
            var loaded = store.LoadConversation("planner");

            Assert.Equal(3, loaded.Count);
            Assert.Equal("user", loaded[0].Role);
            Assert.Equal("assistant", loaded[1].Role);
            Assert.Equal("tool", loaded[2].Role);

            var tool = Assert.IsType<AgentToolResultContent>(loaded[2].Content.Single());
            Assert.Equal("call-1", tool.ToolCallId);
            Assert.Equal("tool output", tool.Output);
            Assert.False(tool.IsError);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
