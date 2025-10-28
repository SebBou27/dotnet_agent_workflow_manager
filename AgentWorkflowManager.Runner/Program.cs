using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

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

    var options = new OpenAiOptions();
    using var client = new OpenAiResponseClient(options);

    var descriptor = new AgentDescriptor(
        name: "nano-runner",
        functionDescription: "Provide concise, helpful answers to user questions in French.",
        model: "gpt-5-nano");

    var agent = new OpenAiAgent(client, descriptor);

    var manager = new WorkflowManager();
    manager.RegisterAgent(agent);

    var mcpClient = await RegisterMcpToolsIfPresentAsync(manager).ConfigureAwait(false);

    try
    {
        var session = new AgentSession(manager, "nano-runner");
        Console.WriteLine("Session ouverte avec l'agent gpt-5-nano. Entrée vide pour quitter.");

        while (true)
        {
            Console.Write("Vous: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Fin de la session.");
                break;
            }

            var result = await session.SendAsync(input).ConfigureAwait(false);
            var reply = session.GetLatestAssistantText();

            if (string.IsNullOrWhiteSpace(reply))
            {
                Console.WriteLine("(Pas de réponse)");
                continue;
            }

            Console.WriteLine($"Agent: {reply}");

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

static async Task<IAsyncDisposable?> RegisterMcpToolsIfPresentAsync(WorkflowManager manager)
{
    var configPath = ResolveMcpConfigPath();
    if (configPath is null)
    {
        return null;
    }

    if (!McpToolConfigLoader.TryLoadFromFile(configPath, out var descriptors))
    {
        return null;
    }

    Console.WriteLine($"Chargement de {descriptors.Count} outils MCP depuis {configPath}.");

    var client = new McpHttpToolClient();

    foreach (var descriptor in descriptors)
    {
        manager.RegisterTool(new McpAgentTool(descriptor, client));
    }

    return client;
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
