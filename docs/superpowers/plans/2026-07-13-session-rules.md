# Session Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add application-specific session identity rules that let DevWT route browser/proxy traffic from the same tool session to the context that started the backend.

**Architecture:** Store session identity rules in runtime settings, resolve a process session from configured match and identity rules, and let the gateway map caller sessions to observed backend listener sessions. Expose rule management in the Web UI with typed fields instead of raw JSON.

**Tech Stack:** .NET 10, xUnit, ASP.NET Core minimal APIs, existing DevWT state store and Web UI.

## Global Constraints

- Core routing must stay generic; Codex, ABP Studio, Rider, and browsers must be expressed as rules.
- Session IDs are configuration-derived and must not replace explicit process targets.
- Web UI must expose structured controls, not raw JSON editing.

---

### Task 1: Runtime Models and Resolver

**Files:**
- Modify: `src/Devwt.Core/DevwtModels.cs`
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Test: `tests/Devwt.Core.Tests/HookCoreStateTests.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Produces: `DevwtSessionRule`, `DevwtSessionMatch`, `DevwtSessionIdentity`, `ProcessSessionResolver.ResolveSessionId(...)`, `ProcessSessionResolver.ResolveSessionContext(...)`.

- [ ] Write failing tests for runtime settings round-trip and root-process session routing.
- [ ] Run targeted tests and verify failure.
- [ ] Add model records and resolver.
- [ ] Re-run targeted tests and verify pass.

### Task 2: Control API and Web UI

**Files:**
- Modify: `src/Devwt.Service/ControlApi.cs`
- Modify: `src/Devwt.Cli/DevwtWebUiAspNetHost.cs`
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Produces: `DevwtControlOperation.SetSessionRule`, `DevwtControlOperation.RemoveSessionRule`, Web UI actions `add-session-rule` and `remove-session-rule`.

- [ ] Write failing tests for control handler add/remove and Web UI session rule controls.
- [ ] Run targeted tests and verify failure.
- [ ] Implement handler operations and structured Web UI form/list.
- [ ] Re-run targeted tests and verify pass.

### Task 3: Verification

**Files:**
- All changed files.

- [ ] Run `dotnet test .\Devwt.slnx --no-restore`.
- [ ] Run native hook build check.
- [ ] Rebuild installer bundle if release artifacts changed.
