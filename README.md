# FolderSync

[![CI](https://github.com/FilipDlouhy/folder-sync/actions/workflows/ci.yml/badge.svg)](https://github.com/FilipDlouhy/folder-sync/actions/workflows/ci.yml)

Console app that keeps a **replica** folder identical to a **source** folder.
Synchronization is one-way and periodic: every cycle the replica is changed to match the source exactly,
and every create, copy, update and delete is logged to the console and to a log file.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows or Linux (CI runs the tests on both)

## Usage

```bash
dotnet run --project src/FolderSync -- --source ./data/source --replica ./data/replica --interval 30 --log ./logs/sync.log
```

| Option | Description |
|---|---|
| `--source` | Folder to synchronize from. Must exist. |
| `--replica` | Folder kept identical to the source. Created if missing. |
| `--interval` | Seconds between synchronizations (1–86400). |
| `--log` | Log file path. Created if missing, appended otherwise. |

All options are required. The first sync runs immediately, then every `--interval` seconds. Stop with **Ctrl+C**.

Invalid input is reported all at once and the app exits with code `1`, for example when the source does not exist,
the interval is out of range, source and replica overlap (also through a link or junction), or the log file is inside one of the synced folders.
The log file is also opened once at startup; if it cannot be written, the app exits with code `1` instead of running without a file log.

Example output:

```text
2026-11-08 16:22:43.246 [INF] Starting sync C:\data\source -> C:\data\replica every 30 s, log file C:\logs\sync.log. Press Ctrl+C to stop.
2026-11-08 16:22:43.268 [INF] Created replica folder C:\data\replica
2026-11-08 16:22:43.276 [INF] Created directory sub
2026-11-08 16:22:43.279 [INF] Copied new file a.txt
2026-11-08 16:22:43.280 [INF] Copied new file sub\b.txt
2026-11-08 16:22:43.281 [INF] Sync #1 done in 14 ms: copied 2, updated 0, deleted 0, new folders 1, failed 0
2026-11-08 16:23:13.275 [INF] Updated file a.txt
2026-11-08 16:23:13.275 [INF] Sync #2 done in 1 ms: copied 0, updated 1, deleted 0, new folders 0, failed 0
```

## How it works

Every cycle runs three steps:

1. **Scan**: `DirectoryScanner` walks both folders and returns a flat snapshot of relative paths, kinds and sizes.
2. **Plan**: `SyncPlanner` compares the snapshots and returns an ordered list of operations.
   It only reads the disk through `IFileComparer`, so it is easy to unit test.
3. **Execute**: `SyncExecutor` applies the operations to the replica and logs each one.

`Synchronizer` runs one cycle and `SyncLoop` repeats it on a `PeriodicTimer`.

## Testing approach

```bash
dotnet test
```

65 NUnit tests in three layers:

- **Unit** (`SyncPlannerTests`, `CommandLineTests`): the planner on in-memory snapshots with a fake comparer (no disk access),
  plus argument validation and parsing on temporary folders. Each test states one rule
  (operation order, file/folder swap, links, overlapping paths also through links, interval bounds).
- **Integration** (`SynchronizerTests`): one real sync cycle on temporary folders. Scenarios were picked where
  sync tools usually break: read-only and hidden files and folders, locked files in the source and in the replica,
  unreadable folders in the source and in the replica, links in the source and in the replica (also one that cannot be deleted),
  case-only renames, same size and timestamp but different content,
  a missing source, cancellation and 2,000 files. One test tampers with the replica (changed file, extra file,
  a link pointing back into the source) and checks that the source stays byte-for-byte the same.
- **End-to-end** (`EndToEndTests`): starts the real app as a process and checks periodic sync, console and file logging,
  exit codes for invalid arguments and an unwritable log file, and on Linux `SIGINT`/`SIGTERM` sent while a 200 MB file is being copied.

Principles:

- No fixed sleeps. Integration tests call one cycle directly; end-to-end tests poll the log with a timeout.
- Every test gets its own temporary folder, removed afterwards.
- CI runs the suite on Windows and Linux. Tests the OS cannot reproduce are skipped with a reason
  (file locks, folder attributes and case-insensitive paths on Linux; `kill` signals and Unix permissions on Windows;
  permission tests when running as root).
- Tests for fixed bugs were checked against the bug: with the fix reverted they fail
  (links in the replica, a replica link over the source, a replica link that cannot be deleted, read-only and unreadable
  replica folders, a stop signal during the first sync).

Known gaps:

- Ctrl+C on Windows is not in the suite: a console control event can only be sent by attaching to the app's console.
  It was verified with a separate script (Ctrl+C during a 1.5 GB copy: file finished, `Synchronization stopped`, exit code `0`).
- No performance tests in the suite and no code coverage report; scenarios were chosen by risk instead.
  Measured manually: 20,000 small files take about 15 s for the first sync on Windows (3 s on Linux),
  then 8 s (0.7 s on Linux) per cycle, mostly hashing. Cycles never overlap; a slow cycle delays the next tick.
