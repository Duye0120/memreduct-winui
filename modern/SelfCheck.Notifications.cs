using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MemReduct.Fluent;

public sealed partial class MainWindow
{
    // Explicit visual check only: private identity/tray, no settings, tasks or real cleanup.
    internal async void RunNotificationPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var identity));
        string applicationId;
        try { applicationId = Marshal.PtrToStringUni(identity)!; }
        finally { Marshal.FreeCoTaskMem(identity); }
        Require(applicationId.StartsWith(TrayIcon.ApplicationId + ".NotificationPreview.", StringComparison.Ordinal),
            "Notification checks must not use the installed app identity");
        var shortcutDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "memreduct-winui-notification-checks", applicationId);
        var shortcutPath = Path.Combine(shortcutDirectory, "Mem Reduct.lnk");
        try
        {
            CheckNotificationPayload();
            NotificationShortcut.Ensure(shortcutPath, applicationId);
            await App.WaitUntilAsync(() => NotificationShortcut.GetDisplayName(applicationId) == "Mem Reduct", TimeSpan.FromSeconds(5));
            Require(NotificationShortcut.GetDisplayName(applicationId) == "Mem Reduct",
                "The Windows application catalog resolves the notification identity to Mem Reduct");
            Activate();
            await Task.Delay(500);
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + applicationId);
            Require((string?)key?.GetValue("DisplayName") == "Mem Reduct"
                && (string?)key?.GetValue("IconUri") == TrayIcon.NotificationIconPath,
                "Windows notification identity uses Mem Reduct and the app icon");
            using var tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowWindow,
                () => { }, () => { }, () => { }, () => false, () => { }, () => true, shortcutPath);
            Require(tray.IsVisible, "The isolated preview tray was added");
            tray.Update(82, CleanupFeedback.None, "Notification preview", false);
            tray.NotifySuccess("642 MB");
            await App.WaitUntilAsync(() => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + applicationId,
                "LastNotificationAddedTime", null) is long, TimeSpan.FromSeconds(5));
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + applicationId);
            Require(settings?.GetValue("LastNotificationAddedTime") is long,
                "Windows received the cleanup notification under the explicit application identity");
            File.WriteAllText(Path.Combine(directory, "notification-check.json"), JsonSerializer.Serialize(new
            {
                passed = true, applicationId, displayName = key!.GetValue("DisplayName"), shellDisplayName = NotificationShortcut.GetDisplayName(applicationId),
                icon = key.GetValue("IconUri"), notificationTime = settings!.GetValue("LastNotificationAddedTime"),
                language = UiText.LanguageTag, settingsUsed = false, cleanupExecuted = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            await Task.Delay(12000);
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(directory, "notification-check.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally
        {
            try
            {
                File.Delete(shortcutPath);
                if (Directory.Exists(shortcutDirectory)) Directory.Delete(shortcutDirectory);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + applicationId, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + applicationId, false);
            }
            finally { ExitWindow(); }
        }
    }

    private static void CheckNotificationPayload()
    {
        var trayData = new TrayIcon.NotifyIconData { Window = new IntPtr(1), Id = 1, Icon = new IntPtr(2), Tip = "82%" };
        var notification = TrayIcon.BuildSuccessNotification(trayData, "642 MB", new IntPtr(3));
        Require(Marshal.SizeOf<TrayIcon.NotifyIconData>() == 976 && notification.Flags == (0x10 | 0x40)
            && notification.InfoFlags == (4 | 0x10 | 0x20 | 0x80) && notification.BalloonIcon == new IntPtr(3)
            && notification.Icon == trayData.Icon && notification.InfoTitle == UiText.Get("CleanupSucceeded")
            && notification.Info == UiText.Get("CleanupNotification", "642 MB") && trayData.Flags == 0,
            "Success notifications use the app icon independently of tray digits, stay silent, respect quiet time and do not queue stale results");
    }

    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr identity);

}
