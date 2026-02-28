# Release Checklist (v0.1.x)

- [ ] `dotnet restore AgentWorkflowManager.sln`
- [ ] `dotnet build AgentWorkflowManager.sln -c Debug`
- [ ] `dotnet test AgentWorkflowManager.sln -c Debug`
- [ ] Verify `README.md` examples still work
- [ ] Confirm no secrets in tracked files (`OPENAI_API_KEY`, `mcp.tools.json`)
- [ ] Confirm log policy defaults are safe (`AWM_LOG_PAYLOADS` disabled)
- [ ] Update `CHANGELOG.md`
- [ ] Tag release (`v0.1.x`) and publish notes
