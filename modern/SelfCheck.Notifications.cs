using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
            NotificationShortcut.CheckReadOnlyShortcuts(directory, applicationId);
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

internal static partial class NotificationShortcut
{
    internal static void CheckReadOnlyShortcuts(string directory, string applicationId)
    {
        foreach (ushort type in new ushort[] { 8, 31 })
        {
            var path = Path.Combine(directory, "shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
            var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
            try
            {
                link.SetPath(Environment.ProcessPath!);
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                var value = new PropertyValue { Type = type, Pointer = type == 8 ? Marshal.StringToBSTR(applicationId) : Marshal.StringToCoTaskMemUni(applicationId) };
                try { ((IPropertyStore)link).SetValue(ref key, ref value); ((IPropertyStore)link).Commit(); }
                finally { PropVariantClear(ref value); }
                ((IPersistFile)link).Save(path, true);
            }
            finally { Marshal.ReleaseComObject(link); }
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                var before = File.ReadAllBytes(path);
                Ensure(path, applicationId);
                if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
                    throw new InvalidOperationException("A correct read-only notification shortcut must remain unchanged");
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
    }
}
