using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

public class FileDiffSyncManager(ILogger logger, SystemState systemState, BackupConfigurationManager backupConfigManager)
{
    private readonly ILogger _logger = logger;
    private readonly SystemState _systemState = systemState;
    private readonly BackupConfigurationManager _backupConfigManager = backupConfigManager;

    public List<string> GetFilesToBackup(List<string> allFiles, string? group = null)
    {
        var backupType = _backupConfigManager.Configuration.Type;
        _logger.Log($"Determining files to backup based on type: {backupType}", LogLevel.Debug);

        var filesToBackup = _systemState.GetChangedFiles(allFiles, backupType, group)
            ?? _systemState.GetChangedFiles(allFiles, backupType)
            ?? [];

        _logger.Log($"Identified {filesToBackup.Count} files requiring backup.", LogLevel.Info);
        return filesToBackup;
    }

    /// <summary>
    /// Records the post-backup state of each file. The caller saves state afterwards, so a
    /// partial run does not persist metadata claiming files were captured.
    /// </summary>
    public async Task UpdateFileMetadataAsync(List<string> backedUpFiles)
    {
        _logger.Log($"Updating metadata for {backedUpFiles.Count} successfully backed up files.", LogLevel.Debug);
        foreach (var filePath in backedUpFiles)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    await _systemState.AddOrUpdateFileMetadataAsync(filePath);
                }
                else
                {
                    _logger.Log($"File no longer exists, skipping metadata update: {filePath}", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                // One unreadable file must not abandon the rest of the batch.
                _logger.Log($"Error updating metadata for file {filePath}: {ex.Message}", LogLevel.Warning);
            }
        }
        _logger.Log("Metadata update complete.", LogLevel.Debug);
    }
}
