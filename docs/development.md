# Development

## Project Structure

```
ReStore/
├── ReStore.Core/             # Core CLI application
│   │
│   ├── Program.cs            # CLI entry point
│   ├── src/
│   │   ├── core/             # Backup, restore, and state management
│   │   ├── storage/          # Storage provider implementations
│   │   ├── sharing/          # File sharing functionality
│   │   ├── monitoring/       # File watching and change detection
│   │   ├── utils/            # Configuration, logging, and utilities
│   │   └── backup/           # System backup functionality
│   └── config/               # Configuration files
│
├── ReStore/                  # WPF GUI application
│   │
│   ├── App.xaml              # Application entry point
│   ├── Assets/               # Icons, images, and resources
│   ├── Interop/              # Win32 interop (window backdrop, maximised bounds)
│   ├── Views/                # UI pages (Dashboard, Backups, Settings)
│   │   ├── Pages/            # Navigation pages
│   │   └── Windows/          # Dialog windows
│   └── Services/             # GUI services (config/state sharing, theme, tray, notifications)
│
└── ReStore.Tests/            # Unit and integration tests
```

## Development

The project is built with .NET 10.0 and uses:

- WPF for the GUI with the WPF-UI library
- JSON for configuration
- SHA256 hashing for change detection
- Content-defined chunking and snapshot manifests for user-file backups
- ZIP compression for system backup components

To extend storage support, implement the `IStorage` interface and register your provider in
`StorageFactory`, then add its config block to `config.example.json`.

### Line Endings

The repository ships a `.gitattributes` that normalises line endings (stored LF, checked
out CRLF for C#/XAML/project/PowerShell files). Without it, an editor rewriting the tree
as CRLF makes every tracked file appear modified, which buries real changes in a
thousands-of-lines diff. If `git status` ever shows unexpected whole-file changes, check
`git diff --ignore-cr-at-eol` — if that output is empty, the diff is purely line endings.

### Multi-Architecture Builds

Both projects declare `RuntimeIdentifiers=win-x64;win-arm64`. `ReStore.Core` is an
executable project that the WPF host references as a shared implementation assembly, so
`ReStore.csproj` sets `ValidateExecutableReferencesMatchSelfContained=false` and forwards
its RID to the reference. `build-msix.ps1` packages both architectures.

```bash
dotnet build ReStore/ReStore.csproj -r win-x64
dotnet build ReStore/ReStore.csproj -r win-arm64
```

## Testing

The repository includes an automated test project at `ReStore.Tests` (xUnit) that covers core backup/restore behavior (including encryption and fuzz/integration scenarios).

Run the full test suite:

```bash
dotnet test ReStore.Tests/ReStore.Tests.csproj
```

Or run tests for the entire solution:

```bash
dotnet test ReStore.sln
```

## Diffing Status

Two similarly-named concepts are easy to confuse:

- `ChunkSnapshot` in the live backup flow creates point-in-time manifests and reuses previously uploaded content-addressed chunks.
- `verify` is a first-class CLI command for validating a manifest, its chunks, and the reconstructed file hashes without restoring content.
- `DiffManager` is a standalone binary diff prototype that can create and apply patch blobs in isolation, but it is not used by `Backup`, `Restore`, `FileWatcher`, or any storage provider in production.

If you work on diffing features, be explicit about whether you mean production chunk-manifest snapshots or standalone binary patch-chain research.

### DiffManager status

`DiffManager` round-trips correctly — create then apply reproduces the target byte-for-byte —
but it is not a useful delta algorithm as written. It hashes the original file at fixed 4 KB
offsets and compares each 4 KB block of the new file only at the *same* alignment: the rolling
window is computed but never slid. Measured on a 512 KB file:

| Edit | Patch size |
| ---- | ---------- |
| One byte overwritten in place | ~1% of file |
| 1 KB appended | ~0.5% of file |
| **One byte inserted at the front** | **~100% of file** |

Any insertion or deletion shifts every subsequent block out of alignment, so the whole
remainder is emitted as literal data. Fixing it means sliding the weak hash byte-by-byte,
rsync-style, which is a real piece of work.

Content-defined chunking in `ChunkingService` already solves the same problem better for
ReStore's purposes: boundaries follow content, so an insertion shifts only the chunks it
touches, and dedup works across files and snapshots rather than against one prior version.
`DiffManager` is kept as a reference only. Don't build on it without closing the
sliding-window gap first.
