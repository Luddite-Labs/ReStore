using Hardcodet.Wpf.TaskbarNotification;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// Routes scheduler outcomes to tray balloons, gated on the <c>notifications</c> config
    /// section and on whether Windows wants notifications suppressed. Deliberately silent on
    /// routine success.
    /// </summary>
    public sealed class BackupNotificationService(ILogger logger, INotificationSuppressionProbe? suppressionProbe = null)
    {
        private readonly INotificationSuppressionProbe _suppressionProbe =
            suppressionProbe ?? new WindowsNotificationSuppressionProbe(logger);

        private SystemTrayManager? _trayManager;
        private NotificationConfig _config = new();

        public void Configure(SystemTrayManager? trayManager, NotificationConfig? config)
        {
            _trayManager = trayManager;
            _config = config ?? new NotificationConfig();
        }

        public void NotifyCycleFinished(BackupCycleResult result)
        {
            if (result == null || !CanNotify())
            {
                return;
            }

            if (_config.NotifyOnBackupFailure && (result.DirectoriesFailed > 0 || result.SystemBackupFailed))
            {
                var detail = result.FailureMessages.Count > 0
                    ? result.FailureMessages[0]
                    : "See the activity log for details.";

                var suffix = result.FailureMessages.Count > 1
                    ? $" (+{result.FailureMessages.Count - 1} more)"
                    : string.Empty;

                Show("Backup failed", $"{detail}{suffix}", BalloonIcon.Error);
            }

            if (_config.NotifyOnVerificationFailure && result.VerificationsFailed > 0)
            {
                var detail = result.VerificationFailureMessages.Count > 0
                    ? result.VerificationFailureMessages[0]
                    : "A snapshot failed its integrity check.";

                Show("Backup integrity check failed", detail, BalloonIcon.Error);
            }

            // Opt-in only: notifying on every good backup trains people to ignore the ones
            // that matter.
            if (_config.NotifyOnEveryBackupSuccess && result.DirectoriesBackedUp > 0 && !result.HasFailures)
            {
                Show("Backup completed", $"{result.DirectoriesBackedUp} folder(s) backed up.", BalloonIcon.Info);
            }
        }

        public void NotifyRecovered(BackupCycleResult result)
        {
            if (!CanNotify() || !_config.NotifyOnRecovery)
            {
                return;
            }

            Show("Backups working again", "A scheduled backup succeeded after previous failures.", BalloonIcon.Info);
        }

        private bool CanNotify()
        {
            if (!_config.Enabled || _trayManager == null)
            {
                return false;
            }

            return !_suppressionProbe.IsSuppressed();
        }

        private void Show(string title, string message, BalloonIcon icon)
        {
            try
            {
                _trayManager!.ShowBalloonTip(title, message, icon);
            }
            catch (Exception ex)
            {
                // A failed toast must never take down a backup cycle.
                logger.Log($"Failed to show notification '{title}': {ex.Message}", LogLevel.Debug);
            }
        }
    }
}
