using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class UiGenerateAssetToolTests
{
    [Fact]
    public async Task InvokeAsync_WritesPngFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "awm_assets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var pngBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            var b64 = Convert.ToBase64String(pngBytes);
            var handler = new FakeHttpHandler("{\"data\":[{\"b64_json\":\"" + b64 + "\"}]}");
            var client = new HttpClient(handler);
            var tool = new UiGenerateAssetTool(new OpenAiOptions("test-key"), root, client);

            using var args = JsonDocument.Parse("""{"prompt":"icon dashboard","filename":"icon-dashboard"}""");
            var result = await tool.InvokeAsync(Ctx("c1", "ui.generate_asset", args), CancellationToken.None);

            using var payload = JsonDocument.Parse(result.Output);
            var path = payload.RootElement.GetProperty("path").GetString();
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static ToolInvocationContext Ctx(string callId, string toolName, JsonDocument args)
    {
        var call = new AgentToolCall(toolName, callId, args);
        return new ToolInvocationContext(call, (_, _, _) => Task.FromResult(new AgentWorkflowResult(null, Array.Empty<AgentMessage>())));
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _response;

        public FakeHttpHandler(string response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(msg);
        }
    }
}
