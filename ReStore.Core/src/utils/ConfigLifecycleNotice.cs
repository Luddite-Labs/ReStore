namespace ReStore.Core.src.utils;

/// <summary>
/// Builds the one-off startup notice about configuration creation and schema migration.
/// </summary>
public static class ConfigLifecycleNotice
{
    /// <param name="setupResult">Outcome of <see cref="ConfigInitializer.EnsureConfigurationSetup"/>.</param>
    /// <param name="migrationResult">Migration outcome, or null when config never loaded.</param>
    /// <param name="configPath">Path shown for a newly created config.</param>
    /// <param name="suppressConfigCreatedNotice">
    /// Drops the creation lines while keeping migration lines. Set when first-run setup is about to
    /// run: the wizard covers the same ground, and two stacked dialogs mean the user dismisses one
    /// before reaching the one that matters.
    /// </param>
    /// <returns>The message, or null when there is nothing to report.</returns>
    public static string? Build(
        ConfigSetupResult setupResult,
        ConfigMigrationResult? migrationResult,
        string configPath,
        bool suppressConfigCreatedNotice)
    {
        var lines = new List<string>();

        if (setupResult.ConfigCreated && !suppressConfigCreatedNotice)
        {
            lines.Add("Created your initial configuration file.");
            lines.Add($"Path: {configPath}");
        }

        if (migrationResult?.MigrationApplied == true)
        {
            lines.Add($"Upgraded configuration schema from v{migrationResult.SourceSchemaVersion} to v{migrationResult.TargetSchemaVersion}.");

            if (!string.IsNullOrWhiteSpace(migrationResult.BackupPath))
            {
                lines.Add($"Backup: {migrationResult.BackupPath}");
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        lines.Add(string.Empty);
        lines.Add("Open Settings to review or adjust your configuration.");
        return string.Join(Environment.NewLine, lines);
    }
}
