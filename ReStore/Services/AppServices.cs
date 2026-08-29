using System.Threading.Tasks;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// Shared <see cref="ConfigManager"/> and <see cref="SystemState"/> for all pages.
    /// SaveAsync writes whatever its own instance holds, so per-page instances would let one
    /// page's save discard another's unsaved edits.
    /// </summary>
    public static class AppServices
    {
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        private static ConfigManager? _configManager;
        private static SystemState? _systemState;
        private static Logger? _logger;

        public static Logger Logger => _logger ??= new Logger();

        public static async Task<ConfigManager> GetConfigManagerAsync()
        {
            if (_configManager != null)
            {
                return _configManager;
            }

            await _initLock.WaitAsync();
            try
            {
                if (_configManager == null)
                {
                    var manager = new ConfigManager(Logger);
                    await manager.LoadAsync();

                    // Assign only after a successful load, so a failure doesn't cache a
                    // half-initialised instance for the rest of the session.
                    _configManager = manager;
                }
            }
            finally
            {
                _initLock.Release();
            }

            return _configManager;
        }

        public static async Task<SystemState> GetSystemStateAsync()
        {
            if (_systemState != null)
            {
                return _systemState;
            }

            await _initLock.WaitAsync();
            try
            {
                if (_systemState == null)
                {
                    var state = new SystemState(Logger);
                    await state.LoadStateAsync();
                    _systemState = state;
                }
            }
            finally
            {
                _initLock.Release();
            }

            return _systemState;
        }

        /// <summary>Re-reads config from disk, discarding unsaved in-memory changes.</summary>
        public static async Task ReloadConfigurationAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                _configManager ??= new ConfigManager(Logger);
                await _configManager.LoadAsync();
            }
            finally
            {
                _initLock.Release();
            }
        }
    }
}
