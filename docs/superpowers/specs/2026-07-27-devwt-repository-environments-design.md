# DevWT Repository Environments Design

## Goal

Add repository-specific development environments for large repositories and
monorepos. An environment bundles all knowledge needed to prepare one DevWT
worktree:

- a PowerShell setup script that may create databases, run migrations, install
  NPM dependencies, or perform any other repository-specific preparation; and
- Codex instructions that apply only while that environment is selected.

DevWT does not model individual setup steps. It validates the package, applies
the agent instructions, runs one user-owned PowerShell script, and records the
result.

Environment setup is manual in this version. No hook, worktree synchronizer, or
repository-controlled setting may start setup automatically.

## Scope

This change covers:

- machine-local and repository-shared environment packages;
- one selected environment per worktree;
- environment discovery, validation, creation, editing, removal, and setup;
- complete, script-only, and agent-only setup modes;
- safe generation of a worktree-root `AGENTS.override.md`;
- local Git exclusion and accidental-commit protection for that generated file;
- user-context PowerShell execution;
- setup state, bounded logs, CLI commands, and Admin UI controls;
- installation of a safe custom URI launcher for Admin UI actions;
- automated and installed-runtime verification.

It does not add:

- automatic setup during worktree creation or discovery;
- a setup step graph, dependency model, retry policy, or parallel execution;
- implicit trust of scripts from a repository or branch;
- non-PowerShell setup runners;
- secret detection or redaction;
- live reloading of instructions in an already-running Codex task.

## Environment Package

Every environment is a directory with the same three-file contract:

```text
<environment-name>/
  environment.json
  setup.ps1
  AGENTS.md
```

All three files are required and must be non-empty.

`environment.json` has a deliberately small versioned schema:

```json
{
  "schemaVersion": 1,
  "name": "full",
  "description": "Full development environment",
  "powershell": "pwsh"
}
```

The fields have these meanings:

- `schemaVersion` must be `1`.
- `name` must equal the package directory name using a case-insensitive Windows
  comparison.
- `description` is user-facing text shown by the CLI and Admin UI.
- `powershell` is required and must be either `pwsh` or `powershell`. DevWT
  never silently chooses one.

Names must be safe single path segments. They cannot contain directory
separators, traversal components, control characters, or shell syntax.
Package files are resolved beneath the discovered package root, and a package
cannot redirect DevWT to another script or instruction path.

### Shared Environments

Shared packages live in the current worktree:

```text
<worktree>/.devwt/environments/<environment-name>/
```

They are normal repository files and may be intentionally committed. A branch
may add, change, or remove a shared environment. Discovery and setup always use
the package from the target worktree, not the primary checkout or another
worktree.

The package's `AGENTS.md` is source material and is allowed in Git. It is not
the generated file protected by DevWT.

### Local Environments

Local packages live under the configured DevWT state root:

```text
<DEVWT_STATE_ROOT>/environments/<repository-id>/<environment-name>/
```

They are associated with the stable DevWT repository ID and are never placed
in a worktree. They therefore cannot be staged or committed.

The service creates local package directories through an explicit CLI or Admin
UI action. It grants the invoking user's SID `Modify` access and keeps
`SYSTEM` and the local Administrators group at `FullControl`; the feature does
not grant another principal write access. Installer and package-creation tests
must verify the resulting Windows ACL rather than relying on inherited
`ProgramData` permissions.

### Discovery And Name Conflicts

DevWT merges local and shared discovery results for display but does not apply
implicit source precedence.

If an environment name exists in only one source, this is sufficient:

```powershell
devwt environment setup full
```

If the same name exists in both sources, the command fails before mutation and
requires an explicit source:

```powershell
devwt environment setup full --source local
devwt environment setup full --source shared
```

The same ambiguity rule applies to open, remove, and any other command that
targets an existing package by name.

## Persisted Model

Environment state is stored separately from `repos.json`, `contexts.json`,
`routing.json`, and `runtime.json`. Existing state formats and their loading
behavior remain unchanged.

The environment state records:

- package registrations needed for machine-local packages;
- the selected repository ID, context ID, package name, and package source for
  each worktree binding;
- whether the most recent application included script, agents, or both;
- the package fingerprint and agent-source fingerprints used by that
  application;
- current status: `Pending`, `Running`, `Succeeded`, `Failed`, or `Canceled`;
- run ID, start time, completion time, duration, PowerShell host, and exit code;
- the last generated override fingerprint and ownership information.

Run logs are separate files below an environment-run state directory. Removing
or clearing a binding does not alter the existing DevWT context or its routing
configuration.

Only one environment may be selected for a worktree at a time. Different
worktrees, including worktrees from the same repository, may select different
environments.

## Setup Command And Modes

The primary command is:

```powershell
devwt environment setup <name> [--source local|shared]
```

By default it applies both the agent instructions and the script.

The two partial modes are:

```powershell
devwt environment setup <name> --script-only
devwt environment setup <name> --agents-only
```

`--script-only` and `--agents-only` are mutually exclusive. Both partial modes
still bind the selected environment to the worktree and record which component
was applied:

- `--script-only` does not inspect, create, replace, or remove the current
  `AGENTS.override.md`.
- `--agents-only` does not resolve or start a PowerShell executable and does not
  create a script run.

The complete operation applies agents before starting the script. If agent
application fails, the script does not run. If the script subsequently fails,
the environment binding and generated agent file remain in place so their
instructions and diagnostics are available during recovery.

Setup is never triggered by `post-checkout`, `worktree-ready`, the periodic
worktree synchronizer, package discovery, repository registration, or merely
opening the Admin UI.

## Setup Preflight And Concurrency

Before changing a file or persisted state, DevWT validates:

1. the requested directory belongs to a registered DevWT repository and
   worktree context;
2. package discovery resolves one unambiguous local or shared package;
3. the manifest schema, name, required files, and package boundaries are valid;
4. the selected `pwsh` or `powershell` executable is available when the script
   component will run;
5. an agent update will not overwrite a tracked or unmanaged
   `AGENTS.override.md`;
6. no other environment setup currently holds the target worktree lease.

Package fingerprints are recomputed as part of preflight. The run plan retains
those fingerprints so a package changed between validation and execution is
rejected instead of executing different content.

Only one setup operation may run for a worktree. Setup operations for different
worktrees may run concurrently. There is no implicit retry or timeout. The
setup script owns its internal sequencing, parallelism, idempotency, and
timeouts.

## User-Context Script Execution

The installed DevWT Windows service runs as `LocalSystem`. It must not execute
repository setup scripts because that would change the effective user,
credentials, profile, PATH, package-manager configuration, mapped resources,
and other environment-dependent behavior.

The service owns validation, state transitions, bindings, agent materialization,
run leases, and bounded log persistence. The interactive `devwt` CLI process
owns script execution under the invoking user's Windows identity.

The flow is:

1. The CLI asks the service to preflight and reserve an immutable setup run.
2. The service applies the requested agent component, persists the binding, and
   returns a run plan when a script component remains.
3. The CLI verifies the returned plan and starts the selected PowerShell host.
4. The CLI mirrors stdout and stderr to the terminal while forwarding bounded
   log data and state updates to the service.
5. The CLI reports the final exit code, cancellation, or launch failure.
6. The service closes the run lease and persists the terminal state.

The PowerShell process starts in the worktree root:

```powershell
<pwsh|powershell> -NoProfile -ExecutionPolicy Bypass -File <setup.ps1>
```

DevWT passes no positional script parameters. It supplies these environment
variables:

```text
DEVWT_ENVIRONMENT_NAME
DEVWT_ENVIRONMENT_SOURCE
DEVWT_PROFILE_ROOT
DEVWT_REPOSITORY_ROOT
DEVWT_WORKTREE_PATH
DEVWT_CONTEXT_ID
```

`DEVWT_PROFILE_ROOT` points to the selected package directory, allowing the
script to load additional package-owned resources deliberately. The caller's
normal environment is otherwise inherited.

`Ctrl+C` cancels the operation, terminates the setup process tree, and records
`Canceled`. A non-zero script exit code records `Failed`. An exit code of zero
records `Succeeded`. In every case the selected environment remains bound.

## Agent Instruction Materialization

Codex loads at most one instruction file per directory and prefers
`AGENTS.override.md` over `AGENTS.md`. DevWT therefore generates a synthesized
worktree-root override rather than placing the environment source file beside
the repository instructions.

The generated file contains, in order:

1. a DevWT ownership header with a format version, repository ID, context ID,
   selected environment, source, and source fingerprints;
2. the current worktree-root `AGENTS.md`, when present;
3. a clearly delimited environment section containing the selected package's
   `AGENTS.md`.

The environment section comes last so its environment-specific directions have
the strongest ordering within the synthesized root instruction file. Nested
repository instruction files remain discoverable normally when Codex works
below their directories.

Generation uses a temporary file and atomic replacement. DevWT refuses to
replace:

- a tracked `AGENTS.override.md`;
- a file without a valid DevWT ownership header;
- a previously generated file whose current content no longer matches its
  recorded generated fingerprint.

In those cases setup stops and offers an explicit import workflow. Import copies
the existing user content into the selected package's `AGENTS.md`; it does not
silently discard or merge user changes.

When the repository `AGENTS.md`, environment `AGENTS.md`, or binding changes,
the binding reports `Stale`. DevWT does not rewrite the file automatically.
The user refreshes it explicitly:

```powershell
devwt environment setup <name> --agents-only
```

Codex discovers instruction files once at task/session startup. After applying
or changing the generated override, CLI and Admin UI must state that an already
running Codex task needs to be restarted before the new instructions take
effect.

## Git Protection

DevWT protects the generated worktree-root `AGENTS.override.md`, not the
environment package's source `AGENTS.md`.

Protection has three layers:

1. Add the exact root-relative generated path to the repository's local Git
   exclude configuration without editing tracked `.gitignore` files.
2. Add a marked, upgradable DevWT block to the repository's `pre-commit` hook
   while preserving unrelated hook content.
3. Put a visible generated-file warning and ownership marker in the file.

Normal `git add` and `git add -A` therefore ignore the generated file. If a user
uses `git add -f`, the pre-commit block inspects the staged
`AGENTS.override.md` content and rejects the commit only when the staged blob
contains the DevWT ownership marker. A legitimate repository-owned override
without that marker is not globally blocked.

The guard is intended to prevent accidental commits. It cannot and does not
claim to prevent deliberate bypass through `git commit --no-verify`, marker
removal, direct index manipulation, or plumbing commands.

If the file is already staged when setup begins, DevWT fails with recovery
instructions. It does not silently unstage user state. Clearing or switching an
environment removes or replaces the generated file only when both its ownership
marker and recorded fingerprint match.

## Admin UI And Custom URI Launch

Repository settings gain an Environments section showing:

- local and shared packages with source badges;
- validation errors and local/shared name conflicts;
- the environment selected by each worktree;
- component application and stale status;
- the latest run state, duration, host, exit code, and log;
- actions to add, open, set up, clear, and remove environments.

Setup offers `Setup`, `Script only`, and `Agents only` actions. Package files
open in the user's normal editor rather than a large embedded Web editor.

The Web UI is hosted by the service and cannot safely execute setup as the
interactive user. Installer support therefore registers a `devwt://` custom URI
handler that launches the installed CLI in a visible terminal under the current
user.

The URI contains only a short-lived, single-use, opaque job ID. It never embeds
a command, environment name, repository path, worktree path, script path, or
shell fragment. The CLI redeems the job through the local control channel,
displays the resolved repository, worktree, source, environment, and mode, and
requires confirmation before a mutating action. It then uses the normal setup
flow. Expired, replayed, malformed, declined, or context-mismatched jobs fail
without starting a process or changing state.

The same launcher pattern is used for UI operations that must modify or open
user-owned package files. Installer update and uninstall flows add, replace,
and remove the custom URI registration safely.

## CLI Surface

The environment command group is:

```text
environment list
environment add <name> --source <local|shared> --powershell <pwsh|powershell>
environment open <name> [--source <local|shared>]
environment setup <name> [--source <local|shared>] [--script-only|--agents-only]
environment status
environment logs [--run <id>]
environment clear
environment remove <name> --source <local|shared>
```

Every command accepts `--worktree <path>`. Without it, DevWT resolves the
current directory's longest registered worktree match. Commands never fall back
to an unrelated registered context.

`add` creates a valid editable package template. `open` opens the package
directory. `clear` removes the worktree binding and removes the generated
override only when safe; it does not delete a package. `remove` deletes a
package and requires explicit confirmation. Removing a shared package is a
normal working-tree deletion that the user may intentionally commit.

## Logs And Retention

Script output is streamed to the interactive terminal without a DevWT-imposed
output limit. Persisted logs use these approved bounds:

- retain the most recent 10 runs per worktree;
- retain the most recent 10 MiB of combined output per run;
- when output exceeds the per-run limit, discard older persisted output and add
  an explicit truncation marker;
- remove older run metadata and log files together.

Each persisted output frame records a monotonically increasing sequence number,
UTC timestamp, and `stdout` or `stderr` stream identity. Run metadata is stored
with the frames, but the script body is never copied into a log.

DevWT does not attempt heuristic secret redaction because it cannot do so
reliably. Documentation and generated templates warn scripts not to print
passwords, tokens, connection strings, or other secrets. Logs remain local
DevWT state and are not copied into the repository.

## Error Handling

- An unregistered repository or unresolved worktree fails before package
  discovery.
- A missing, ambiguous, invalid, or changed package fails before binding.
- A missing selected PowerShell host fails before agent application in complete
  or script-only mode.
- Agent ownership conflicts fail before script execution.
- A script launch failure, non-zero exit, or cancellation preserves the binding
  and any successfully generated override.
- A failed state/log update is surfaced by the CLI; it is not reported as a
  successful setup.
- A stale binding is visible but never refreshed automatically.
- Clearing an altered generated file preserves the file and returns a warning
  instead of deleting user content.
- Package removal does not delete run history for other packages or worktrees.
- A custom URI failure never falls back to executing raw URI text.

## Testing

### Core Tests

- manifest serialization, schema validation, required files, safe names, and
  package-boundary enforcement;
- local/shared discovery and explicit-source conflict handling;
- parser coverage for all commands and mutually exclusive setup modes;
- binding and run-state serialization without changing existing state files;
- deterministic package and agent-source fingerprints;
- agent synthesis order, ownership parsing, atomic replacement, stale
  detection, and unmanaged-file refusal;
- log ring behavior for 10 runs and the most recent 10 MiB.

### Service Tests

- repository/context resolution and worktree-scoped setup leases;
- preflight-before-mutation behavior;
- complete, script-only, and agents-only state transitions;
- success, non-zero exit, launch failure, and cancellation completion;
- preservation of bindings and agents after script failure;
- safe clear, switch, import, and remove behavior;
- local package directory ACL creation;
- short-lived single-use URI jobs, replay rejection, and context validation;
- isolation between repositories, worktrees, packages, and concurrent runs.

### CLI And Runner Tests

- exact PowerShell executable selection with no fallback;
- working directory and all documented environment variables;
- inherited caller environment and absence of service-account execution;
- live stdout/stderr forwarding, bounded persistence, exit codes, and
  cancellation of the process tree;
- safe redemption of opaque UI jobs;
- clear errors for missing executables and changed run plans.

### Git Integration Tests

Using temporary real repositories and linked worktrees:

- preserve existing `post-checkout` and `pre-commit` hook content;
- install and upgrade exactly one marked DevWT guard block;
- exclude only the generated root override from normal add operations;
- verify `git add -A` does not stage the generated override;
- verify forced staging is blocked when the DevWT ownership marker remains;
- allow shared package source files to stage normally;
- refuse tracked and unmanaged root overrides;
- preserve manually altered generated files during switch and clear.

### Admin UI And Installer Tests

- environment list, source badges, conflicts, selected worktree, modes, states,
  logs, and destructive confirmations;
- action mapping uses opaque job IDs and never serializes commands or paths into
  the URI;
- desktop and 390-pixel-wide layout without horizontal overflow;
- custom URI registration, update, and uninstall behavior;
- package editor launch and visible user terminal startup.

### End-To-End Verification

After implementation:

1. Build and run the Core and Service test suites.
2. Install or update the managed DevWT build and restart the backend because the
   feature changes C# service and CLI behavior.
3. Verify service startup, CLI help, control channel, and Admin UI HTTP status.
4. Register a temporary repository with a linked worktree.
5. Exercise one shared and one local environment, including a name conflict.
6. Verify complete, script-only, agents-only, failed, canceled, stale, clear,
   import, and remove flows.
7. Confirm the setup process runs as the interactive user and receives the
   documented working directory and environment values.
8. Confirm Git protection with normal add, forced add, commit, and an unrelated
   existing hook.
9. Verify Admin UI actions, terminal launch, logs, and mobile-width rendering in
   a real browser with no console errors.

## Completion Criteria

The feature is complete when a user can create either a local or shared package,
manually apply it to any registered worktree from CLI or Admin UI, run its
PowerShell setup under the correct user identity, receive its environment
instructions in a protected generated override, diagnose failures from bounded
logs, and remove or switch it without overwriting user files or staging the
generated instructions accidentally.
