using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Microsoft.Extensions.Configuration;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

var singlePrompt = GetSinglePrompt(args);
var configuration = BuildConfiguration();

try
{
    // Optional: direct MCP test mode without going through the LLM.
    if (Environment.GetEnvironmentVariable("MCP_DIRECT_TEST") == "1")
    {
        var configPath = ResolveMcpConfigPath();
        if (configPath is null)
        {
            Console.Error.WriteLine("mcp.tools.json introuvable pour MCP_DIRECT_TEST");
            return;
        }

        if (!McpToolConfigLoader.TryLoadFromFile(configPath, out var descriptors) || descriptors.Count == 0)
        {
            Console.Error.WriteLine("Aucun outil MCP configuré pour MCP_DIRECT_TEST");
            return;
        }

        var tool = descriptors.FirstOrDefault(d => string.Equals(d.Name, "analysis.get_tab_data", StringComparison.OrdinalIgnoreCase))
                   ?? descriptors.First();

        await using var directClient = new McpHttpToolClient();

        var fileId = Environment.GetEnvironmentVariable("MCP_TEST_FILE_ID") ?? "1rw2cUdsAp8MnguJoCTyxDIqn1QhA2vi-";
        var tabName = Environment.GetEnvironmentVariable("MCP_TEST_TAB") ?? "MGS0001";

        using var argsDoc = System.Text.Json.JsonDocument.Parse("{\"file_id\":\"" + fileId + "\",\"tab_name\":\"" + tabName + "\"}");
        var output = await directClient.InvokeAsync(tool, argsDoc, default);
        Console.WriteLine(output);
        return;
    }

    var openAiOptions = new OpenAiOptions();
    using var client = new OpenAiResponseClient(openAiOptions);

    var appOptions = configuration.GetSection("AgentWorkflow").Get<AppOptions>() ?? new AppOptions();

    var descriptor = new AgentDescriptor(
        name: appOptions.Agent.Name,
        functionDescription: appOptions.Agent.FunctionDescription,
        model: appOptions.Agent.Model,
        temperature: appOptions.Agent.Temperature,
        topP: appOptions.Agent.TopP,
        maxOutputTokens: appOptions.Agent.MaxOutputTokens,
        systemPrompt: appOptions.Agent.SystemPrompt,
        reasoningEffort: appOptions.Agent.ReasoningEffort,
        verbosity: appOptions.Agent.Verbosity);

    var agent = new OpenAiAgent(client, descriptor);

    var manager = new WorkflowManager(
        maxTurns: appOptions.Workflow.MaxTurns,
        retryPolicy: new AgentRetryPolicy(
            maxAttempts: appOptions.Workflow.Retry.MaxAttempts,
            delayBetweenAttempts: TimeSpan.FromMilliseconds(appOptions.Workflow.Retry.DelayMs)),
        runtimeOptions: new WorkflowRuntimeOptions
        {
            AgentTimeout = TimeSpan.FromSeconds(appOptions.Workflow.Timeouts.AgentSeconds),
            ToolTimeout = TimeSpan.FromSeconds(appOptions.Workflow.Timeouts.ToolSeconds),
        });

    manager.RegisterAgent(agent);

    var mcpClient = await RegisterMcpToolsIfPresentAsync(manager).ConfigureAwait(false);

    try
    {
        var session = new AgentSession(manager, descriptor.Name);

        if (!string.IsNullOrEmpty(singlePrompt))
        {
            await RunSinglePromptAsync(session, singlePrompt).ConfigureAwait(false);
            return;
        }

        Console.WriteLine($"Session ouverte avec l'agent {descriptor.Model}. Entrée vide pour quitter.");

        while (true)
        {
            Console.Write("Vous: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Fin de la session.");
                break;
            }

            await RunSinglePromptAsync(session, input).ConfigureAwait(false);
        }
    }
    finally
    {
        if (mcpClient is not null)
        {
            await mcpClient.DisposeAsync().ConfigureAwait(false);
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Erreur lors de l'exécution du workflow : {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine(ex.InnerException);
    }
}

static IConfigurationRoot BuildConfiguration()
{
    return new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddEnvironmentVariables(prefix: "AWM_")
        .Build();
}

static Task<IAsyncDisposable?> RegisterMcpToolsIfPresentAsync(WorkflowManager manager)
{
    var configPath = ResolveMcpConfigPath();
    if (configPath is null)
    {
        return Task.FromResult<IAsyncDisposable?>(null);
    }

    if (!McpToolConfigLoader.TryLoadFromFile(configPath, out var descriptors))
    {
        return Task.FromResult<IAsyncDisposable?>(null);
    }

    Console.WriteLine($"Chargement de {descriptors.Count} outils MCP depuis {configPath}.");

    var client = new McpHttpToolClient();

    foreach (var descriptor in descriptors)
    {
        manager.RegisterTool(new McpAgentTool(descriptor, client));
    }

    return Task.FromResult<IAsyncDisposable?>(client);
}

static string? ResolveMcpConfigPath()
{
    static string? Check(string candidate) => File.Exists(candidate) ? candidate : null;

    var baseDir = AppContext.BaseDirectory;
    var currentDir = Directory.GetCurrentDirectory();

    var direct = Check(Path.Combine(baseDir, "mcp.tools.json")) ?? Check(Path.Combine(currentDir, "mcp.tools.json"));
    if (direct is not null)
    {
        return direct;
    }

    var directory = new DirectoryInfo(currentDir);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "mcp.tools.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}

static async Task RunSinglePromptAsync(AgentSession session, string prompt)
{
    var result = await session.SendAsync(prompt).ConfigureAwait(false);
    var reply = session.GetLatestAssistantText();

    if (!string.IsNullOrWhiteSpace(reply))
    {
        Console.WriteLine($"Agent: {reply}");
    }
    else
    {
        Console.WriteLine("(Pas de réponse)");
    }

    if (result.Conversation.Any(message => message.Role == "tool"))
    {
        foreach (var toolMessage in result.Conversation.Where(m => m.Role == "tool"))
        {
            var toolContent = toolMessage.Content.OfType<AgentToolResultContent>().FirstOrDefault();
            if (toolContent is not null)
            {
                var status = toolContent.IsError ? "erreur outil" : "outil";
                Console.WriteLine($"[{status}] {toolContent.Output}");
            }
        }
    }
}

static string? GetSinglePrompt(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg.Equals("--prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        else if (arg.StartsWith("--prompt=", StringComparison.OrdinalIgnoreCase))
        {
            return arg.Substring("--prompt=".Length);
        }
    }

    return null;
}

sealed class AppOptions
{
    public AgentOptions Agent { get; init; } = new();
    public WorkflowOptions Workflow { get; init; } = new();
}

sealed class AgentOptions
{
    public string Name { get; init; } = "nano-runner";
    public string FunctionDescription { get; init; } = "Provide concise, helpful answers to user questions in French.";
    public string Model { get; init; } = "gpt-5-nano";
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? SystemPrompt { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? Verbosity { get; init; }
}

sealed class WorkflowOptions
{
    public int MaxTurns { get; init; } = 8;
    public RetryOptions Retry { get; init; } = new();
    public TimeoutOptions Timeouts { get; init; } = new();
}

sealed class RetryOptions
{
    public int MaxAttempts { get; init; } = 3;
    public int DelayMs { get; init; } = 400;
}

sealed class TimeoutOptions
{
    public int AgentSeconds { get; init; } = 90;
    public int ToolSeconds { get; init; } = 45;
}
