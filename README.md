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
6. Start Backup. One preview or backup runs at a time. Controls that could change a running operation are disabled; Cancel remains available. History includes output paths, status, duration, size, messages, and links to logs and retained work files.

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

Each run is an independent full backup. Source file contents are opened for reading and are never written or deleted. There are two execution paths:

- **7-Zip archives:** selected files are hashed during preflight, then the installed `7z.exe` compresses an explicit literal file list directly from the source. No uncompressed payload copy is written. A `.bckinator-work-<run-id>` directory contains only file lists and manifest metadata. A temporary directory symlink or junction gives the source the generated archive root name without copying file data. The alias is detached on success, failure, or cancellation and is never recursively deleted. Source link restrictions continue to apply to actual source paths.
- **Folder backups:** selected files are copied into `.bckinator-staging-<run-id>` under the destination, verified with SHA-256, and renamed to their final output folder. This temporary name prevents partially copied folders from looking complete.

When a source alias is unavailable (for example, some network sources without symbolic-link privileges), direct compression still runs from the source. The application then prefixes the archived paths with the generated root by renaming entries. **This fallback rewrites the compressed archive once**, requires space for that extra compressed copy, and is explicitly logged. Local NTFS sources normally use a junction without administrator privileges and avoid this rewrite. No fallback stages uncompressed source data.

Archives keep a unique `.partial.7z` name while being built. Verification checks the exact archived file and empty-directory selection, runs `7z t`, and extracts file contents through a memory pipe to compare their SHA-256 hashes against preflight hashes. No verification extraction is written to disk. Any missing, extra, changed, or corrupt content prevents publication. Existing outputs and manifest sidecars are never overwritten.

Empty eligible directory leaves are included explicitly, with their children excluded so newly created files cannot be pulled in by recursive directory processing. Nonempty archive parent folders are represented by their file paths; file modification timestamps are preserved, while implicit parent-folder timestamps may be recreated during extraction. Folder backups also preserve ordinary directory modification timestamps where practical. An empty file selection is rejected.

Successful runs produce a `<output>.manifest.json` sidecar with sizes, timestamps, SHA-256 hashes matching the verified backup content, and warnings. Work files are cleaned only after verification and publication; cleanup refuses to follow links. Failed and cancelled archives retain metadata work files and any partial archive, rather than an uncompressed source copy. Failed folder runs retain their copied staging data. **History → Open work files** locates the retained directory. Partial archives are not complete backups. Old history records remain compatible; the JSON field `retainedStagingPath` now also represents archive metadata work directories.

The default error policy fails the run. **Continue with warnings** can skip unreadable items during scanning or archive preflight, and failed copies for folder output. Successful partial selections are marked **CompletedWithWarnings**, and skipped items are recorded. Once direct compression starts, source changes, missing files, or any nonzero 7-Zip result fail the run instead of publishing an archive with unverifiable contents. There is no VSS snapshot; keep source files stable for the duration of the operation.

Disk-space estimates for direct archives allow roughly the selected uncompressed size plus overhead for a worst-case compressed output, without reserving space for an additional uncompressed staging copy. The root-renaming fallback checks space for another compressed archive. These are estimates, not reservations; UNC free space is not prechecked, and writes can still fail if capacity changes. Tested with installed 7-Zip 26.05; direct mode requires support for literal file lists, `-spm`, and `-sse`.

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

- `Bckinator.Core`: profile models and storage, rule evaluation, scanning, naming, direct archive streaming, folder staging, verification, and 7-Zip process integration. It has no WPF dependency.
- `Bckinator.App`: Workspace views, view model, commands, and native folder/file dialogs. Uses the built-in Fluent theme following system appearance.
- `Bckinator.Tests`: rule, nesting, glob, serialization, naming, path-boundary, cancellation, locked-file, collision, folder, archive, and configuration tests. Direct archive tests cover metadata-only work files, alias cleanup, root-renaming fallback, exact selection, late-added directory contents, source mutation with unchanged size/timestamp, and streamed SHA-256 verification for solid and non-solid archives. A WPF dispatcher test loads all tabs and exercises bound Save, Preview, and Start commands. Archive integration tests require 7-Zip at its default installed location.

The tests use disposable fixtures and check that original file contents remain unchanged. They do not run against the example profile's real paths.

## Current limits

Manual full backups only: no scheduling, incremental/differential chains, retention deletion, encryption, or automatic restore UI. Existing folders and standard 7-Zip extraction tools provide file recovery. There is no VSS snapshot: live databases, application-specific consistency, and files actively being changed require their own backup strategy. ACLs, ownership, alternate streams, hard-link identity, and special filesystem metadata are not preserved. Preview holds the enumerated rows in memory, so extremely large trees can require substantial RAM. Failed-run work files, folder staging, partial archives, and history/log retention are managed manually.
