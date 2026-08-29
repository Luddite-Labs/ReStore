using System.Runtime.InteropServices;
using Microsoft.Win32;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// Whether Windows currently wants notifications suppressed (Focus Assist / Do not
    /// disturb / presentation mode / fullscreen).
    /// </summary>
    public interface INotificationSuppressionProbe
    {
        bool IsSuppressed();
    }

    /// <summary>
    /// Documented shell state first, undocumented registry blob only as a fallback.
    /// <c>SHQueryUserNotificationState</c> is the supported API and covers presentation mode,
    /// exclusive-fullscreen and legacy quiet time. Its coverage of the Windows 11 "Do not
    /// disturb" rename is not something we can rely on, so the registry probe stays as a
    /// second signal rather than being deleted.
    /// </summary>
    public sealed class WindowsNotificationSuppressionProbe(ILogger logger) : INotificationSuppressionProbe
    {
        private const string QuietMomentsKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.notifications.quiethoursprofile\Current";

        /// <summary>
        /// Values of <c>QUERY_USER_NOTIFICATION_STATE</c> (shellapi.h). Only
        /// <see cref="AcceptsNotifications"/> means "go ahead".
        /// </summary>
        private enum UserNotificationState
        {
            NotPresent = 1,
            Busy = 2,
            RunningD3dFullScreen = 3,
            PresentationMode = 4,
            AcceptsNotifications = 5,
            QuietTime = 6,
            App = 7
        }

        // DllImport rather than LibraryImport: the source-generated marshaller requires
        // AllowUnsafeBlocks project-wide, which is not worth enabling for a single BOOL/enum
        // signature that needs no custom marshalling.
        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHQueryUserNotificationState(out UserNotificationState state);

        public bool IsSuppressed()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            if (TryQueryShellState(out var suppressedByShell))
            {
                if (suppressedByShell)
                {
                    return true;
                }

                // The shell says notifications are accepted. On Windows 11 that answer does
                // not always account for "Do not disturb", so confirm against the registry
                // before deciding to interrupt the user.
                return IsQuietHoursProfileActive();
            }

            return IsQuietHoursProfileActive();
        }

        private bool TryQueryShellState(out bool suppressed)
        {
            suppressed = false;

            try
            {
                if (!SHQueryUserNotificationState(out var state))
                {
                    return false;
                }

                // QUNS_APP means a fullscreen app is running but notifications are still
                // allowed through; everything except that and AcceptsNotifications suppresses.
                suppressed = state is not (UserNotificationState.AcceptsNotifications or UserNotificationState.App);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                logger.Log($"SHQueryUserNotificationState unavailable: {ex.Message}", LogLevel.Debug);
                return false;
            }
            catch (Exception ex)
            {
                logger.Log($"Could not query shell notification state: {ex.Message}", LogLevel.Debug);
                return false;
            }
        }

        /// <summary>
        /// Best-effort read of the undocumented quiet-hours profile blob. Any failure is
        /// treated as "not suppressed", so a missing or reshaped value cannot silence the
        /// failure notifications that matter.
        /// </summary>
        private bool IsQuietHoursProfileActive()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(QuietMomentsKey);
                if (key?.GetValue("Data") is byte[] data)
                {
                    // Byte 0x12 carries the profile: 0 = off, non-zero = priority/alarms only.
                    const int profileOffset = 0x12;
                    if (data.Length > profileOffset)
                    {
                        return data[profileOffset] != 0;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log($"Could not read Focus Assist state: {ex.Message}", LogLevel.Debug);
            }

            return false;
        }
    }
}
