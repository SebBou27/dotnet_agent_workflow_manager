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

        var result = await session.SendAsync(input);
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
catch (Exception ex)
{
    Console.Error.WriteLine($"Erreur lors de l'exécution du workflow : {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine(ex.InnerException);
    }
}
