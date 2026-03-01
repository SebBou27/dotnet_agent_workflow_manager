using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkflowManager.Core;

public sealed class RepoReadFileTool : IAgentTool
{
    private const int DefaultOffset = 1;
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "Chemin relatif au dépôt du fichier à lire."
        },
        "offset": {
          "type": "integer",
          "description": "Ligne de départ (1-indexé). Défaut: 1",
          "minimum": 1
        },
        "limit": {
          "type": "integer",
          "description": "Nombre max de lignes à retourner. Défaut: 200, Max: 2000",
          "minimum": 1,
          "maximum": 2000
        }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """)!;

    private readonly RepoSandboxPathResolver _pathResolver;

    public RepoReadFileTool(string repositoryRoot)
    {
        _pathResolver = new RepoSandboxPathResolver(repositoryRoot);
        Definition = new ToolDefinition(
            "repo.read_file",
            "Lit une portion de fichier texte dans le dépôt (sandbox: chemins relatifs uniquement).",
            ParametersSchema);
    }

    public string Name => Definition.Name;

    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var (relativePath, offset, limit) = GetArguments(context.ToolCall.Arguments);
        var fullPath = _pathResolver.Resolve(relativePath);

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Le fichier '{relativePath}' est introuvable dans le dépôt.");
        }

        var allText = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var lines = allText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var totalLines = lines.Length;

        var startIndex = Math.Max(0, offset - 1);
        if (startIndex >= totalLines)
        {
            var emptyPayload = JsonSerializer.Serialize(new
            {
                path = relativePath,
                offset,
                limit,
                totalLines,
                truncated = false,
                content = string.Empty,
            });
            return new AgentToolExecutionResult(context.ToolCall.CallId, emptyPayload);
        }

        var endExclusive = Math.Min(totalLines, startIndex + limit);
        var truncated = endExclusive < totalLines;

        var sb = new StringBuilder();
        for (var i = startIndex; i < endExclusive; i++)
        {
            if (i > startIndex)
            {
                sb.Append('\n');
            }

            sb.Append(lines[i]);
        }

        var payload = JsonSerializer.Serialize(new
        {
            path = relativePath,
            offset,
            limit,
            totalLines,
            truncated,
            content = sb.ToString(),
        });

        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }

    private static (string Path, int Offset, int Limit) GetArguments(JsonDocument arguments)
    {
        var path = ReadRequiredString(arguments, "path", "L'outil repo.read_file requiert un argument string 'path'.");

        var offset = DefaultOffset;
        if (arguments.RootElement.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out offset) || offset < 1)
            {
                throw new InvalidOperationException("L'argument 'offset' doit être un entier >= 1.");
            }
        }

        var limit = DefaultLimit;
        if (arguments.RootElement.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out limit) || limit < 1)
            {
                throw new InvalidOperationException("L'argument 'limit' doit être un entier >= 1.");
            }
        }

        limit = Math.Min(limit, MaxLimit);
        return (path, offset, limit);
    }

    private static string ReadRequiredString(JsonDocument arguments, string name, string error)
    {
        if (!arguments.RootElement.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(error);
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"L'argument '{name}' ne peut pas être vide.");
        }

        return value;
    }
}

public sealed class RepoWriteFileTool : IAgentTool
{
    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "Chemin relatif au dépôt du fichier à écrire."
        },
        "content": {
          "type": "string",
          "description": "Contenu complet à écrire dans le fichier."
        },
        "createDirs": {
          "type": "boolean",
          "description": "Crée les dossiers parents si besoin. Défaut: true"
        }
      },
      "required": ["path", "content"],
      "additionalProperties": false
    }
    """)!;

    private readonly RepoSandboxPathResolver _pathResolver;

    public RepoWriteFileTool(string repositoryRoot)
    {
        _pathResolver = new RepoSandboxPathResolver(repositoryRoot);
        Definition = new ToolDefinition(
            "repo.write_file",
            "Écrit un fichier texte dans le dépôt (sandbox: chemins relatifs uniquement).",
            ParametersSchema);
    }

    public string Name => Definition.Name;

    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var (relativePath, content, createDirs) = GetArguments(context.ToolCall.Arguments);
        var fullPath = _pathResolver.Resolve(relativePath);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            if (!createDirs)
            {
                throw new InvalidOperationException("Le dossier parent n'existe pas et createDirs=false.");
            }

            Directory.CreateDirectory(directory);
        }

        var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            if (File.Exists(fullPath))
            {
                File.Copy(tempPath, fullPath, overwrite: true);
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            path = relativePath,
            bytes = Encoding.UTF8.GetByteCount(content),
        });

        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }

    private static (string Path, string Content, bool CreateDirs) GetArguments(JsonDocument arguments)
    {
        var path = ReadRequiredString(arguments, "path", "L'outil repo.write_file requiert un argument string 'path'.");
        var content = ReadRequiredString(arguments, "content", "L'outil repo.write_file requiert un argument string 'content'.");

        var createDirs = true;
        if (arguments.RootElement.TryGetProperty("createDirs", out var createDirsElement) && createDirsElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (createDirsElement.ValueKind != JsonValueKind.True && createDirsElement.ValueKind != JsonValueKind.False)
            {
                throw new InvalidOperationException("L'argument 'createDirs' doit être un booléen.");
            }

            createDirs = createDirsElement.GetBoolean();
        }

        return (path, content, createDirs);
    }

    private static string ReadRequiredString(JsonDocument arguments, string name, string error)
    {
        if (!arguments.RootElement.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(error);
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"L'argument '{name}' ne peut pas être vide.");
        }

        return value;
    }
}

internal sealed class RepoSandboxPathResolver
{
    private readonly string _repositoryRoot;

    public RepoSandboxPathResolver(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new ArgumentException("Repository root cannot be null or whitespace.", nameof(repositoryRoot));
        }

        _repositoryRoot = EnsureTrailingSeparator(Path.GetFullPath(repositoryRoot));
    }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("L'argument 'path' ne peut pas être vide.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Le chemin doit être relatif à la racine du dépôt.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!fullPath.StartsWith(_repositoryRoot, comparison))
        {
            throw new InvalidOperationException("Le chemin demandé sort du sandbox du dépôt.");
        }

        return fullPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
