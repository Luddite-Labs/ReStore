# CLI Usage

The `restore` command is automatically available system-wide after installing ReStore through the MSIX package.

**To disable the CLI usage** (if not needed):

1. Open Windows **Settings** → **Apps** → **Advanced app settings** → **App execution aliases**
2. Find **restore.exe** in the list
3. Toggle it **Off**

## Start File Watcher

Monitor directories for changes and backup automatically:

```bash
restore --service
```

## Manual Backup

Backup a specific directory (uses configured storage from config.json):

```bash
restore backup "C:\Users\YourName\Documents"

# Or override storage type
restore backup "C:\Users\YourName\Documents" --storage gdrive
```

The backup pipeline writes chunked snapshot artifacts:

- Snapshot manifests under `snapshots/<group-key>/...manifest.json`
- Snapshot head pointer at `snapshots/<group-key>/HEAD`
- Deduplicated chunks under `chunks/<prefix>/<chunk-id>.chunk`

## Restore Files

Restore from a snapshot manifest or HEAD path:

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents"
restore restore "snapshots/documents_abcd1234ef567890/snapshot_20260101010101_abcdef.manifest.json" "C:\Restore\Documents"
```

Every restore prints a preview first — file count, total size, and which destination files
already exist and would be affected.

### Preview without writing anything

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --dry-run
```

### Overwrite protection

`--conflict` decides what happens to files that already exist at the destination. The
default is `skip`, which never modifies a file you already have.

```bash
# Keep existing files, restore only what is missing (default)
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict skip

# Restore alongside as "name (restored).ext"
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict keepboth

# Replace existing files
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict overwrite

# Abort before writing anything if any file would be replaced
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict fail
```

Restored files are written to a temporary sibling file and moved into place only after their
hash matches the manifest, so an interrupted restore never leaves a truncated file where
your real file was.

### Restore a subset

`--include` filters by manifest-relative path and may be repeated. `**` spans directory
separators, `*` and `?` do not.

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --include "reports/**"
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --include "reports/**" --include "*.docx"
```

### Cancellation

`Ctrl+C` cancels an in-flight backup, restore, or verify. A cancelled backup leaves `HEAD`
pointing at the previous snapshot, and a cancelled restore leaves no partial files behind.

## Verify Snapshot Integrity

Verify manifest and chunk-store integrity without restoring files:

```bash
restore verify "snapshots/documents_abcd1234ef567890/HEAD"
restore verify "snapshots/documents_abcd1234ef567890/snapshot_20260101010101_abcdef.manifest.json" --storage s3
```

Verification checks:

- Manifest root hash validation
- Chunk existence and content-hash validation
- Reconstructed file hash and size validation

The command exits with a non-zero exit code if validation fails.

## System Backup

Backup installed programs, environment variables, and Windows settings:

```bash
# Backup all components (uses configured storage per component)
restore system-backup all

# Backup specific components
restore system-backup programs
restore system-backup environment
restore system-backup settings

# Override storage for specific backup
restore system-backup programs --storage github
```

## System Restore

Restore system components:

```bash
restore system-restore "system_backups/programs/programs_backup_<timestamp>.zip" programs
restore system-restore "system_backups/environment/env_backup_<timestamp>.zip" environment
restore system-restore "system_backups/settings/settings_backup_<timestamp>.zip" settings
```

## CLI Usage with Encryption

When encryption is enabled, the CLI prompts for your password during restore and verify operations:

```bash
# Restore encrypted chunk snapshot
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents"

# Verify encrypted chunk snapshot
restore verify "snapshots/documents_abcd1234ef567890/HEAD"

# Restore encrypted system backups (will prompt for password)
restore system-restore "system_backups/programs/programs_backup_20250817.zip.enc" programs
restore system-restore "system_backups/environment/env_backup_20250817.zip.enc" environment
restore system-restore "system_backups/settings/settings_backup_20250817.zip.enc" settings
```

For non-interactive use, set `RESTORE_ENCRYPTION_PASSWORD` before running the command.
