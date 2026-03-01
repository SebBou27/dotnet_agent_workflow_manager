# dotnet_agent_workflow_manager

Gestionnaire de workflow d'agents .NET (OpenAI Responses API + outils MCP).

## Ce que fait le projet
- Orchestre un ou plusieurs agents (`AgentWorkflowManager`)
- Exécute des appels d'outils (tool-calls) et réinjecte les résultats dans la conversation
- Supporte la délégation inter-agent
- Peut charger des outils MCP HTTP via `mcp.tools.json`

## Structure
- `AgentWorkflowManager.Core` : moteur (agents, outils, workflow, client OpenAI)
- `AgentWorkflowManager.Runner` : exécutable console
- `AgentWorkflowManager.Tests` : tests unitaires

## Prérequis
- .NET 9 SDK
- Clé OpenAI (`OPENAI_API_KEY`) via variable d'environnement ou `.env`

## Démarrage rapide
```bash
dotnet restore AgentWorkflowManager.sln
dotnet test AgentWorkflowManager.sln -c Debug
dotnet run --project AgentWorkflowManager.Runner -- --prompt "Bonjour"
```

Forcer l'agent en CLI (pratique CI):
```bash
dotnet run --project AgentWorkflowManager.Runner -- --prompt "Implémente X" --agent executor
dotnet run --project AgentWorkflowManager.Runner -- --prompt "Planifie Y" --agent planner
```

Mode interactif:
```bash
dotnet run --project AgentWorkflowManager.Runner
```
Commandes utiles:
- `/plan ...` force le planner
- `/exec ...` force l'executor
- `/status` affiche sessions actives + dernières métriques

## Configuration
Le runner charge:
1. `AgentWorkflowManager.Runner/appsettings.json`
2. `AgentWorkflowManager.Runner/appsettings.Development.json` (optionnel)
3. Variables d'environnement préfixées `AWM_`

Exemple config:
```json
{
  "AgentWorkflow": {
    "Agent": {
      "Name": "nano-runner",
      "FunctionDescription": "Provide concise, helpful answers to user questions in French.",
      "Model": "gpt-5-nano",
      "ReasoningEffort": "minimal",
      "Verbosity": "low"
    },
    "Workflow": {
      "MaxTurns": 8,
      "Retry": {
        "MaxAttempts": 3,
        "DelayMs": 400
      },
      "Timeouts": {
        "AgentSeconds": 90,
        "ToolSeconds": 45
      }
    }
  }
}
```

## Variables d'environnement utiles
- `OPENAI_API_KEY` : clé API OpenAI
- `AWM_LOG_LEVEL` : `off|error|info|debug|trace`
- `AWM_LOG_PAYLOADS` : `true|false` (par défaut recommandé: false)
- `MCP_DIRECT_TEST=1` : mode test MCP direct

## Mémoire de session (planner/executor)
Le runner persiste l'historique de conversation par agent dans `./.sessions` (configurable):
- `AgentWorkflow:Workflow:SessionMemory:Enabled`
- `AgentWorkflow:Workflow:SessionMemory:Directory`

Exemple:
```json
"SessionMemory": {
  "Enabled": true,
  "Directory": ".sessions"
}
```

## Export métriques (JSONL)
Le runner peut exporter les métriques de chaque interaction dans un fichier JSONL:
- `AgentWorkflow:Workflow:Metrics:Enabled`
- `AgentWorkflow:Workflow:Metrics:FilePath`

Exemple:
```json
"Metrics": {
  "Enabled": true,
  "FilePath": ".metrics/workflow-metrics.jsonl"
}
```

Chaque ligne contient: timestamp UTC, agent ciblé, turns, tool calls, erreurs, durée.

### Synthèse rapide des métriques
Script fourni:
```powershell
./scripts/metrics-summary.ps1
```

Options:
```powershell
./scripts/metrics-summary.ps1 -Path ".metrics/workflow-metrics.jsonl" -Last 500
```

Sortie: volume, durée moyenne/p50/p95, taux d'erreur tools global + par agent.

## MCP tools
1. Copier `mcp.tools.example.json` vers `mcp.tools.json`
2. Ajuster les endpoints
3. Lancer le runner

Le fichier `mcp.tools.json` est ignoré par Git.

## Sécurité logs
Par défaut, les payloads complets prompts/réponses sont masqués dans les logs.
Pour debug local approfondi: `AWM_LOG_PAYLOADS=true`.

## Exemples d'utilisation
Voir `EXAMPLES.md` pour des scénarios prêts à copier-coller (one-shot, interactif, /plan, /exec, /status, métriques, MCP).

## Limites actuelles
- Runner orienté console (pas encore service HTTP)

## Dépannage rapide
- `OpenAI API key is not configured` : vérifier `OPENAI_API_KEY` / `.env`
- timeout tool/agent : augmenter `AgentWorkflow:Workflow:Timeouts`
- aucun outil MCP chargé : vérifier `mcp.tools.json`
