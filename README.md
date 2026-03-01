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

Mode non-interactif via stdin (CI/pipes):
```bash
echo "Fais un plan de test" | dotnet run --project AgentWorkflowManager.Runner -- --stdin --agent planner
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

## Tools fichiers (repo.list_tree / repo.search / repo.read_file / repo.write_file)
Le runner enregistre des tools locaux sandboxés sur la racine du dépôt.

Configuration:
```json
"Tools": {
  "EnableRepoFileTools": true,
  "WorkspaceRoot": "."
}
```

### repo.list_tree
Arguments JSON:
```json
{ "path": ".", "recursive": true, "maxEntries": 300 }
```
- `path`: dossier relatif (défaut racine)
- `recursive`: défaut `true`
- `maxEntries`: défaut 300, max 5000

Retour JSON:
```json
{ "path":".", "recursive":true, "maxEntries":300, "count":2, "entries":[{"path":"src/app.js","kind":"file"}] }
```

### repo.search
Arguments JSON:
```json
{ "query": "token", "path": "src", "caseSensitive": false, "maxResults": 100 }
```
Retour JSON:
```json
{ "query":"token", "path":"src", "maxResults":100, "truncated":false, "count":1, "results":[{"file":"a.js","line":1,"text":"const token = 'abc';"}] }
```

### repo.read_file
Arguments JSON:
```json
{ "path": "src/app.js", "offset": 1, "limit": 200 }
```
- `offset`: 1-indexé, défaut 1
- `limit`: défaut 200, max 2000

Retour JSON:
```json
{ "path":"src/app.js", "offset":1, "limit":200, "totalLines":820, "truncated":true, "content":"..." }
```

### repo.write_file
Arguments JSON:
```json
{ "path": "src/new.txt", "content": "hello", "createDirs": true }
```
- écriture UTF-8 atomique (temp + replace)
- sandbox actif: pas de sortie hors `WorkspaceRoot`

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
