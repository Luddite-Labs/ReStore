# Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up
- Windows settings backup captures registry-based settings only; some settings stored in other locations may not be included
- WiFi passwords and network credentials are not included in the settings backup
- User-file backups use snapshot manifests and deduplicated chunk objects; restoring from the older archive format is not supported by this flow
- Verification and restore hold one chunk in memory at a time, but a single file larger than `maxFileSizeMB` is skipped entirely rather than streamed
- `ReStore.Core/src/core/DiffManager.cs` is a standalone prototype and is not wired into production backup/restore flow. It only detects *aligned* block matches — its rolling window never slides — so a single-byte **insertion** produces a patch as large as the whole file. Content-defined chunking (`ChunkingService`) supersedes it for deduplication
- GitHub storage buffers each upload in memory as base64 (peak ≈ 2.3× file size) and is capped at GitHub's 100 MB per-file API limit; prefer object storage for large backups
