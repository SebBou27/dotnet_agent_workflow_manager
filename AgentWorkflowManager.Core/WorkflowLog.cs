using System;

namespace AgentWorkflowManager.Core;

internal static class WorkflowLog
{
    private static readonly string Level = (Environment.GetEnvironmentVariable("AWM_LOG_LEVEL") ?? "info").Trim().ToLowerInvariant();
    private static readonly bool VerbosePayload = string.Equals(Environment.GetEnvironmentVariable("AWM_LOG_PAYLOADS"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("AWM_LOG_PAYLOADS"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsDebug => Level is "debug" or "trace";

    public static void Info(string message)
    {
        if (Level is "off" or "error") return;
        Console.WriteLine(message);
    }

    public static void Debug(string message)
    {
        if (!IsDebug) return;
        Console.WriteLine(message);
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine(message);
    }

    public static string SafePayload(string payload)
    {
        if (VerbosePayload)
        {
            return payload;
        }

        // Keep logs usable without exposing full prompts/outputs by default.
        if (string.IsNullOrEmpty(payload)) return "<empty>";
        return $"<redacted payload, length={payload.Length}>";
    }
}
