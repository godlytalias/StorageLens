# Storage Lens

Storage Lens is a packaged C# WinUI 3 application for visualizing storage usage, tracing folder trees, classifying large items, and reviewing cleanup suggestions.

Storage indexes and preferences stay on the local device. User-authorized cleanup moves eligible files to the Windows Recycle Bin; Storage Lens does not provide a permanent-delete path.

## Included UI

- Storage overview with clickable category, recommendation, and largest-item drill-down.
- Fast-growth callouts for folders at a configurable minimum size (1 GB by default).
- Storage explorer with drive and folder selection, parent/child navigation, category filtering, search, sorting, and item details.
- Cleanup review with explicit safety levels, formatted item counts, selection totals, and a Recycle Bin confirmation dialog.
- Exact, paged candidate-file review for each cleanup recommendation with bounded loading and visible error states.
- Dedicated Help page describing navigation, safety, growth alerts, and automation.
- WinAppSDK timer background task for configurable drive/free-space and user-folder growth notifications.
- Opt-in WinAppSDK maintenance and user-away tasks that refresh every available drive on a configurable cadence from 1 to 7 days when Windows is idle.
- Persisted daily, weekly, biweekly, or monthly cleanup-review schedules with category selection.
- Settings for theme, scan preferences, cleanup safety, and app information.
- Desktop Acrylic at the window and tile levels, Light/Dark/System themes, responsive Explorer filters, and automatic right-to-left handling.
- Complete localization packs for English, Hindi, Simplified Chinese, French, Spanish, Arabic, Hebrew, Japanese, and Korean.
- A custom Storage Lens package icon with multi-resolution 32-bit ICO and target-size tile assets.

## Requirements

- Windows 11 22000 or later
- .NET 8 SDK
- Windows App SDK 2.6.5
- Visual Studio with WinUI 3 / MSIX tooling, or the .NET CLI

Open `StorageLens.sln` in Visual Studio to build the app, test project, and the
separate MSIX packaging project together. The solution and packaging project
support x86, x64, and ARM64. Use Visual Studio MSBuild for the solution or the
`.wapproj` because the packaging targets are supplied by Visual Studio's
Desktop Bridge workload.

Set `StorageLens.Package` as the startup project for package debugging. To
create a Store package, right-click that packaging project and use
**Publish > Create App Packages**. `StorageLens.csproj` is the application
project and is intentionally not an independent MSIX packaging head.

To build an unsigned x64 sideload package from a Visual Studio Developer
PowerShell:

```powershell
msbuild .\StorageLens.Package\StorageLens.Package.wapproj `
  -restore -t:Build `
  -p:Configuration=Release `
  -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxBundle=Never `
  -p:UapAppxPackageBuildMode=SideloadOnly
```

The package is written under `StorageLens.Package\AppPackages`. It is unsigned
for local development; signing and Store submission metadata remain deployment
configuration rather than repository defaults.

## Test

```powershell
dotnet test .\StorageLens.Tests\StorageLens.Tests.csproj -c Debug -r win-x64
```

## Safety

Background monitoring reads drive free space and durable folder-growth baselines. Folder growth compares each current folder with the first time it appeared in a completed index, removes deleted-folder baselines, and starts fresh when a folder is recreated. Parent rollups are suppressed when a significant share of their growth belongs to visible child folders. Idle indexing uses maintenance and user-away triggers plus internal cadence checks and a cross-process scan lock, so first indexes run at the next idle opportunity and normal refreshes do not run too often. Scheduled review runs send a notification that opens the exact selected policies. Optional automatic schedules are limited to explicitly selected Temporary app files, revalidate every file, and move only eligible items to the Recycle Bin.

## Local index and recovery

Storage Lens stores a cancellable, batched SQLite index in the package LocalState folder. New scans use a normalized, per-volume schema with 128-bit file identities, allocated and logical sizes, persisted pending work items, and a typed issue ledger. Completed queue items are removed as each directory commits. Interrupted scans retain durable partial work and resume with idempotent row writes. USN journal checkpoints make the normal scan action incremental, while ambiguous, wrapped, inaccessible, or structural changes fall back to a verification scan. Category and cleanup summaries are cached per scan, while bounded sample-first duplicate hashing resumes during idle indexing. Schema-9 keeps durable first-seen folder baselines independently of retained sample history, adds foreign-key and allocated-size indexes, and uses small, resumable history-prune batches so stale data does not become uninterruptible work. Storage maintenance keeps one completed snapshot per drive, removes abandoned duplicate snapshots, uses incremental vacuum for new indexes, and performs a guarded full compaction when reclaimable pages are substantial. Package-version-aware instance coordination prevents an older running version from receiving activations after an update.

Files are revalidated by size, timestamp, Windows file identity, and (for duplicates) SHA-256 content before cleanup. Hard-link aliases and protected/reparse/cloud-only items are not cleanup candidates. Pending cleanup records are reconciled after an interrupted app process so audit history is never retried as a destructive operation. **Settings > Local storage index** reports allocated database and WAL space, runs SQLite integrity and foreign-key health checks, and provides an explicit compaction action. Routine background maintenance uses bounded WAL checkpoints; it does not run a daily full-database vacuum.
