# EXAMPLES

Exemples concrets d'utilisation de `dotnet_agent_workflow_manager`.

## 1) Prompt unique (one-shot)
```bash
dotnet run --project AgentWorkflowManager.Runner -- --prompt "Donne-moi un plan de release en 5 étapes"
```

## 2) Session interactive
```bash
dotnet run --project AgentWorkflowManager.Runner
```
Puis dans le prompt:
- `Je veux une roadmap produit`
- `/plan Décompose ce besoin en backlog`
- `/exec Propose un squelette d'implémentation API`
- `/status`

## 3) Forcer le routage planner/executor
- Forcer planner:
```text
/plan Compare 2 approches d'architecture
```
- Forcer executor:
```text
/exec Écris les tests unitaires pour ce service
```

## 4) Activer logs détaillés localement
```powershell
$env:AWM_LOG_LEVEL = "debug"
$env:AWM_LOG_PAYLOADS = "true"
dotnet run --project AgentWorkflowManager.Runner -- --prompt "Bonjour"
```

## 5) Utiliser un modèle différent via appsettings
Dans `AgentWorkflowManager.Runner/appsettings.Development.json`:
```json
{
  "AgentWorkflow": {
    "Planner": { "Model": "gpt-5-mini" },
    "Executor": { "Model": "gpt-5-mini" }
  }
}
```

## 6) Mémoire de session on/off
Dans `appsettings.json`:
```json
"SessionMemory": {
  "Enabled": true,
  "Directory": ".sessions"
}
```

## 7) Métriques JSONL + résumé
Exécuter quelques prompts, puis:
```powershell
./scripts/metrics-summary.ps1
```
Ou:
```powershell
./scripts/metrics-summary.ps1 -Path ".metrics/workflow-metrics.jsonl" -Last 500
```

## 8) Test MCP direct
```powershell
$env:MCP_DIRECT_TEST = "1"
$env:MCP_TEST_FILE_ID = "<file_id>"
$env:MCP_TEST_TAB = "MGS0001"
dotnet run --project AgentWorkflowManager.Runner
```

## 9) Erreurs fréquentes
- `OpenAI API key is not configured`
  - définir `OPENAI_API_KEY` dans l'environnement ou `.env`
- `mcp.tools.json introuvable`
  - copier `mcp.tools.example.json` en `mcp.tools.json`
- timeouts fréquents
  - augmenter `AgentWorkflow:Workflow:Timeouts`.
