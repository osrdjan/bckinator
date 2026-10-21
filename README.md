# bckinator

A Windows desktop backup utility built with C# .NET 10, WPF, and MVVM. The Workspace layout keeps reusable profiles on the left and Profile, Rules, Preview, History, and Settings tabs on the right. Runtime projects use only built-in .NET/WPF libraries; 7-Zip runs as a separate installed executable.

## Build and run

Requirements: Windows, the .NET 10 SDK, and installed **64-bit 7-Zip** for archive backups and archive integration tests. Folder backups do not require 7-Zip.

```powershell
dotnet build Bckinator.slnx
dotnet test Bckinator.slnx
dotnet run --project src/Bckinator.App
```

Set the executable location in **Settings** if `C:\Program Files\7-Zip\7z.exe` is not correct. You can use Visual Studio with the .NET desktop development workload or another editor with the .NET SDK. No administrator access is needed to run the app.

Publish a portable, self-contained x64 folder:

```powershell
dotnet publish src/Bckinator.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

Run `artifacts/publish/win-x64/Bckinator.App.exe`. Keep the publish folder together. 7-Zip is configured separately and is not bundled.

## Using Workspace

1. Create a profile or duplicate the bundled WOC example. The example comes from `samples/woc.json`; its paths and selection rules are data, not execution logic. Nothing runs automatically.
2. Choose absolute source and destination paths, Folder or SevenZip output, and a naming pattern such as `project_{yyyyMMdd_HHmmss}`. Date/time expressions use .NET formatting and the run's local start time. Archives contain a top-level folder with that same generated name.
3. Configure global rules and directory-specific include-only scopes. Select a scope row to edit its included paths, one per line. Directory literals such as `diagrams/` include their eligible descendants.
4. Save the profile. Edits are explicit; the application asks before discarding unsaved changes. Start and Preview use the currently displayed settings, including unsaved edits, through an isolated snapshot.
5. Use Preview to inspect relative paths, inclusion decisions, reasons, included file count, and uncompressed size. Filter by decision, path, or reason. Changing settings clears the old preview. Excluded directory trees are represented by their directory row and are not traversed; this avoids unnecessary access and expensive enumeration.
6. Start Backup. One preview or backup runs at a time. Controls that could change a running operation are disabled; Cancel remains available. History includes output paths, status, duration, size, messages, and links to logs and retained staging.

## Selection semantics

Matching is case-insensitive and relative to the source. Both `/` and `\` separators are accepted. `*` matches within one path segment, `?` matches one non-separator character, and `**` matches across directories. A `**/` prefix also matches files directly in the current directory. Character classes and gitignore negation are not supported.

| Match type | Example | Behavior |
| --- | --- | --- |
| DirectoryName | `.venv` | Matches directories at any depth and their descendants |
| FilePattern | `*.exe` | Matches filenames at any depth |
| RelativePath | `build/output` | Matches that relative path and descendants |
| PathGlob | `src/**/temp/*.json` | Matches paths relative to the source |

**Exclusions always win.** A global Include never overrides an exclusion. Global Include rules restrict selection only when **Include only items matching enabled global Include rules** is checked; otherwise everything not excluded is initially eligible. An empty enabled global include list in that mode selects no files.

Each enabled directory scope further restricts eligible paths inside that directory. Nested scopes intersect. Scopes do not select otherwise excluded paths. Rule reordering changes presentation only; it does not change precedence. An exclusion cannot be accidentally bypassed by moving an Include rule below it.

Symbolic links and junctions are excluded by default. If this is unchecked, only targets resolving inside the source are followed; external targets and cycles are skipped with warnings. Source and destination roots and their ancestors must not themselves pass through junctions or symbolic links. Destinations inside the source are rejected, including drive-root cases.

## Execution and recovery

Each run is an independent full backup. The source is opened for reading and is never written or deleted. Selected files are copied into a unique `.bckinator-staging-<run-id>` directory under the destination. Ordinary file and directory modification timestamps are preserved where practical. Empty eligible directories are preserved when at least one eligible file exists.

Copied files are verified with SHA-256. A 7z backup then compresses that staged root and runs `7z t`; any nonzero 7-Zip exit code prevents final publication. The archive uses a unique `.partial.7z` name until verification succeeds. Folder backups are renamed from their verified staging root. Existing outputs and manifest sidecars are never overwritten.

Successful runs produce a `<output>.manifest.json` sidecar with file sizes, modification timestamps, SHA-256 hashes, and warnings. A successful manifest move is followed by cleanup of only that run's staging directory. Cleanup failures appear as warnings. Failures and cancellations retain staging and partial archives for inspection; **History → Open retained staging** locates them. After inspecting them, remove unwanted data yourself. Partial archives are not complete backups.

The default error policy fails the run. Opting into **Continue with warnings** allows unreadable or uncopyable items to be skipped; a successful result is then **CompletedWithWarnings**, and skipped items are listed in the log and manifest. Source files changed between scanning and copying are treated as errors. An empty selection, or a run with no successfully copied files, is rejected.

Disk-space checks conservatively allow the uncompressed total plus an archive of that size, with a small overhead allowance. They are estimates, not reservations; UNC free space is not prechecked, and writes can still fail if capacity changes. Failed writes retain the staging data.

## Application data and profile exchange

The default data directory is `%LOCALAPPDATA%\Bckinator`:

- `profiles.json`: saved profiles in a versioned envelope.
- `settings.json`: configured 7-Zip executable location.
- `history.json`: run results.
- `runs/<id>.log`: persistent run logs. The on-screen viewer keeps the most recent 2,000 lines.

Configuration writes use a same-directory temporary file, flush it, and replace the previous file atomically. Unsupported schemas and malformed documents are reported; existing files are preserved. Startup does not silently reset invalid configuration. One application instance is allowed per data directory.

Import/export handles one profile per versioned JSON document. Import assigns a fresh identity. `docs/profile.schema.json` describes the exported v1 format; application validation also checks path safety and selection/compression settings. The schema is documentation for external tools; no additional schema-validation dependency is loaded at runtime.

An isolated data directory is useful for development and UI verification:

```powershell
dotnet run --project src/Bckinator.App -- --data-dir D:\scratch\bckinator-config
```

## Architecture and validation

- `Bckinator.Core`: profile models and storage, rule evaluation, scanning, naming, staging, verification, and 7-Zip process integration. It has no WPF dependency.
- `Bckinator.App`: Workspace views, view model, commands, and native folder/file dialogs. Uses the built-in Fluent theme following system appearance.
- `Bckinator.Tests`: rule, nesting, glob, serialization, naming, path-boundary, cancellation, locked-file, collision, folder, archive, and configuration tests. A WPF dispatcher test loads all tabs and exercises bound Save, Preview, and Start commands. The archive integration test requires 7-Zip at its default installed location.

The tests use disposable fixtures and check that original file contents remain unchanged. They do not run against the example profile's real paths.

## Current limits

Manual full backups only: no scheduling, incremental/differential chains, retention deletion, encryption, or automatic restore UI. Existing folders and standard 7-Zip extraction tools provide file recovery. There is no VSS snapshot: live databases, application-specific consistency, and files actively being changed require their own backup strategy. ACLs, ownership, alternate streams, hard-link identity, and special filesystem metadata are not preserved. Preview holds the enumerated rows in memory, so extremely large trees can require substantial RAM. Failed-run staging and history/log retention are managed manually.
