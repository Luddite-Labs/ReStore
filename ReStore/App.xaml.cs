using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using System.Windows;
using ReStore.Views;
using ReStore.Views.Pages;
using ReStore.Services;
using ReStore.Interop;
using ReStore.Core.src.utils;

namespace ReStore
{
    public partial class App : Application
    {
        private SystemTrayManager? _trayManager;
        private static Mutex? _instanceMutex;
        private static bool _ownsMutex;
        private const string MUTEX_NAME = "ReStore_SingleInstance_Mutex";
        private const string PIPE_NAME = "ReStore_CommandPipe";
        private Thread? _pipeServerThread;
        private bool _isRunning = true;

        public static GuiPasswordProvider? GlobalPasswordProvider { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MUTEX_NAME, out bool createdNew);
            _ownsMutex = createdNew;

            if (!createdNew)
            {
                if (e.Args.Length > 0)
                {
                    SendCommandToExistingInstance(e.Args);
                }
                else
                {
                    BringExistingInstanceToFront();
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);

            var configSetupResult = ConfigInitializer.EnsureConfigurationSetup(AppServices.Logger);
            ConfigMigrationResult? configMigrationResult = null;

            try
            {
                // Warms the shared instance so pages reuse this load rather than each
                // re-reading config.json; also surfaces migration results up front.
                var startupConfigManager = await AppServices.GetConfigManagerAsync();
                configMigrationResult = startupConfigManager.LastMigrationResult;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Config preflight load failed: {ex}");
                MessageBox.Show(
                    $"Configuration initialization encountered an issue. You may need to review your config file.\n\n{ex.Message}\n\nPath: {ConfigInitializer.GetUserConfigPath()}",
                    "ReStore Configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var lifecycleMessage = ConfigLifecycleNotice.Build(
                configSetupResult,
                configMigrationResult,
                ConfigInitializer.GetUserConfigPath(),
                suppressConfigCreatedNotice: await FirstRunSetupIsPendingAsync());

            GlobalPasswordProvider = new GuiPasswordProvider();

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Trace.WriteLine($"UnhandledException: {ex}");
                MessageBox.Show(ex?.ToString() ?? "Unknown error", "ReStore Error");
            };
            DispatcherUnhandledException += (_, args) =>
            {
                Trace.WriteLine($"DispatcherUnhandledException: {args.Exception}");
                MessageBox.Show(args.Exception.ToString(), "ReStore Error");
                args.Handled = true;
            };

            var theme = ThemeSettings.Load();
            theme.Apply();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            var settings = AppSettings.Load();
            mainWindow.UpdateTrayManager(settings.MinimizeToTray);
            _trayManager = mainWindow.TrayManager;

            mainWindow.SourceInitialized += (_, __) =>
            {
                WindowEffects.ApplySystemBackdrop(mainWindow);
                WindowEffects.FixMaximizedBounds(mainWindow);
            };

            if (e.Args.Length > 0 && e.Args[0] == "--share" && e.Args.Length > 1)
            {
                _ = OpenShareWindowAsync(e.Args[1], shutdownOnClose: true);
            }
            else
            {
                if (e.Args.Length > 0)
                {
                    HandleCommandLineArgs(e.Args, mainWindow);
                }

                mainWindow.Show();

                if (!string.IsNullOrWhiteSpace(lifecycleMessage))
                {
                    MessageBox.Show(
                        lifecycleMessage,
                        "ReStore Configuration",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

            _pipeServerThread = new Thread(ListenForCommands)
            {
                IsBackground = true
            };
            _pipeServerThread.Start();
        }

        private void ListenForCommands()
        {
            while (_isRunning)
            {
                try
                {
                    using var pipeServer = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipeServer.WaitForConnection();

                    using var reader = new StreamReader(pipeServer);
                    var command = reader.ReadToEnd();

                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            HandleCommand(command, mw);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Pipe server error: {ex}");

                    // A failure that recurs immediately (name already taken, ACL rejection)
                    // would otherwise spin this thread at full speed for the app's lifetime.
                    if (_isRunning)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
        }

        private void SendCommandToExistingInstance(string[] args)
        {
            string? command;
            if (args.Length > 0 && args[0] == "--share" && args.Length > 1)
            {
                command = $"--share \"{args[1]}\"";
            }
            else
            {
                command = string.Join(" ", args);
            }
            SendCommandToExistingInstance(command);
        }

        private static void SendCommandToExistingInstance(string command)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out);
                pipeClient.Connect(1000);

                using var writer = new StreamWriter(pipeClient);
                writer.Write(command);
                writer.Flush();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to send command to existing instance: {ex}");
            }
        }

        private static void BringExistingInstanceToFront()
        {
            SendCommandToExistingInstance("/show");
        }

        private void HandleCommand(string command, MainWindow mainWindow)
        {
            if (command.StartsWith("--share "))
            {
                var path = command.Substring(8).Trim('"');
                _ = OpenShareWindowAsync(path, shutdownOnClose: false);
                return;
            }

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();

            if (command == "/startWatcher")
            {
                ExecuteWatcherAction(mainWindow, true);
            }
            else if (command == "/stopWatcher")
            {
                ExecuteWatcherAction(mainWindow, false);
            }
        }

        private async Task OpenShareWindowAsync(string filePath, bool shutdownOnClose)
        {
            try
            {
                var configManager = await AppServices.GetConfigManagerAsync();
                var shareService = new ReStore.Core.src.sharing.ShareService(configManager, AppServices.Logger);

                await Dispatcher.InvokeAsync(() =>
                {
                    var shareWindow = new ReStore.Views.Windows.ShareWindow(filePath, shareService, configManager);
                    if (shutdownOnClose)
                    {
                        shareWindow.Closed += (_, _) =>
                        {
                            if (MainWindow == null || MainWindow.Visibility != Visibility.Visible)
                            {
                                Shutdown();
                            }
                        };
                    }
                    shareWindow.Show();
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open share window: {ex}");
                MessageBox.Show($"Failed to open share window: {ex.Message}", "ReStore Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ExecuteWatcherAction(MainWindow mainWindow, bool start)
        {
            mainWindow.Dispatcher.InvokeAsync(async () =>
            {
                var frame = mainWindow.FindName("ContentFrame") as System.Windows.Controls.Frame;

                if (frame?.Content is not DashboardPage)
                {
                    var dashboard = new DashboardPage();
                    frame?.Navigate(dashboard);

                    // Wait for the page to load before executing action
                    dashboard.Loaded += async (s, e) =>
                    {
                        if (start) await dashboard.StartWatcherAsync();
                        else dashboard.StopWatcher();
                    };
                }
                else if (frame?.Content is DashboardPage dashboard)
                {
                    if (start) await dashboard.StartWatcherAsync();
                    else dashboard.StopWatcher();
                }
            });
        }

        private void HandleCommandLineArgs(string[] args, MainWindow mainWindow)
        {
            foreach (var arg in args)
            {
                HandleCommand(arg, mainWindow);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isRunning = false;

            if (MainWindow is MainWindow mw)
            {
                mw.TrayManager?.Dispose();
            }

            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
            }
            _instanceMutex?.Dispose();

            base.OnExit(e);
        }

        /// <summary>
        /// Whether the Dashboard is about to open the first-run wizard. The wizard explains setup
        /// far better than a bare "config created" alert, and stacking both means the user dismisses
        /// a dialog before seeing the one that matters.
        /// </summary>
        private static async Task<bool> FirstRunSetupIsPendingAsync()
        {
            try
            {
                var configManager = await AppServices.GetConfigManagerAsync();
                var state = await AppServices.GetSystemStateAsync();

                var localBackupDirectoryExists =
                    configManager.StorageSources.TryGetValue("local", out var localConfig)
                    && !string.IsNullOrWhiteSpace(localConfig.Path)
                    && Directory.Exists(Environment.ExpandEnvironmentVariables(localConfig.Path));

                return FirstRunDetector.NeedsSetup(
                    configManager.StorageSources,
                    localBackupDirectoryExists,
                    state.GetTotalBackupCount());
            }
            catch (Exception ex)
            {
                // Never let this gate startup; worst case the user sees the extra notice.
                Trace.WriteLine($"First-run check for startup notice failed: {ex}");
                return false;
            }
        }

    }
}
