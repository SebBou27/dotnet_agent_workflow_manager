using System;

namespace AgentWorkflowManager.Core;

/// <summary>
/// Immutable definition for agents backed by the OpenAI Responses API.
/// </summary>
public sealed record AgentDescriptor
{
    public AgentDescriptor(
        string name,
        string functionDescription,
        string model,
        double? temperature = null,
        double? topP = null,
        int? maxOutputTokens = null,
        string? systemPrompt = null,
        string? reasoningEffort = null,
        string? verbosity = null)
    {
        Name = !string.IsNullOrWhiteSpace(name) ? name : throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
        FunctionDescription = !string.IsNullOrWhiteSpace(functionDescription) ? functionDescription : throw new ArgumentException("Value cannot be null or whitespace.", nameof(functionDescription));
        Model = !string.IsNullOrWhiteSpace(model) ? model : throw new ArgumentException("Value cannot be null or whitespace.", nameof(model));
        Temperature = temperature;
        TopP = topP;
        MaxOutputTokens = maxOutputTokens;
        SystemPrompt = systemPrompt;
        ReasoningEffort = reasoningEffort ?? InferDefaultReasoningEffort(model);
        Verbosity = verbosity ?? InferDefaultVerbosity(model);
    }

    public string Name { get; }

    public string FunctionDescription { get; }

    public string Model { get; }

    public double? Temperature { get; }

    public double? TopP { get; }

    public int? MaxOutputTokens { get; }

    public string? SystemPrompt { get; }

    public string? ReasoningEffort { get; }

    public string? Verbosity { get; }

    private static string? InferDefaultReasoningEffort(string model)
        => string.Equals(model, "gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? "minimal" : null;

    private static string? InferDefaultVerbosity(string model)
        => string.Equals(model, "gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? "low" : null;
}
