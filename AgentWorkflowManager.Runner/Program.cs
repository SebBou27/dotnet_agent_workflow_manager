using System;
using System.Linq;
using System.Threading.Tasks;
using AgentWorkflowManager.Core;
using WorkflowManager = AgentWorkflowManager.Core.AgentWorkflowManager;

try
{
    var options = new OpenAiOptions();
    using var client = new OpenAiResponseClient(options);

    var descriptor = new AgentDescriptor(
        name: "nano-runner",
        functionDescription: "Provide concise, helpful answers to user questions in French.",
        model: "gpt-5-nano");

    var agent = new OpenAiAgent(client, descriptor);

    var manager = new WorkflowManager();
    manager.RegisterAgent(agent);

    var request = new AgentRequest(new[]
    {
        AgentMessage.FromText("user", "Dis bonjour, puis donne-moi un conseil productif en deux phrases."),
    });

    var result = await manager.RunAgentAsync("nano-runner", request);

    if (result.FinalMessage is null)
    {
        Console.WriteLine("L'agent n'a pas renvoyé de message final.");
        return;
    }

    foreach (var content in result.FinalMessage.Content.OfType<AgentTextContent>())
    {
        Console.WriteLine(content.Text);
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
