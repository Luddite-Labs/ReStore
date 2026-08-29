using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using ReStore.Core.src.core;
using System.Runtime.Versioning;

namespace ReStore.Core.src.backup;

[SupportedOSPlatform("windows")]
public class SystemBackupManager
{
    private readonly ILogger _logger;
    private readonly SystemProgramDiscovery _programDiscovery;
    private readonly EnvironmentVariablesManager _envManager;
    private readonly WindowsSettingsManager _settingsManager;
    private readonly IConfigManager _config;
    private readonly SystemState _systemState;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly RetentionManager _retentionManager;

    public SystemBackupManager(ILogger logger, IConfigManager config, SystemState systemState, IPasswordProvider? passwordProvider = null)
    {
        _logger = logger;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _systemState = systemState;
        _programDiscovery = new SystemProgramDiscovery(logger);
        _envManager = new EnvironmentVariablesManager(logger);
        _settingsManager = new WindowsSettingsManager(logger);
        _passwordProvider = passwordProvider;
        _retentionManager = new RetentionManager(_logger, _config, _systemState);
    }

    public async Task BackupSystemAsync()
    {
        _logger.Log("Starting full system backup...", LogLevel.Info);

        try
        {
            if (_config.SystemBackup.IncludePrograms)
                await BackupInstalledProgramsAsync();
            else
                _logger.Log("Skipping programs backup (disabled in config)", LogLevel.Info);

            if (_config.SystemBackup.IncludeEnvironmentVariables)
                await BackupEnvironmentVariablesAsync();
            else
                _logger.Log("Skipping environment variables backup (disabled in config)", LogLevel.Info);

            if (_config.SystemBackup.IncludeWindowsSettings)
                await BackupWindowsSettingsAsync();
            else
                _logger.Log("Skipping Windows settings backup (disabled in config)", LogLevel.Info);

            _logger.Log("System backup completed successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"System backup failed: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private string GetStorageTypeForComponent(string component)
    {
        return component.ToLowerInvariant() switch
        {
            "programs" => _config.SystemBackup.ProgramsStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            "environment" => _config.SystemBackup.EnvironmentStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            "settings" => _config.SystemBackup.SettingsStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            _ => _config.GlobalStorageType
        };
    }

    public Task BackupInstalledProgramsAsync()
    {
        return RunComponentBackupAsync(
            new SystemBackupComponent(
                Component: "programs",
                Group: "system_programs",
                FileNamePrefix: "programs_backup",
                RemoteDirectory: "system_backups/programs",
                StartMessage: "Backing up installed programs...",
                FailureMessage: "Failed to backup installed programs"),
            async tempDir =>
            {
                var programs = await _programDiscovery.GetAllInstalledProgramsAsync();

                await _programDiscovery.ExportProgramsToJsonAsync(programs, Path.Combine(tempDir, "installed_programs.json"));
                await CreateWingetRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_winget_programs.ps1"));
                await CreateManualInstallListAsync(programs, Path.Combine(tempDir, "manual_install_list.txt"));
                await CreateFullRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_programs.ps1"));

                return $"{programs.Count} programs";
            });
    }

    public Task BackupEnvironmentVariablesAsync()
    {
        return RunComponentBackupAsync(
            new SystemBackupComponent(
                Component: "environment",
                Group: "system_environment",
                FileNamePrefix: "env_backup",
                RemoteDirectory: "system_backups/environment",
                StartMessage: "Backing up environment variables...",
                FailureMessage: "Failed to backup environment variables"),
            async tempDir =>
            {
                var variables = await _envManager.GetAllEnvironmentVariablesAsync();

                await _envManager.ExportEnvironmentVariablesToJsonAsync(variables, Path.Combine(tempDir, "environment_variables.json"));
                await _envManager.CreateRestoreScriptAsync(variables, Path.Combine(tempDir, "restore_environment_variables.ps1"));
                await CreateRegistryBackupScriptAsync(Path.Combine(tempDir, "backup_env_registry.ps1"));

                return $"{variables.Count} variables";
            });
    }

    public Task BackupWindowsSettingsAsync()
    {
        return RunComponentBackupAsync(
            new SystemBackupComponent(
                Component: "settings",
                Group: "system_settings",
                FileNamePrefix: "settings_backup",
                RemoteDirectory: "system_backups/settings",
                StartMessage: "Backing up Windows settings...",
                FailureMessage: "Failed to backup Windows settings"),
            async tempDir =>
            {
                var export = await _settingsManager.ExportWindowsSettingsAsync(tempDir);

                var scriptPath = Path.Combine(tempDir, "restore_windows_settings.ps1");
                await _settingsManager.CreateRestoreScriptAsync(export, tempDir, scriptPath);

                return $"{export.ExportedCategories.Count} categories";
            });
    }

    /// <summary>Naming and storage conventions for one system backup component.</summary>
    private sealed record SystemBackupComponent(
        string Component,
        string Group,
        string FileNamePrefix,
        string RemoteDirectory,
        string StartMessage,
        string FailureMessage);

    /// <summary>
    /// Shared pipeline: resolve storage, stage payload, archive, optionally encrypt, upload,
    /// record history, apply retention, and always clean up temp files.
    /// </summary>
    /// <param name="writePayloadAsync">
    /// Writes files into the staging directory and returns a short summary for the log line.
    /// </param>
    private async Task RunComponentBackupAsync(
        SystemBackupComponent component,
        Func<string, Task<string>> writePayloadAsync)
    {
        _logger.Log(component.StartMessage, LogLevel.Info);

        IStorage? storage = null;
        string? tempDir = null;
        string? zipPath = null;
        string? fileToUpload = null;

        try
        {
            var storageType = GetStorageTypeForComponent(component.Component);
            storage = await _config.CreateStorageAsync(storageType);
            _logger.Log($"Using {storageType} storage for {component.Component} backup", LogLevel.Info);

            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var uniqueId = Guid.NewGuid().ToString("N");
            tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemBackup", $"{timestamp}_{uniqueId}");
            Directory.CreateDirectory(tempDir);

            var payloadSummary = await writePayloadAsync(tempDir);

            var remotePath = $"{component.RemoteDirectory}/{component.FileNamePrefix}_{uniqueId}_{timestamp}.zip";
            zipPath = Path.Combine(Path.GetTempPath(), $"{component.FileNamePrefix}_{uniqueId}_{timestamp}.zip");

            // Recursive so payload writers may use subdirectories.
            var filesToCompress = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).ToList();
            await CompressionUtil.CompressFilesAsync(filesToCompress, tempDir, zipPath);

            fileToUpload = zipPath;
            if (_config.Encryption.Enabled)
            {
                EnsureEncryptionProviderAvailable();
                _logger.Log($"Encrypting {component.Component} backup...", LogLevel.Info);

                var password = await _passwordProvider!.GetPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Encryption is enabled but no password was provided");
                }

                var encryptedPath = await CompressionUtil.CompressAndEncryptAsync(
                    zipPath,
                    password,
                    _config.Encryption.Salt!,
                    _logger,
                    _config.Encryption.KeyDerivationIterations);

                fileToUpload = encryptedPath;
                remotePath = remotePath.Replace(".zip", ".zip.enc");

                var remoteMetadataPath = remotePath + ".meta";
                await storage.UploadAsync(encryptedPath + ".meta", remoteMetadataPath);
                _logger.Log($"Uploaded encryption metadata: {remoteMetadataPath}", LogLevel.Debug);
            }

            await storage.UploadAsync(fileToUpload, remotePath);

            var backupSize = new FileInfo(fileToUpload).Length;
            _systemState.AddBackup(component.Group, remotePath, false, storageType, backupSize);
            await _retentionManager.ApplyGroupAsync(component.Group);
            await _systemState.SaveStateAsync();

            _logger.Log(
                $"{component.Component} backup completed: {payloadSummary} backed up to {remotePath}",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"{component.FailureMessage}: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            CleanupBackupTempArtifacts(tempDir, zipPath, fileToUpload);
            storage?.Dispose();
        }
    }

    private async Task CreateWingetRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId)).ToList();

        var scriptContent = new List<string>
        {
            "# ReStore Winget Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "Write-Host 'Starting winget programs restore...' -ForegroundColor Green",
            "Write-Host 'This will install programs that are available via winget.' -ForegroundColor Yellow",
            "",
            "$installedCount = 0",
            "$failedCount = 0",
            "$skippedCount = 0",
            ""
        };

        foreach (var program in wingetPrograms)
        {
            scriptContent.AddRange(new[]
            {
                $"# Installing: {program.Name}",
                $"Write-Host 'Installing {program.Name}...' -ForegroundColor Cyan",
                "try {",
                $"    winget install --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "    if ($LASTEXITCODE -eq 0) {",
                $"        Write-Host 'Successfully installed {program.Name}' -ForegroundColor Green",
                "        $installedCount++",
                "    } else {",
                $"        Write-Host 'Failed to install {program.Name} (Exit code: $LASTEXITCODE)' -ForegroundColor Red",
                "        $failedCount++",
                "    }",
                "} catch {",
                $"    Write-Host 'Error installing {program.Name}: $($_.Exception.Message)' -ForegroundColor Red",
                "    $failedCount++",
                "}",
                ""
            });
        }

        scriptContent.AddRange(new[]
        {
            "Write-Host 'Winget restore completed!' -ForegroundColor Green",
            "Write-Host \"Installed: $installedCount\" -ForegroundColor Green",
            "Write-Host \"Failed: $failedCount\" -ForegroundColor Red",
            "Write-Host \"Total winget-available programs: " + wingetPrograms.Count + "\" -ForegroundColor Yellow"
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    private async Task CreateManualInstallListAsync(List<InstalledProgram> programs, string outputPath)
    {
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();

        var content = new List<string>
        {
            "# Programs that need manual installation",
            "# These programs were not found in winget and need to be installed manually",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            $"Total programs requiring manual installation: {manualPrograms.Count}",
            "",
            "Program Name | Version | Publisher | Install Location",
            "-------------|---------|-----------|------------------"
        };

        foreach (var program in manualPrograms.OrderBy(p => p.Name))
        {
            content.Add($"{program.Name} | {program.Version} | {program.Publisher} | {program.InstallLocation}");
        }

        content.AddRange(new[]
        {
            "",
            "Note: Search for these programs online or check if they have newer versions available.",
            "Some programs might now be available in winget - try searching with:",
            "winget search \"<program name>\""
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, content));
    }

    private async Task CreateFullRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore All Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "param(",
            "    [switch]$WingetOnly = $false,",
            "    [switch]$SkipConfirmation = $false",
            ")",
            "",
            "Write-Host 'ReStore Programs Restore' -ForegroundColor Green",
            "Write-Host '=========================' -ForegroundColor Green",
            "",
            "if (-not $SkipConfirmation) {",
            "    $response = Read-Host 'This will attempt to install all backed up programs. Continue? (y/N)'",
            "    if ($response -ne 'y' -and $response -ne 'Y') {",
            "        Write-Host 'Restore cancelled by user.' -ForegroundColor Yellow",
            "        exit 0",
            "    }",
            "}",
            "",
            "# Check if winget is available",
            "try {",
            "    winget --version | Out-Null",
            "    $wingetAvailable = $true",
            "    Write-Host 'Winget is available' -ForegroundColor Green",
            "} catch {",
            "    $wingetAvailable = $false",
            "    Write-Host 'Winget is not available' -ForegroundColor Red",
            "}",
            "",
            "$installedCount = 0",
            "$failedCount = 0",
            "$skippedCount = 0",
            ""
        };

        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId)).ToList();
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();

        scriptContent.AddRange(new[]
        {
            "# Installing programs via winget",
            "if ($wingetAvailable) {",
            $"    Write-Host 'Installing {wingetPrograms.Count} programs via winget...' -ForegroundColor Cyan",
            ""
        });

        foreach (var program in wingetPrograms)
        {
            scriptContent.AddRange(new[]
            {
                $"    Write-Host 'Installing {program.Name}...' -ForegroundColor Yellow",
                "    try {",
                $"        winget install --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "        if ($LASTEXITCODE -eq 0) {",
                $"            Write-Host 'Successfully installed {program.Name}' -ForegroundColor Green",
                "            $installedCount++",
                "        } else {",
                $"            Write-Host 'Failed to install {program.Name}' -ForegroundColor Red",
                "            $failedCount++",
                "        }",
                "    } catch {",
                $"        Write-Host 'Error installing {program.Name}: $($_.Exception.Message)' -ForegroundColor Red",
                "        $failedCount++",
                "    }",
                ""
            });
        }

        scriptContent.AddRange(new[]
        {
            "} else {",
            "    Write-Host 'Skipping winget installations (winget not available)' -ForegroundColor Yellow",
            $"    $skippedCount += {wingetPrograms.Count}",
            "}",
            "",
            "# Manual installation required",
            "if (-not $WingetOnly) {",
            $"    Write-Host 'The following {manualPrograms.Count} programs require manual installation:' -ForegroundColor Yellow",
            "    Write-Host '================================================================' -ForegroundColor Yellow"
        });

        foreach (var program in manualPrograms.Take(20)) // Limit output
        {
            scriptContent.Add($"    Write-Host '- {program.Name} (v{program.Version}) by {program.Publisher}' -ForegroundColor White");
        }

        if (manualPrograms.Count > 20)
        {
            scriptContent.Add($"    Write-Host '... and {manualPrograms.Count - 20} more (see manual_install_list.txt)' -ForegroundColor Gray");
        }

        scriptContent.AddRange(new[]
        {
            "    Write-Host 'Check manual_install_list.txt for complete list with details.' -ForegroundColor Yellow",
            "}",
            "",
            "# Summary",
            "Write-Host 'Restore Summary:' -ForegroundColor Green",
            "Write-Host \"Successfully installed: $installedCount\" -ForegroundColor Green",
            "Write-Host \"Failed to install: $failedCount\" -ForegroundColor Red",
            "Write-Host \"Skipped: $skippedCount\" -ForegroundColor Yellow",
            $"Write-Host \"Manual installation required: {manualPrograms.Count}\" -ForegroundColor Yellow"
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    private async Task CreateRegistryBackupScriptAsync(string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore Registry Environment Variables Backup Script",
            "# This creates a backup of environment variables stored in the registry",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "$timestamp = Get-Date -Format 'yyyyMMddHHmmss'",
            "$backupDir = \"$env:TEMP\\ReStore_RegistryBackup_$timestamp\"",
            "New-Item -ItemType Directory -Path $backupDir -Force | Out-Null",
            "",
            "Write-Host 'Backing up environment variables from registry...' -ForegroundColor Green",
            "",
            "# Backup system environment variables",
            "$systemEnvPath = 'HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment'",
            "$systemBackupFile = \"$backupDir\\system_environment.reg\"",
            "reg export \"$systemEnvPath\" \"$systemBackupFile\" /y",
            "",
            "# Backup user environment variables",
            "$userEnvPath = 'HKCU\\Environment'",
            "$userBackupFile = \"$backupDir\\user_environment.reg\"",
            "reg export \"$userEnvPath\" \"$userBackupFile\" /y",
            "",
            "Write-Host 'Registry backup completed:' -ForegroundColor Green",
            "Write-Host \"System variables: $systemBackupFile\" -ForegroundColor White",
            "Write-Host \"User variables: $userBackupFile\" -ForegroundColor White",
            "Write-Host \"Backup directory: $backupDir\" -ForegroundColor White",
            "",
            "# Note: To restore, use: reg import <file.reg>"
        };

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    public async Task RestoreSystemAsync(string backupType, string backupPath, string? storageTypeOverride = null)
    {
        _logger.Log($"Starting system restore of {backupType} from {backupPath}...", LogLevel.Info);

        IStorage? storage = null;
        string? tempDir = null;
        try
        {
            var component = ResolveRestoreComponent(backupType, backupPath);

            var storageType = storageTypeOverride ?? GetStorageTypeForComponent(component);
            storage = await _config.CreateStorageAsync(storageType);
            _logger.Log($"Using {storageType} storage for restore", LogLevel.Info);

            tempDir = CreateRestoreTempDirectory();
            var extractDir = await DownloadAndExtractRestoreBackupAsync(storage, backupPath, tempDir);
            await RestoreComponentAsync(component, extractDir);

            _logger.Log($"System restore of {backupType} completed successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"System restore failed: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            storage?.Dispose();
            CleanupRestoreTempDirectory(tempDir);
        }
    }

    private static string ResolveRestoreComponent(string backupType, string backupPath)
    {
        var normalizedBackupType = backupType.Trim().ToLowerInvariant();
        var normalizedBackupPath = backupPath.Replace('\\', '/').ToLowerInvariant();
        var component = normalizedBackupType switch
        {
            "system_programs" => "programs",
            "system_environment" => "environment",
            "system_settings" => "settings",
            "all" when normalizedBackupPath.Contains("/programs/") => "programs",
            "all" when normalizedBackupPath.Contains("/environment/") => "environment",
            "all" when normalizedBackupPath.Contains("/settings/") => "settings",
            _ => normalizedBackupType
        };

        if (component is not ("programs" or "environment" or "settings"))
        {
            throw new ArgumentException($"Unsupported system restore type: {backupType}. Use programs, environment, or settings.", nameof(backupType));
        }

        return component;
    }

    private static string CreateRestoreTempDirectory()
    {
        var restoreTimestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var restoreUniqueId = Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemRestore", $"{restoreTimestamp}_{restoreUniqueId}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private async Task<string> DownloadAndExtractRestoreBackupAsync(IStorage storage, string backupPath, string tempDir)
    {
        var isEncrypted = backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase);
        var zipPath = Path.Combine(tempDir, isEncrypted ? "backup.zip.enc" : "backup.zip");
        await storage.DownloadAsync(backupPath, zipPath);

        var extractDir = Path.Combine(tempDir, "extracted");

        if (isEncrypted)
        {
            await DownloadAndDecryptBackupAsync(storage, backupPath, zipPath, extractDir);
        }
        else
        {
            await CompressionUtil.DecompressAsync(zipPath, extractDir);
        }

        return extractDir;
    }

    private async Task DownloadAndDecryptBackupAsync(IStorage storage, string backupPath, string zipPath, string extractDir)
    {
        _logger.Log("Backup is encrypted, decrypting...", LogLevel.Info);
        var metadataPath = backupPath + ".meta";
        var tempMetadataPath = zipPath + ".meta";
        await storage.DownloadAsync(metadataPath, tempMetadataPath);

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted backup detected but no password provider available");
        }

        var password = await _passwordProvider.GetPasswordAsync();
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Password required to decrypt backup");
        }

        try
        {
            await CompressionUtil.DecryptAndDecompressAsync(zipPath, password, extractDir, _logger);
        }
        catch (Exception ex)
        {
            _passwordProvider.ClearPassword();
            _logger.Log("Decryption failed. Password cleared for retry.", LogLevel.Debug);
            throw new InvalidOperationException($"Failed to decrypt backup: {ex.Message}", ex);
        }
    }

    private async Task RestoreComponentAsync(string component, string extractDir)
    {
        if (component == "programs")
        {
            await RestoreProgramsAsync(extractDir);
        }
        else if (component == "environment")
        {
            await RestoreEnvironmentVariablesAsync(extractDir);
        }
        else if (component == "settings")
        {
            await RestoreWindowsSettingsAsync(extractDir);
        }
    }

    private void CleanupRestoreTempDirectory(string? tempDir)
    {
        if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to cleanup temporary restore directory {tempDir}: {ex.Message}", LogLevel.Warning);
        }
    }

    private Task RestoreProgramsAsync(string extractDir)
    {
        var scriptPath = Path.Combine(extractDir, "restore_programs.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Programs restore script found. Please run it manually with appropriate permissions.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }

        var jsonPath = Path.Combine(extractDir, "installed_programs.json");
        if (File.Exists(jsonPath))
        {
            _logger.Log($"Programs backup data available at: {jsonPath}", LogLevel.Info);
        }

        return Task.CompletedTask;
    }

    private async Task RestoreEnvironmentVariablesAsync(string extractDir)
    {
        var jsonPath = Path.Combine(extractDir, "environment_variables.json");
        if (File.Exists(jsonPath))
        {
            await _envManager.RestoreEnvironmentVariablesAsync(jsonPath);
        }

        var scriptPath = Path.Combine(extractDir, "restore_environment_variables.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Environment variables restore script available for manual execution.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }
    }

    private void CleanupBackupTempArtifacts(string? tempDir, string? zipPath, string? fileToUpload)
    {
        var tempFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTempFile(tempFiles, zipPath);
        AddTempFile(tempFiles, fileToUpload);

        if (!string.IsNullOrWhiteSpace(fileToUpload))
        {
            AddTempFile(tempFiles, fileToUpload + ".meta");
        }

        foreach (var tempFile in tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to delete temporary backup file {tempFile}: {ex.Message}", LogLevel.Warning);
            }
        }

        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to delete temporary backup directory {tempDir}: {ex.Message}", LogLevel.Warning);
        }
    }

    private static void AddTempFile(HashSet<string> tempFiles, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            tempFiles.Add(path);
        }
    }

    private Task RestoreWindowsSettingsAsync(string extractDir)
    {
        var scriptPath = Path.Combine(extractDir, "restore_windows_settings.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Windows settings restore script available for manual execution.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
            _logger.Log("IMPORTANT: Review the script before running. Some settings may require administrator privileges.", LogLevel.Warning);
        }

        var manifestPath = Path.Combine(extractDir, "settings_manifest.json");
        if (File.Exists(manifestPath))
        {
            _logger.Log($"Settings manifest available at: {manifestPath}", LogLevel.Info);
        }

        return Task.CompletedTask;
    }

    private void EnsureEncryptionProviderAvailable()
    {
        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encryption is enabled but no password provider is available");
        }
    }
}
