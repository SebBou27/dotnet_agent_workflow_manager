using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using Microsoft.Extensions.Configuration;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

var singlePrompt = GetSinglePrompt(args);
var forcedAgent = GetForcedAgent(args);
var stdinMode = HasFlag(args, "--stdin");
var configuration = BuildConfiguration();

try
{
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

    var plannerDescriptor = BuildDescriptor(appOptions.Planner);
    var executorDescriptor = BuildDescriptor(appOptions.Executor);

    var plannerAgent = new OpenAiAgent(client, plannerDescriptor);
    var executorAgent = new OpenAiAgent(client, executorDescriptor);

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

    manager.RegisterAgent(plannerAgent);
    manager.RegisterAgent(executorAgent);

    RegisterLocalRepoTools(manager, appOptions.Tools, Directory.GetCurrentDirectory());

    var router = new RuleBasedAgentRouter(new AgentRoutingOptions
    {
        PlannerAgentName = plannerDescriptor.Name,
        ExecutorAgentName = executorDescriptor.Name,
    });

    var sessionStore = appOptions.Workflow.SessionMemory.Enabled
        ? new AgentSessionMemoryStore(appOptions.Workflow.SessionMemory.Directory)
        : null;

    var metricsExporter = appOptions.Workflow.Metrics.Enabled
        ? new MetricsJsonlExporter(appOptions.Workflow.Metrics.FilePath)
        : null;

    var sessions = new System.Collections.Generic.Dictionary<string, AgentSession>(StringComparer.OrdinalIgnoreCase);

    AgentSession GetOrCreateSession(string agentName)
    {
        if (sessions.TryGetValue(agentName, out var existing))
        {
            return existing;
        }

        var initialConversation = sessionStore?.LoadConversation(agentName);
        var session = new AgentSession(manager, agentName, initialConversation);
        sessions[agentName] = session;
        return session;
    }

    var mcpClient = await RegisterMcpToolsIfPresentAsync(manager).ConfigureAwait(false);

    try
    {
        if (stdinMode && string.IsNullOrWhiteSpace(singlePrompt))
        {
            singlePrompt = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(singlePrompt))
        {
            var target = ResolveTargetAgent(forcedAgent, router, singlePrompt, plannerDescriptor.Name, executorDescriptor.Name);
            var session = GetOrCreateSession(target);
            Console.WriteLine($"[route] {target}");
            var singleResult = await RunSinglePromptAsync(session, singlePrompt).ConfigureAwait(false);
            sessionStore?.SaveConversation(target, session.Conversation);
            metricsExporter?.Append(target, singleResult.Metrics);
            return;
        }

        Console.WriteLine($"Session ouverte. Planner={plannerDescriptor.Model} | Executor={executorDescriptor.Model}");
        Console.WriteLine("Entrée vide pour quitter. Préfixes: /plan ... | /exec ... | /status");

        AgentWorkflowMetrics? lastMetrics = null;

        while (true)
        {
            Console.Write("Vous: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Fin de la session.");
                break;
            }

            if (string.Equals(input.Trim(), "/status", StringComparison.OrdinalIgnoreCase))
            {
                PrintStatus(sessions, lastMetrics, sessionStore is not null ? appOptions.Workflow.SessionMemory.Directory : null);
                continue;
            }

            var target = ResolveTargetAgent(forcedAgent, router, input, plannerDescriptor.Name, executorDescriptor.Name);
            var session = GetOrCreateSession(target);
            Console.WriteLine($"[route] {target}");
            var result = await RunSinglePromptAsync(session, input).ConfigureAwait(false);
            lastMetrics = result.Metrics;
            sessionStore?.SaveConversation(target, session.Conversation);
            metricsExporter?.Append(target, result.Metrics);
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

static AgentDescriptor BuildDescriptor(AgentOptions options)
{
    return new AgentDescriptor(
        name: options.Name,
        functionDescription: options.FunctionDescription,
        model: options.Model,
        temperature: options.Temperature,
        topP: options.TopP,
        maxOutputTokens: options.MaxOutputTokens,
        systemPrompt: options.SystemPrompt,
        reasoningEffort: options.ReasoningEffort,
        verbosity: options.Verbosity);
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

static void RegisterLocalRepoTools(WorkflowManager manager, ToolsOptions options, string currentDirectory)
{
    if (!options.EnableRepoFileTools)
    {
        return;
    }

    var workspaceRoot = string.IsNullOrWhiteSpace(options.WorkspaceRoot)
        ? currentDirectory
        : options.WorkspaceRoot;

    manager.RegisterTool(new RepoReadFileTool(workspaceRoot));
    manager.RegisterTool(new RepoWriteFileTool(workspaceRoot));
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

static async Task<AgentWorkflowResult> RunSinglePromptAsync(AgentSession session, string prompt)
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

    if (result.Metrics is not null)
    {
        Console.WriteLine($"[metrics] turns={result.Metrics.Turns} tools={result.Metrics.ToolCallsRequested} ok={result.Metrics.ToolCallsSucceeded} err={result.Metrics.ToolCallsFailed} durationMs={result.Metrics.DurationMs}");
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

    return result;
}

static void PrintStatus(System.Collections.Generic.IReadOnlyDictionary<string, AgentSession> sessions, AgentWorkflowMetrics? lastMetrics, string? memoryDirectory)
{
    Console.WriteLine("=== STATUS ===");
    Console.WriteLine($"Sessions actives: {sessions.Count}");
    foreach (var kvp in sessions)
    {
        Console.WriteLine($" - {kvp.Key}: {kvp.Value.Conversation.Count} messages");
    }

    if (lastMetrics is not null)
    {
        Console.WriteLine($"Dernières métriques: turns={lastMetrics.Turns}, tools={lastMetrics.ToolCallsRequested}, ok={lastMetrics.ToolCallsSucceeded}, err={lastMetrics.ToolCallsFailed}, durationMs={lastMetrics.DurationMs}");
    }
    else
    {
        Console.WriteLine("Dernières métriques: n/a");
    }

    if (!string.IsNullOrWhiteSpace(memoryDirectory))
    {
        Console.WriteLine($"Session memory: enabled ({memoryDirectory})");
    }
    else
    {
        Console.WriteLine("Session memory: disabled");
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

static string? GetForcedAgent(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        if (arg.StartsWith("--agent=", StringComparison.OrdinalIgnoreCase))
        {
            return arg.Substring("--agent=".Length);
        }
    }

    return null;
}

static bool HasFlag(string[] args, string flag)
    => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

static string ResolveTargetAgent(string? forcedAgent, RuleBasedAgentRouter router, string input, string plannerName, string executorName)
{
    if (string.IsNullOrWhiteSpace(forcedAgent))
    {
        return router.ResolveAgent(input);
    }

    return forcedAgent.Trim().ToLowerInvariant() switch
    {
        "planner" => plannerName,
        "executor" => executorName,
        _ => router.ResolveAgent(input),
    };
}

sealed class AppOptions
{
    public ToolsOptions Tools { get; init; } = new();

    public AgentOptions Planner { get; init; } = new()
    {
        Name = "planner",
        FunctionDescription = "Break down user requests into concise plans in French, then provide clear actionable steps.",
        Model = "gpt-5-nano",
        ReasoningEffort = "minimal",
        Verbosity = "low",
    };

    public AgentOptions Executor { get; init; } = new()
    {
        Name = "executor",
        FunctionDescription = "Execute technical/code-oriented requests in French with concise outputs.",
        Model = "gpt-5-nano",
        ReasoningEffort = "minimal",
        Verbosity = "low",
    };

    public WorkflowOptions Workflow { get; init; } = new();
}

sealed class ToolsOptions
{
    public bool EnableRepoFileTools { get; init; } = true;
    public string WorkspaceRoot { get; init; } = ".";
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
    public SessionMemoryOptions SessionMemory { get; init; } = new();
    public MetricsOptions Metrics { get; init; } = new();
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

sealed class SessionMemoryOptions
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = ".sessions";
}

sealed class MetricsOptions
{
    public bool Enabled { get; init; } = true;
    public string FilePath { get; init; } = ".metrics/workflow-metrics.jsonl";
}

sealed class MetricsJsonlExporter
{
    private readonly string _filePath;

    public MetricsJsonlExporter(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Append(string agentName, AgentWorkflowMetrics? metrics)
    {
        if (metrics is null) return;

        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            agent = agentName,
            turns = metrics.Turns,
            toolCalls = metrics.ToolCallsRequested,
            toolOk = metrics.ToolCallsSucceeded,
            toolErr = metrics.ToolCallsFailed,
            durationMs = metrics.DurationMs,
        });

        File.AppendAllText(_filePath, line + Environment.NewLine);
    }
}
