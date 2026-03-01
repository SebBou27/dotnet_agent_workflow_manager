using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

public sealed class UiGenerateAssetTool : IAgentTool
{
    public sealed record UiGenerateAssetOptions(
        string Model,
        string DefaultSize = "1024x1024",
        string DefaultQuality = "medium",
        string DefaultBackground = "auto");

    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "prompt": { "type": "string" },
        "filename": { "type": "string" },
        "size": { "type": "string", "enum": ["1024x1024", "1024x1536", "1536x1024"] },
        "quality": { "type": "string", "enum": ["low", "medium", "high"] },
        "background": { "type": "string", "enum": ["transparent", "opaque", "auto"] }
      },
      "required": ["prompt", "filename"],
      "additionalProperties": false
    }
    """)!;

    private readonly HttpClient _httpClient;
    private readonly string _assetsDirectory;
    private readonly UiGenerateAssetOptions _toolOptions;

    public UiGenerateAssetTool(OpenAiOptions options, string assetsDirectory, UiGenerateAssetOptions? toolOptions = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory))
        {
            throw new ArgumentException("assetsDirectory cannot be empty.", nameof(assetsDirectory));
        }

        _assetsDirectory = assetsDirectory;
        _toolOptions = toolOptions ?? new UiGenerateAssetOptions("gpt-image-1");
        Directory.CreateDirectory(_assetsDirectory);

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        Definition = new ToolDefinition(
            "ui.generate_asset",
            "Génère un asset visuel (PNG) via OpenAI Images API et l'enregistre localement.",
            ParametersSchema);
    }

    public string Name => Definition.Name;

    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var args = context.ToolCall.Arguments.RootElement;

        var prompt = ReadRequiredString(args, "prompt", "'prompt' is required.");
        var filename = ReadRequiredString(args, "filename", "'filename' is required.");
        var size = ReadOptionalString(args, "size") ?? _toolOptions.DefaultSize;
        var quality = ReadOptionalString(args, "quality") ?? _toolOptions.DefaultQuality;
        var background = ReadOptionalString(args, "background") ?? _toolOptions.DefaultBackground;

        if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".png";
        }

        var safeFilename = filename.Replace("..", string.Empty).Replace("/", "_").Replace("\\", "_");
        var outputPath = Path.Combine(_assetsDirectory, safeFilename);

        var requestBody = JsonSerializer.Serialize(new
        {
            model = _toolOptions.Model,
            prompt,
            size,
            quality,
            background,
            output_format = "png",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Image generation failed ({(int)response.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Image generation response did not contain data.");
        }

        var first = data[0];
        if (!first.TryGetProperty("b64_json", out var b64) || b64.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Image generation response missing b64_json.");
        }

        var bytes = Convert.FromBase64String(b64.GetString()!);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new
        {
            path = outputPath,
            bytes = bytes.Length,
            size,
            quality,
            background,
        });

        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }

    private static string ReadRequiredString(JsonElement root, string key, string error)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(error);
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
