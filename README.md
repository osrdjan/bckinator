# bckinator

A Windows desktop backup utility built with C# .NET 10, WPF, and MVVM. The Workspace layout keeps reusable profiles on the left and Profile, Rules, Preview, History, and Settings tabs on the right. Runtime projects use only built-in .NET/WPF libraries; 7-Zip and WinRAR run as separate installed executables.

## Build and run

Requirements: Windows and the .NET 10 SDK. Install **64-bit 7-Zip** for `.7z` output or **WinRAR** for `.rar` output. Each format uses only its own archiver, including verification. Folder backups require neither. WinRAR is separately licensed and is not bundled.

```powershell
dotnet build Bckinator.slnx
dotnet test Bckinator.slnx
dotnet run --project src/Bckinator.App
```

Configure executable locations in **Settings**. Defaults are `C:\Program Files\7-Zip\7z.exe` and `C:\Program Files\WinRAR\Rar.exe`. Use the console `Rar.exe` shipped with WinRAR. You can use Visual Studio with the .NET desktop development workload or another editor with the .NET SDK. No administrator access is needed to run the app.

Publish a portable, self-contained x64 folder:

```powershell
dotnet publish src/Bckinator.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

Run `artifacts/publish/win-x64/Bckinator.App.exe`. Keep the publish folder together. Both archivers are configured separately and are not bundled.

## Using Workspace

1. Create a profile or duplicate the bundled WOC example. The example comes from `samples/woc.json`; its paths and selection rules are data, not execution logic. Nothing runs automatically.
2. Choose absolute source and destination paths, Folder, 7-Zip (.7z), or WinRAR (.rar) output, and a naming pattern such as `project_{yyyyMMdd_HHmmss}`. Date/time expressions use .NET formatting and the run's local start time. Archives contain a top-level folder with that same generated name.
   WinRAR defaults to **RAR5**, **Best** compression (`-m5`), a **256 MB dictionary**, **solid compression on**, a **5% recovery record**, **long range search always on**, and **BLAKE2 checksums**. Change these options per profile. Existing profiles retain their explicitly saved overrides; click **Use RAR defaults**, then **Save profile**, to apply the new defaults. Best is WinRAR's slowest, most compressive method; actual size depends on the input data. Existing 7-Zip settings remain intact when switching formats.
3. Configure global rules and directory-specific include-only scopes. Select a scope row to edit its included paths, one per line. Directory literals such as `diagrams/` include their eligible descendants.
4. Save the profile. Edits are explicit; the application asks before discarding unsaved changes. Start and Preview use the currently displayed settings, including unsaved edits, through an isolated snapshot.
5. Use Preview to inspect relative paths, inclusion decisions, reasons, included file count, and uncompressed size. Filter by decision, path, or reason. Changing settings clears the old preview. Excluded directory trees are represented by their directory row and are not traversed; this avoids unnecessary access and expensive enumeration.
6. Start Backup. One preview or backup runs at a time. Controls that could change a running operation are disabled; Cancel remains available. History includes output paths, status, duration, size, messages, and links to logs and retained work files.

## RAR defaults

The console command uses `a` (add and replace), `-ma5`, `-m5`, `-md256m`, `-s`, `-rr5p`, `-t`, `-mcl+`, `-mcd`, `-mce`, `-mcx-`, `-htb`, `-qo`, and `-oi0`. Recovery percentage, method, dictionary, solid mode, long range search, and BLAKE2 are profile settings. RAR can reduce the stored dictionary for small inputs. Long range search applies to Fast through Best methods. Use WinRAR/RAR 7 or newer for these compression controls.

| Setting | Default |
| --- | --- |
| Archive format / method / dictionary | RAR5 / Best / 256 MB |
| Solid archive | Enabled |
| Recovery record | Enabled, 5% of archive size (approximately) |
| Test archived files | Enabled; additional SHA-256 verification always runs |
| Long range search | Always on |
| Delta / executable compression | Auto / Auto |
| Exhaustive search | Disabled |
| File checksum | BLAKE2sp |
| Quick open information | Add for larger files (RAR chooses the threshold) |
| Identical files as references / original archive name and time | Disabled / Disabled |
| File security / alternate streams / symbolic links as links / hard links as links | Disabled |
| Delete after archiving / SFX / locking / volumes / encryption | Disabled |
| Update mode | Add and replace files within the new archive; completed backups are never overwritten |
| Background archiving / waiting for other copies / shutdown | Disabled; normal process priority, PC keeps running |

`-cfg-` ignores external presets and environment defaults. Disabled opt-in behaviors are omitted from the command, and password flags are omitted entirely. Archives created by the earlier bckinator RAR integration may have been encrypted with the literal password `-` because it incorrectly used `-p-`; use that password to extract those older backups. New archives are tested with independent password-free extraction.

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

Each run is an independent full backup. Source file contents are opened for reading and are never written or deleted. There are archive and folder execution paths:

- **7-Zip archives:** selected files are hashed during preflight, then the installed `7z.exe` compresses an explicit literal file list directly from the source. No uncompressed payload copy is written. A `.bckinator-work-<run-id>` directory contains only file lists and manifest metadata. A temporary directory symlink or junction gives the source the generated archive root name without copying file data. The alias is detached on success, failure, or cancellation and is never recursively deleted. Source link restrictions continue to apply to actual source paths.
- **WinRAR archives:** selected files use the same preflight and SHA-256 checks, then `Rar.exe` reads the explicit file list directly from the source. Its native archive-path prefix (`-ap`) adds the generated root folder, avoiding aliases and root-renaming rewrites. `-r-` stores listed empty directory leaves without recursively reading their contents. `-cfg-` prevents external RAR configuration from changing the operation.
- **Folder backups:** selected files are copied into `.bckinator-staging-<run-id>` under the destination, verified with SHA-256, and renamed to their final output folder. This temporary name prevents partially copied folders from looking complete.

For 7-Zip, when a source alias is unavailable (for example, some network sources without symbolic-link privileges), direct compression still runs from the source. The application then prefixes the archived paths with the generated root by renaming entries. **This fallback rewrites the compressed archive once**, requires space for that extra compressed copy, and is explicitly logged. Local NTFS sources normally use a junction without administrator privileges and avoid this rewrite. No fallback stages uncompressed source data.

Archives keep a unique `.partial.7z` or `.partial.rar` name while being built. Verification checks the exact archived file and empty-directory selection, runs the selected archiver's integrity test, and extracts file contents through a memory pipe to compare their SHA-256 hashes against preflight hashes. No verification extraction is written to disk. Any missing, extra, changed, or corrupt content prevents publication. Existing outputs and manifest sidecars are never overwritten.

Empty eligible directory leaves are included explicitly, with their children excluded so newly created files cannot be pulled in by recursive directory processing. Nonempty archive parent folders are represented by their file paths; file modification timestamps are preserved, while implicit parent-folder timestamps may be recreated during extraction. Folder backups also preserve ordinary directory modification timestamps where practical. An empty file selection is rejected.

Successful runs produce a `<output>.manifest.json` sidecar with sizes, timestamps, SHA-256 hashes matching the verified backup content, and warnings. Work files are cleaned only after verification and publication; cleanup refuses to follow links. Failed and cancelled archives retain metadata work files and any partial archive, rather than an uncompressed source copy. Failed folder runs retain their copied staging data. **History → Open work files** locates the retained directory. Partial archives are not complete backups. Old history records remain compatible; the JSON field `retainedStagingPath` now also represents archive metadata work directories.

The default error policy fails the run. **Continue with warnings** can skip unreadable items during scanning or archive preflight, and failed copies for folder output. Successful partial selections are marked **CompletedWithWarnings**, and skipped items are recorded. Once direct compression starts, source changes, missing files, or any nonzero archiver result fail the run instead of publishing an archive with unverifiable contents. There is no VSS snapshot; keep source files stable for the duration of the operation.

Disk-space estimates for direct archives allow roughly the selected uncompressed size plus overhead for a worst-case compressed output, without reserving space for an additional uncompressed staging copy. Enabled RAR recovery records add their configured percentage to the estimate. The root-renaming fallback checks space for another compressed archive. These are estimates, not reservations; UNC free space is not prechecked, and writes can still fail if capacity changes. Tested with installed 7-Zip 26.05; 7-Zip direct mode requires support for literal file lists, `-spm`, and `-sse`. RAR creation, recovery records, BLAKE2, and password-free extraction were tested with installed WinRAR console RAR 7.23.

## Application data and profile exchange

The default data directory is `%LOCALAPPDATA%\Bckinator`:

- `profiles.json`: saved profiles in a versioned envelope.
- `settings.json`: configured 7-Zip and WinRAR console executable locations.
- `history.json`: run results.
- `runs/<id>.log`: persistent run logs. The on-screen viewer keeps the most recent 2,000 lines.

Configuration writes use a same-directory temporary file, flush it, and replace the previous file atomically. Unsupported schemas and malformed documents are reported; existing files are preserved. Startup does not silently reset invalid configuration. One application instance is allowed per data directory.

Import/export handles one profile per versioned JSON document. Import assigns a fresh identity. `docs/profile.schema.json` describes the exported v1 format; application validation also checks path safety and selection/compression settings. The schema is documentation for external tools; no additional schema-validation dependency is loaded at runtime.

An isolated data directory is useful for development and UI verification:

```powershell
dotnet run --project src/Bckinator.App -- --data-dir D:\scratch\bckinator-config
```

## Architecture and validation

- `Bckinator.Core`: profile models and storage, rule evaluation, scanning, naming, direct archive streaming, folder staging, verification, and shared 7-Zip/WinRAR process integration. It has no WPF dependency.
- `Bckinator.App`: Workspace views, view model, commands, and native folder/file dialogs. Uses the built-in Fluent theme following system appearance.
- `Bckinator.Tests`: rule, nesting, glob, serialization, naming, path-boundary, cancellation, locked-file, collision, folder, archive, and configuration tests. Direct archive tests cover metadata-only work files, alias cleanup, root-renaming fallback, exact selection, late-added directory contents, source mutation with unchanged size/timestamp, and streamed SHA-256 verification for solid and non-solid archives. A WPF dispatcher test loads all tabs and exercises bound Save, Preview, and Start commands. 7-Zip integration tests require its default installed location. WinRAR integration tests run when `C:\Program Files\WinRAR\Rar.exe` is installed and are explicitly skipped otherwise. RAR tests cover Unicode and special filenames, exact selection, late-added files, source mutation, solid and non-solid streaming, collision handling, and missing executables. The dispatcher test also exercises bound RAR settings, persistence, and a real RAR backup when available.

The tests use disposable fixtures and check that original file contents remain unchanged. They do not run against the example profile's real paths.

## Current limits

Manual full backups only: no scheduling, incremental/differential chains, retention deletion, encryption, or automatic restore UI. Existing folders and standard archive extraction tools provide file recovery. There is no VSS snapshot: live databases, application-specific consistency, and files actively being changed require their own backup strategy. ACLs, ownership, alternate streams, hard-link identity, and special filesystem metadata are not preserved. Preview holds the enumerated rows in memory, so extremely large trees can require substantial RAM. Failed-run work files, folder staging, partial archives, and history/log retention are managed manually.
