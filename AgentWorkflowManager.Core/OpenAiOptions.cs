using System;
using System.Collections.Generic;
using System.IO;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Configuration container for OpenAI access.
/// </summary>
public sealed class OpenAiOptions
{
    private const string ApiKeyVariableName = "OPENAI_API_KEY";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnvFileVariables = new(LoadEnvFile);

    /// <summary>
    /// Creates options with the provided API key. If null, the value will be resolved from the environment variables or a local .env file.
    /// </summary>
    public OpenAiOptions(string? apiKey = null)
    {
        ApiKey = apiKey ?? ResolveApiKey()
            ?? throw new InvalidOperationException("OpenAI API key is not configured. Set OpenAiOptions.ApiKey, the OPENAI_API_KEY environment variable, or provide it in a .env file.");
    }

    /// <summary>
    /// Gets the API key used to authenticate requests against the OpenAI Responses API.
    /// </summary>
    public string ApiKey { get; }

    private static string? ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (EnvFileVariables.Value.TryGetValue(ApiKeyVariableName, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> LoadEnvFile()
    {
        try
        {
            var envPath = FindEnvFile(Directory.GetCurrentDirectory());
            if (envPath is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim().Trim('"');

                if (key.Length > 0)
                {
                    variables[key] = value;
                }
            }

            return variables;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindEnvFile(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
