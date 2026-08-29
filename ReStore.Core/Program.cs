using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.backup;

namespace ReStore.Core
{
    public class Program
    {
        private const string USAGE_MESSAGE = @"Usage:
  restore.exe --service
  restore.exe backup <sourceDir> [--storage <storageType>]
  restore.exe restore <backupPath> <targetDir> [--storage <storageType>]
                      [--conflict skip|overwrite|keepboth|fail] [--include <pattern>] [--dry-run]
  restore.exe verify <snapshotManifestOrHeadPath> [--storage <storageType>]
  restore.exe system-backup [programs|environment|settings|all] [--storage <storageType>]
  restore.exe system-restore <backupPath> [programs|environment|settings] [--storage <storageType>]
  restore.exe --validate-config

Examples:
  restore.exe --service
  restore.exe backup %USERPROFILE%\Desktop
  restore.exe backup %USERPROFILE%\Documents --storage gdrive
  restore.exe restore snapshots/documents_abc123/HEAD C:\Restored --dry-run
  restore.exe restore snapshots/documents_abc123/HEAD C:\Restored --conflict keepboth
  restore.exe restore snapshots/documents_abc123/HEAD C:\Restored --include ""docs/**""
  restore.exe verify snapshots/documents_abc123/HEAD
  restore.exe verify snapshots/documents_abc123/snapshot_20260101010101_abcdef.manifest.json --storage s3
  restore.exe system-backup all
  restore.exe system-backup programs --storage local
  restore.exe system-restore system_backups/programs/... programs
  restore.exe --validate-config

Notes:
  - Storage types are configured in config.json
  - Per-path and per-component storage can be set in configuration
  - Use --storage flag to override configured storage for a specific operation
  - Restore defaults to --conflict skip, which never replaces an existing file
  - --include may be repeated; glob '*' and '**' are supported against manifest paths
  - Ctrl+C cancels an in-flight backup, restore or verify
  - Set RESTORE_ENCRYPTION_PASSWORD env var to provide password non-interactively";

        public static async Task Main(string[] args)
        {
            var logger = new Logger();

            var setupResult = ConfigInitializer.EnsureConfigurationSetup(logger);

            var configManager = new ConfigManager(logger);
            await configManager.LoadAsync();
            PrintConfigurationLifecycleSummary(setupResult, configManager.LastMigrationResult, logger);

            if (args.Length == 0)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            if (args.Length == 1 && args[0] == "--validate-config")
            {
                ValidateConfiguration(configManager, logger);
                return;
            }

            var isServiceMode = args.Length >= 1 && args[0] == "--service";
            var commandMode = args.Length >= 1 && (args[0] == "backup" || args[0] == "restore" || args[0] == "verify" || args[0] == "system-backup" || args[0] == "system-restore");

            if (!isServiceMode && !commandMode)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            if (HasMissingRequiredCommandArgument(args))
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            var validationResult = configManager.ValidateConfiguration();
            if (!validationResult.IsValid)
            {
                logger.Log("Configuration validation failed. Please fix the errors before proceeding.", LogLevel.Error);
                PrintValidationResults(validationResult, logger);
                Environment.ExitCode = 1;
                return;
            }
            else if (validationResult.HasIssues)
            {
                logger.Log("Configuration validation completed with warnings.", LogLevel.Warning);
                PrintValidationResults(validationResult, logger);
            }

            var systemState = new SystemState(logger);
            await systemState.LoadStateAsync();

            var sizeAnalyzer = new SizeAnalyzer();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                logger.Log("Cancellation requested. Shutting down...", LogLevel.Info);
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                if (isServiceMode)
                {
                    var servicePasswordProvider = CreateCliPasswordProvider(configManager);
                    using var watcher = new FileWatcher(configManager, logger, systemState, sizeAnalyzer, servicePasswordProvider);
                    await watcher.StartAsync();

                    // The watcher only reacts to changes seen while it is running; the
                    // scheduler covers the configured backupInterval so anything changed
                    // while the service was down still gets captured.
                    using var scheduler = new BackupScheduler(configManager, logger, systemState, sizeAnalyzer, servicePasswordProvider);
                    await scheduler.StartAsync();

                    logger.Log("File watcher service running. Press Ctrl+C to stop.", LogLevel.Info);

                    try
                    {
                        await Task.Delay(Timeout.Infinite, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on Ctrl+C; stop the scheduler before the state save below.
                    }

                    await scheduler.StopAsync();
                    logger.Log("File watcher service stopped.", LogLevel.Info);
                }
                else
                {
                    var command = args[0];
                    var storageOverride = GetStorageOverride(args);

                    switch (command)
                    {
                        case "backup":
                            if (args.Length < 2)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            var backupPasswordProvider = CreateCliPasswordProvider(configManager);
                            var backup = new Backup(logger, systemState, sizeAnalyzer, configManager, backupPasswordProvider);
                            await backup.BackupDirectoryAsync(
                                args[1],
                                storageOverride,
                                CreateConsoleBackupProgress(),
                                cts.Token);
                            break;

                        case "restore":
                            if (args.Length < 3)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }

                            if (!TryParseConflictPolicy(args, out var conflictPolicy))
                            {
                                Console.WriteLine("Invalid --conflict value. Expected one of: skip, overwrite, keepboth, fail.");
                                Environment.ExitCode = 1;
                                break;
                            }

                            var restoreStorageType = storageOverride ?? configManager.GlobalStorageType;
                            var restorePasswordProvider = CreateCliPasswordProvider(configManager);
                            using (var storage = await configManager.CreateStorageAsync(restoreStorageType))
                            {
                                var restore = new Restore(logger, storage, restorePasswordProvider, systemState);
                                var includePatterns = GetRepeatedOptionValues(args, "--include");
                                var isDryRun = args.Contains("--dry-run");

                                var preview = await restore.PreviewRestoreAsync(args[1], args[2], null, cts.Token);
                                var selectedPaths = includePatterns.Count == 0
                                    ? null
                                    : FilterByPatterns(preview, includePatterns);

                                if (selectedPaths != null && selectedPaths.Count == 0)
                                {
                                    logger.Log(
                                        $"No files in the snapshot matched the supplied --include pattern(s): {string.Join(", ", includePatterns)}",
                                        LogLevel.Warning);
                                    break;
                                }

                                PrintRestorePreview(preview, selectedPaths, conflictPolicy);

                                if (isDryRun)
                                {
                                    logger.Log("Dry run requested; no files were written.", LogLevel.Info);
                                    break;
                                }

                                var outcome = await restore.RestoreFromBackupAsync(
                                    args[1],
                                    args[2],
                                    new RestoreOptions
                                    {
                                        ConflictPolicy = conflictPolicy,
                                        RelativePaths = selectedPaths
                                    },
                                    CreateConsoleRestoreProgress(),
                                    cts.Token);

                                logger.Log(
                                    $"Restore finished: restored={outcome.FilesRestored}, skipped={outcome.FilesSkipped}, keptBoth={outcome.FilesKeptBoth}, overwritten={outcome.FilesOverwritten}.",
                                    LogLevel.Info);
                            }
                            break;

                        case "verify":
                            if (args.Length < 2)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }

                            var verifyStorageType = storageOverride ?? configManager.GlobalStorageType;
                            var verifyPasswordProvider = CreateCliPasswordProvider(configManager);
                            using (var storage = await configManager.CreateStorageAsync(verifyStorageType))
                            {
                                var verifier = new SnapshotIntegrityVerifier(logger, storage, verifyPasswordProvider, systemState);
                                var verificationResult = await verifier.VerifyAsync(args[1], null, cts.Token);

                                if (verificationResult.IsValid)
                                {
                                    logger.Log(
                                        $"Snapshot verification passed for '{verificationResult.ResolvedManifestPath}' (snapshot: {verificationResult.SnapshotId}).",
                                        LogLevel.Info);
                                }
                                else
                                {
                                    logger.Log(
                                        $"Snapshot verification failed for '{verificationResult.ResolvedManifestPath}'. Errors: {verificationResult.Errors.Count}.",
                                        LogLevel.Error);

                                    foreach (var error in verificationResult.Errors)
                                    {
                                        logger.Log($"Verify Error: {error}", LogLevel.Error);
                                    }

                                    Environment.ExitCode = 1;
                                }
                            }
                            break;

                        case "system-backup":
                            if (!OperatingSystem.IsWindows())
                            {
                                logger.Log("The 'system-backup' command is only supported on Windows.", LogLevel.Error);
                                break;
                            }
                            var backupType = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : "all";
                            var systemBackupPasswordProvider = CreateCliPasswordProvider(configManager);
                            var systemBackupManager = new SystemBackupManager(logger, configManager, systemState, systemBackupPasswordProvider);

                            switch (backupType.ToLowerInvariant())
                            {
                                case "programs":
                                    await systemBackupManager.BackupInstalledProgramsAsync();
                                    break;
                                case "environment":
                                    await systemBackupManager.BackupEnvironmentVariablesAsync();
                                    break;
                                case "settings":
                                    await systemBackupManager.BackupWindowsSettingsAsync();
                                    break;
                                case "all":
                                default:
                                    await systemBackupManager.BackupSystemAsync();
                                    break;
                            }
                            break;

                        case "system-restore":
                            if (args.Length < 2)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            if (!OperatingSystem.IsWindows())
                            {
                                logger.Log("The 'system-restore' command is only supported on Windows.", LogLevel.Error);
                                break;
                            }
                            var restoreType = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : "all";
                            var systemRestorePasswordProvider = CreateCliPasswordProvider(configManager);
                            var systemRestoreManager = new SystemBackupManager(logger, configManager, systemState, systemRestorePasswordProvider);
                            await systemRestoreManager.RestoreSystemAsync(restoreType, args[1], storageOverride);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C during a command, or shutdown in service mode. Neither is an error:
                // a cancelled backup leaves HEAD untouched and a cancelled restore leaves no
                // partial file behind.
                logger.Log("Operation cancelled.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                logger.Log($"An unexpected error occurred: {ex.Message}", LogLevel.Error);
                logger.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Debug);
                Environment.ExitCode = 1;
            }
            finally
            {
                // The service path now stops its scheduler before reaching here, so saving
                // unconditionally captures telemetry from the final cycle too.
                await systemState.SaveStateAsync();
                logger.Log("Application finished.", LogLevel.Info);
            }
        }

        private static bool TryParseConflictPolicy(string[] arguments, out RestoreConflictPolicy policy)
        {
            // Safest default: never replace a file the user already has.
            policy = RestoreConflictPolicy.Skip;

            var index = Array.IndexOf(arguments, "--conflict");
            if (index < 0)
            {
                return true;
            }

            if (index + 1 >= arguments.Length)
            {
                return false;
            }

            switch (arguments[index + 1].ToLowerInvariant())
            {
                case "skip":
                    policy = RestoreConflictPolicy.Skip;
                    return true;
                case "overwrite":
                    policy = RestoreConflictPolicy.Overwrite;
                    return true;
                case "keepboth":
                    policy = RestoreConflictPolicy.KeepBoth;
                    return true;
                case "fail":
                    policy = RestoreConflictPolicy.Fail;
                    return true;
                default:
                    return false;
            }
        }

        private static List<string> GetRepeatedOptionValues(string[] arguments, string optionName)
        {
            var values = new List<string>();

            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index].Equals(optionName, StringComparison.OrdinalIgnoreCase)
                    && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values.Add(arguments[index + 1]);
                }
            }

            return values;
        }

        private static HashSet<string> FilterByPatterns(RestorePreview preview, List<string> patterns)
        {
            return preview.Entries
                .Select(entry => entry.RelativePath)
                .Where(relativePath => patterns.Any(pattern => MatchesGlob(relativePath, pattern)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Glob match over '/'-separated manifest paths. '**' spans separators, '*' and '?'
        /// do not.
        /// </summary>
        internal static bool MatchesGlob(string relativePath, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            var normalizedPath = relativePath.Replace('\\', '/');
            var normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');

            var regex = new System.Text.StringBuilder("^");
            for (var index = 0; index < normalizedPattern.Length; index++)
            {
                var character = normalizedPattern[index];
                switch (character)
                {
                    case '/' when normalizedPattern.AsSpan(index + 1).StartsWith("**"):
                        // "/**" makes the separator itself optional, so "docs/**" matches
                        // "docs" as well as everything beneath it.
                        regex.Append("(?:/.*)?");
                        index += 2;

                        // "docs/**/x" should also match "docs/x"; consume the separator that
                        // the optional group already accounts for.
                        if (index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '/')
                        {
                            index++;
                            regex.Append('/');
                        }
                        break;
                    case '*':
                        if (index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*')
                        {
                            regex.Append(".*");
                            index++;
                        }
                        else
                        {
                            regex.Append("[^/]*");
                        }
                        break;
                    case '?':
                        regex.Append("[^/]");
                        break;
                    default:
                        regex.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
                        break;
                }
            }

            regex.Append('$');

            return System.Text.RegularExpressions.Regex.IsMatch(
                normalizedPath,
                regex.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void PrintRestorePreview(
            RestorePreview preview,
            IReadOnlySet<string>? selectedPaths,
            RestoreConflictPolicy conflictPolicy)
        {
            var entries = selectedPaths == null
                ? preview.Entries
                : [.. preview.Entries.Where(entry => selectedPaths.Contains(entry.RelativePath))];

            var totalBytes = entries.Sum(entry => entry.SizeBytes);
            var differing = entries.Count(entry => entry.Conflict == RestoreConflictKind.Differs);
            var identical = entries.Count(entry => entry.Conflict == RestoreConflictKind.Identical);

            Console.WriteLine();
            Console.WriteLine($"Restore preview for snapshot {preview.SnapshotId} ({preview.SnapshotCreatedUtc:yyyy-MM-dd HH:mm:ss} UTC)");
            Console.WriteLine($"  Target directory : {preview.TargetDirectory}");
            Console.WriteLine($"  Files to restore : {entries.Count}");
            Console.WriteLine($"  Total size       : {ByteFormatter.Format(totalBytes)}");
            Console.WriteLine($"  Already present  : {identical} identical, {differing} differing");
            Console.WriteLine($"  Conflict policy  : {conflictPolicy}");

            if (preview.FilesFilteredOut > 0 || (selectedPaths != null && preview.FileCount != entries.Count))
            {
                Console.WriteLine($"  Excluded by filter: {preview.FileCount - entries.Count + preview.FilesFilteredOut}");
            }

            if (differing > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  These existing files differ from the snapshot ({conflictPolicy} will be applied):");
                foreach (var entry in entries.Where(e => e.Conflict == RestoreConflictKind.Differs).Take(10))
                {
                    Console.WriteLine($"    {entry.RelativePath} (on disk {ByteFormatter.Format(entry.ExistingSizeBytes ?? 0)}, snapshot {ByteFormatter.Format(entry.SizeBytes)})");
                }

                if (differing > 10)
                {
                    Console.WriteLine($"    ...and {differing - 10} more.");
                }
            }

            Console.WriteLine();
        }

        private static IProgress<BackupProgress> CreateConsoleBackupProgress()
        {
            var lastReport = string.Empty;

            return new Progress<BackupProgress>(progress =>
            {
                var line = $"[{progress.Phase}] {progress.FilesDone}/{progress.FilesTotal} files ({progress.Fraction:P0})";
                if (line == lastReport)
                {
                    return;
                }

                lastReport = line;
                Console.WriteLine(line);
            });
        }

        private static IProgress<RestoreProgress> CreateConsoleRestoreProgress()
        {
            var lastReport = string.Empty;

            return new Progress<RestoreProgress>(progress =>
            {
                var line = $"[{progress.Phase}] {progress.FilesDone}/{progress.FilesTotal} files ({progress.Fraction:P0})";
                if (line == lastReport)
                {
                    return;
                }

                lastReport = line;
                Console.WriteLine(line);
            });
        }

        private static string? GetStorageOverride(string[] arguments)
        {
            var storageIndex = Array.IndexOf(arguments, "--storage");
            if (storageIndex >= 0 && storageIndex + 1 < arguments.Length)
            {
                return arguments[storageIndex + 1];
            }

            return null;
        }

        private static bool HasMissingRequiredCommandArgument(string[] arguments)
        {
            return arguments[0] switch
            {
                "backup" => arguments.Length < 2,
                "restore" => arguments.Length < 3,
                "verify" => arguments.Length < 2,
                "system-restore" => arguments.Length < 2,
                _ => false
            };
        }

        private static IPasswordProvider CreateCliPasswordProvider(IConfigManager config)
        {
            return new CliPasswordProvider(config.Encryption.Enabled);
        }

        private sealed class CliPasswordProvider : IPasswordProvider
        {
            private readonly bool _encryptionEnabled;
            private string? _cachedPassword;

            public CliPasswordProvider(bool encryptionEnabled)
            {
                _encryptionEnabled = encryptionEnabled;
            }

            public Task<string?> GetPasswordAsync()
            {
                if (!_encryptionEnabled)
                {
                    return Task.FromResult<string?>(null);
                }

                if (!string.IsNullOrWhiteSpace(_cachedPassword))
                {
                    return Task.FromResult<string?>(_cachedPassword);
                }

                var envPassword = Environment.GetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD");
                if (!string.IsNullOrWhiteSpace(envPassword))
                {
                    _cachedPassword = envPassword;
                    return Task.FromResult<string?>(_cachedPassword);
                }

                Console.Write("Enter encryption password: ");
                _cachedPassword = ReadPasswordFromConsole();
                return Task.FromResult<string?>(_cachedPassword);
            }

            public bool IsPasswordSet()
            {
                if (!_encryptionEnabled)
                {
                    return false;
                }

                return !string.IsNullOrWhiteSpace(_cachedPassword)
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD"));
            }

            public void ClearPassword()
            {
                _cachedPassword = null;
            }
        }

        private static string ReadPasswordFromConsole()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return password.ToString();
        }

        private static void PrintConfigurationLifecycleSummary(
            ConfigSetupResult setupResult,
            ConfigMigrationResult? migrationResult,
            Logger logger)
        {
            if (setupResult.ConfigCreated)
            {
                logger.Log($"Configuration initialized at: {ConfigInitializer.GetUserConfigPath()}", LogLevel.Info);
            }

            if (setupResult.ExampleConfigUpdated)
            {
                logger.Log($"Updated local config.example.json reference at: {ConfigInitializer.GetUserExampleConfigPath()}", LogLevel.Debug);
            }

            if (migrationResult == null)
            {
                return;
            }

            if (migrationResult.MigrationApplied)
            {
                logger.Log(
                    $"Configuration migration applied ({migrationResult.SourceSchemaVersion} -> {migrationResult.TargetSchemaVersion}).",
                    LogLevel.Info);

                if (!string.IsNullOrWhiteSpace(migrationResult.BackupPath))
                {
                    logger.Log($"Migration backup file: {migrationResult.BackupPath}", LogLevel.Info);
                }
            }

            foreach (var warning in migrationResult.Warnings)
            {
                logger.Log(warning, LogLevel.Warning);
            }
        }

        private static void ValidateConfiguration(IConfigManager configManager, ILogger logger)
        {
            logger.Log($"Configuration file location: {configManager.GetConfigFilePath()}", LogLevel.Info);
            logger.Log("Running comprehensive configuration validation...", LogLevel.Info);

            var result = configManager.ValidateConfiguration();

            PrintValidationResults(result, logger);

            if (result.IsValid)
            {
                Console.WriteLine("\nConfiguration is valid and ready to use!");
                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine($"Found {result.Warnings.Count} warning(s) that should be reviewed.");
                }
            }
            else
            {
                Console.WriteLine($"\nConfiguration validation failed with {result.Errors.Count} error(s).");
                Console.WriteLine("Please fix the errors above before using ReStore.");
                Environment.ExitCode = 1;
            }
        }

        private static void PrintValidationResults(ConfigValidationResult result, ILogger logger)
        {
            if (result.Errors.Count > 0)
            {
                Console.WriteLine("\nERRORS:");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"{error}");
                    logger.Log($"Config Error: {error}", LogLevel.Error);
                }
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("\nWARNINGS:");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"{warning}");
                    logger.Log($"Config Warning: {warning}", LogLevel.Warning);
                }
            }

            // Print info messages (only in validation mode, not during startup)
            if (result.Info.Count > 0)
            {
                Console.WriteLine("\nINFO:");
                foreach (var info in result.Info)
                {
                    Console.WriteLine($"{info}");
                    logger.Log($"Config Info: {info}", LogLevel.Info);
                }
            }
        }
    }
}
