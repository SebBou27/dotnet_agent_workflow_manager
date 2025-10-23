using System;
using System.IO;
using AgentWorkflowManager.Core;
using Xunit;

namespace AgentWorkflowManager.Tests;

public sealed class OpenAiOptionsTests
{
    [Fact]
    public void OpenAiOptions_ReadsFromEnvFile_WhenEnvironmentVariableMissing()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalValue = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"envtest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, ".env"), "OPENAI_API_KEY=file-key");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Directory.SetCurrentDirectory(tempDirectory);

            var options = new OpenAiOptions();
            Assert.Equal("file-key", options.ApiKey);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalValue);

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // ignore cleanup issues
            }
        }
    }

    [Fact]
    public void OpenAiOptions_PrefersEnvironmentVariableOverEnvFile()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalValue = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"envtest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, ".env"), "OPENAI_API_KEY=file-key");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "env-key");
            Directory.SetCurrentDirectory(tempDirectory);

            var options = new OpenAiOptions();
            Assert.Equal("env-key", options.ApiKey);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalValue);

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // ignore cleanup issues
            }
        }
    }
}
