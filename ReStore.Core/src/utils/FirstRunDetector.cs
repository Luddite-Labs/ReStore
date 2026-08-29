namespace ReStore.Core.src.utils;

/// <summary>
/// Whether a config still looks like the untouched template that config auto-creation writes.
/// <para>
/// Auto-creation means a config file always exists by the time any UI runs, so first run cannot
/// be detected from the file being absent. The template also ships with real watch directories
/// and a full set of storage-provider stubs, so neither "has a watch directory" nor "has any
/// storage source" distinguishes a fresh install from a configured one.
/// </para>
/// </summary>
public static class FirstRunDetector
{
    private static readonly HashSet<string> NonCredentialOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "backup_folder_name",
        "token_folder",
        "containerName",
        "bucketName",
        "serviceUrl",
        "host",
        "port",
        "username"
    };

    /// <summary>
    /// Recognises the template's own stand-in values without needing to enumerate them. Listing
    /// them literally meant every new provider stub added to the example config silently made the
    /// config look configured, which is how this check first went wrong: the sftp entry ships
    /// <c>optional-passphrase</c> and a <c>C:\path\to\...</c> key path, so one unlisted value was
    /// enough to suppress the wizard on every clean install.
    /// </summary>
    private static bool IsPlaceholder(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("my-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("optional-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return true;
        }

        // Illustrative filesystem paths: "C:\path\to\key.json", "/path/to/key".
        if (trimmed.Contains("path\\to\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("path/to/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Reserved example domains (RFC 2606) and the generic account name the template uses.
        return trimmed.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("example.com", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("user", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when nothing has been backed up yet and no storage provider has usable credentials.
    /// </summary>
    /// <param name="storageSources">Configured providers, keyed by storage type.</param>
    /// <param name="localProviderIsConfigured">
    /// Whether the local provider counts as set up. Local storage needs no credentials, so it
    /// cannot be judged by <paramref name="storageSources"/> alone; the caller decides, normally
    /// by whether its backup directory already exists.
    /// </param>
    /// <param name="totalBackupCount">Recorded backups across all groups.</param>
    public static bool NeedsSetup(
        IReadOnlyDictionary<string, StorageConfig> storageSources,
        bool localProviderIsConfigured,
        int totalBackupCount)
    {
        if (totalBackupCount > 0)
        {
            return false;
        }

        if (localProviderIsConfigured)
        {
            return false;
        }

        return !storageSources.Any(source => HasUsableCredentials(source.Key, source.Value));
    }

    /// <summary>
    /// True when a provider entry carries at least one filled-in credential. The local provider
    /// is excluded because it has no credentials to inspect.
    /// </summary>
    public static bool HasUsableCredentials(string storageType, StorageConfig config)
    {
        if (string.Equals(storageType, "local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return config.Options.Any(option =>
            !NonCredentialOptionKeys.Contains(option.Key)
            && !IsPlaceholder(option.Value));
    }
}
