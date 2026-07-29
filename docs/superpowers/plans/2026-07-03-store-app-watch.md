# Store App Watch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add durable DevWT IDE watch support for Microsoft Store/MSIX apps such as Codex.

**Architecture:** Keep existing path-based watches for classic desktop apps, and add package-family based watches for Store apps. CLI accepts `--app-id` and `--package-family`; service state stores both; the native watcher matches started processes by `GetPackageFamilyName` and injects in children-only mode.

**Tech Stack:** .NET 10 CLI/service, xUnit tests, Win32 C++ hook watcher.

---

### Task 1: Managed CLI And Planner

**Files:**
- Modify: `src/Devwt.Core/DevwtModels.cs`
- Modify: `src/Devwt.Core/DevwtCommandParser.cs`
- Modify: `src/Devwt.Service/HookRuntimeAdapter.cs`
- Modify: `src/Devwt.Service/ControlApi.cs`
- Test: `tests/Devwt.Core.Tests/HookCoreCommandTests.cs`
- Test: `tests/Devwt.Service.Tests/HookRuntimeProductTests.cs`

- [x] Add failing parser tests for `ide watch add --app-id ...` and `--package-family ...`.
- [x] Add failing planner test that expects `--children-only-package-family`.
- [x] Extend `DevwtIdeWatch` with nullable `ImagePath`, `AppId`, and `PackageFamilyName`.
- [x] Parse `--app-id` by deriving the package family from the part before `!`.
- [x] Parse `--package-family` directly.
- [x] Require exactly one selector for add: `--path`, `--app-id`, or `--package-family`.
- [x] Include package-family watches in planner arguments and list output.
- [x] Run managed tests.

### Task 2: Native Watcher

**Files:**
- Modify: `poc/hook-win32/src/devwt_folder_watcher.cpp`
- Test: `poc/hook-win32/Run-ChildrenOnlyPackageFamilyWatcherSmoke.ps1`

- [x] Add failing smoke that passes `--children-only-package-family DevWT.NoSuchPackage_0000000000000` and verifies the watcher accepts the selector without requiring a versioned executable path.
- [x] Add `ChildrenOnlyPackageFamily` storage and CLI parsing.
- [x] Query process package family via `GetPackageFamilyName`.
- [x] Match package family in the children-only path before falling back to image path.
- [x] Keep path-based watches unchanged.
- [x] Build native artifacts and run native smoke tests.

### Task 3: Install And Verify

**Files:**
- Modify: `docs/troubleshooting.md`

- [x] Document Store app watch commands.
- [x] Build installer bundle.
- [x] Install elevated.
- [x] Add Codex watch using `--app-id OpenAI.Codex_2p2nqsd0c76g0!App`.
- [x] Verify `ide watch list`, service state, and watcher logs.
