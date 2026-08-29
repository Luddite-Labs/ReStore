# GUI Application Usage

The GUI provides:

- **Dashboard**: Statistics, a backup health panel, recent backup history, and quick actions
- **Backup Browser**: View and manage backup history with restore, verify, label, delete, and open-location actions
- **Settings**: Configure watch directories, storage providers, and backup options
- **System Tray**: Minimize to tray and control the file watcher in the background

## First launch

The first time you start ReStore with nothing configured, a three-step wizard opens: pick the
folders to protect, choose a destination and test the connection, and optionally turn on
encryption. It offers to run the first backup when you finish, so you do not have to touch
`config.json` by hand.

You can skip the wizard and configure everything from the Settings page instead.

## Backup health panel

The Dashboard shows what the backup telemetry actually says rather than just a success count:

- Bytes deduplication has saved, as an exact figure rather than an estimate
- Integrity-check pass rate, and when verification last ran
- Restore success rate across past attempts
- Each watched folder with its last successful backup, colour-coded by staleness
- A "needs attention" list covering restore failure categories, verification problems, folders
  that have never been backed up, and retention being switched off

Folder rows link straight through to a restore.

## Progress and cancellation

Manual backups, restores and verifications open a progress window showing the current phase,
per-file and per-byte counts, and a live log. Cancel stops the operation at its next safe
checkpoint; closing the window does the same rather than orphaning the work.

A cancelled backup leaves the previous snapshot as the current one, and a cancelled restore
leaves no partial files behind.

## Restoring

Restores always show a preview before writing anything: how many files, how large, and which
existing files at the destination would be affected — separating files that are already
identical from ones that genuinely differ. You choose per-file what to include and what should
happen to conflicts (skip, keep both, overwrite, or abort).

The default is to skip, so nothing you already have is replaced unless you say so.

## Labelling a restore point

Snapshots are listed by timestamp, which is not much help months later. The **Label** action on
a Backup Browser row lets you name one — "before Windows reinstall", say — and the label shows
as a chip on that row from then on.

## Snapshot verification

Each snapshot row in the Backup Browser has a **Verify Snapshot** action.

- Available for snapshot manifest and HEAD artifacts only
- Validates manifest integrity, chunk presence/hash/size, and reconstructed file hashes
- Results are shown in the UI, and telemetry counters are persisted to `system_state.json`

Verification downloads every unique chunk the snapshot references, so it costs bandwidth on
metered providers. ReStore can also run it on a schedule — see
[Configuration](../configuration.md#scheduled-verification).

## Post-Installation Configuration

After installation, visit the Settings page to:

- Enable "Run at Windows Startup" to launch ReStore automatically when your computer boots
- Configure your storage providers
- Add directories to watch
