# Features

## Graphical User Interface

- **First-Run Wizard**: Three-step setup on first launch — folders to protect, destination with a live connection test, and optional encryption. No hand-editing of JSON required.
- **Modern Dashboard**: Real-time statistics, backup history, and quick actions
- **Backup Health Panel**: Dedup savings, integrity-check pass rate, restore reliability, per-folder staleness, and a "needs attention" list
- **Progress and Cancellation**: Backups, restores, and verifications report live per-file and per-byte progress and can be cancelled mid-operation
- **Restore Preview**: Before writing anything, see file count, total size, and exactly which existing files would be affected, with per-file selection
- **Settings Management**: Configure watch directories, backup types, storage providers, and exclusions from the Settings page
- **Backup Browser**: View and manage your backup history with restore, labelling, open-location, and snapshot verification actions; filter by provider, encryption, or age
- **Automatic CLI Access**: The `restore` command is automatically available in all terminals after installation
- **Run at Startup**: Option to automatically launch ReStore when Windows starts
- **System Tray Integration**: Minimize to tray and control the file watcher in the background
- **Failure Notifications**: Tray alerts for failed backups and integrity checks, and for recovery after a run of failures
- **Theme Support**: Light, dark, and system theme options

## File and Directory Backup

- **Multiple Backup Types**: Full, incremental, and chunk snapshot backups
- **Real-time Monitoring**: Automatic backups when files change in watched directories
- **Scheduled Backups**: Each watched directory is also swept on its configured `backupInterval`, so changes made while the machine was asleep or the app was closed still get captured
- **Scheduled Verification**: Optional periodic integrity checks, off by default and local-only unless you opt out. Each cycle checks the newest snapshot per folder plus the least-recently-verified older ones, so old restore points are eventually covered too
- **Overwrite Protection**: Restores default to never replacing an existing file; choose skip, keep-both, overwrite, or abort-on-conflict
- **Selective Restore**: Restore a chosen subset of a snapshot rather than all of it
- **Restore Point Labels**: Tag a snapshot ("before Windows reinstall") to find it again later
- **Smart Filtering**: Exclude patterns and paths you don't want to backup
- **Size Management**: Configurable thresholds and file size limits

ChunkSnapshot mode creates point-in-time manifests and reuses previously uploaded chunks to reduce transfer and storage costs. The browser can also verify a snapshot manifest and its chunk store without restoring files.

## System State Backup

- **Installed Programs**: Backup your installed software list with automatic Winget restoration scripts
- **Environment Variables**: Save and restore user and system environment variables
- **Windows Settings**: Backup registry-based settings including personalization, themes, taskbar, File Explorer preferences, regional settings, mouse and keyboard configurations, and accessibility options

## Storage Flexibility

- **Local Storage**: Backup to local drives, external drives, or network shares
- **Cloud Platforms**: Support for Google Drive, Google Cloud Storage, Amazon S3, Azure Blob Storage, Dropbox, Backblaze B2, SFTP, and GitHub storage
- **Per-Path Storage**: Configure different storage destinations for each watched directory
- **Per-Component Storage**: Use different storage backends for system backups (programs, environment, settings)
- **Global Fallback**: Set a default storage type that applies when no specific storage is configured
- **Multi-destination**: Use several storage providers at once

## File Sharing

- **Secure Sharing**: Generate temporary, shareable links for your files directly from your storage provider
- **Context Menu Integration**: Enable "Share with ReStore" in Settings to add right-click sharing in Windows Explorer
- **Supported Providers**: Works with Amazon S3, Azure Blob Storage, Google Cloud Storage, Dropbox, and Backblaze B2

## Smart File Handling

- **Change Detection**: SHA256 hashing to detect file modifications accurately
- **Content-Defined Chunking**: Chunk boundaries adapt to file content for high reuse across snapshots
- **Chunk Deduplication**: Content-addressed chunk IDs avoid re-uploading unchanged data
- **Encryption**: AES-256-GCM encryption with password protection for secure backups
- **History Tracking**: Complete backup history with metadata
- **Retention Policies**: Automatic pruning of old backups
