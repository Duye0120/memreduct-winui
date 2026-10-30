#if !HELPER
#nullable disable // Shared with the protected Windows .NET Framework helper.
#endif
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace MemReduct.Fluent
{
    internal static class HelperUpdates
    {
        internal static void PrepareCacheDirectory()
        {
            if (!MemoryEngine.IsAdministrator) throw new UnauthorizedAccessException();
            var installation = new DirectoryInfo(Path.GetDirectoryName(HelperTasks.UiExecutable()));
            // Writable installations create the cache as the ordinary user; never alter their ACLs while elevated.
            try
            {
                HelperTrust.CheckPath(installation, false);
                for (var parent = installation.Parent; parent != null; parent = parent.Parent) HelperTrust.CheckPath(parent, true);
            }
            catch (UnauthorizedAccessException) { return; }
            var path = Path.Combine(installation.FullName, "Updates");
            if (Directory.Exists(path)) return;
            var directory = Directory.CreateDirectory(path);
            HelperTrust.CheckPath(directory, false);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }

        internal static void StartCleanup()
        {
            if (!MemoryEngine.IsAdministrator || !Directory.Exists(Path.Combine(HelperTrust.DirectoryPath, "Updates"))) return;
            using (var process = Process.Start(new ProcessStartInfo(HelperTrust.ExecutablePath, "--cleanup-updates " + HelperTrust.UserSid)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = HelperTrust.DirectoryPath }))
                if (process == null) throw new InvalidOperationException(UiText.Get("UpdateFailed"));
        }

        internal static Task CleanupAsync()
        { return CleanupAsync(Path.Combine(HelperTrust.DirectoryPath, "Updates"), HelperTrust.CheckPath); }

        internal static async Task CleanupAsync(string directory, Action<FileSystemInfo, bool> checkPath)
        {
            if (!Directory.Exists(directory)) return;
            checkPath(new DirectoryInfo(directory), false);
            foreach (var file in new DirectoryInfo(directory).GetFiles())
            {
                if (!UpdatePackage.IsCacheFile(file.Name)) continue;
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (!File.Exists(file.FullName)) break;
                    checkPath(new FileInfo(file.FullName), false);
                    try { File.Delete(file.FullName); break; }
                    catch (IOException) { if (attempt == 39) throw; }
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
        }

        internal static async Task InstallAsync()
        {
            HelperTrust.RequireProtected();
            var installation = Path.GetDirectoryName(HelperTasks.UiExecutable());
            // Version and release metadata come from protected code and GitHub, never from the writable UI.
            AppRelease release;
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            using (var timeout = new CancellationTokenSource(20000))
            {
                client.MaxResponseContentBufferSize = UpdatePackage.MaximumMetadata;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MemReductFluent/" + Assembly.GetExecutingAssembly().GetName().Version);
                using (var response = await client.GetAsync(UpdatePackage.ReleasesApi, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException(UiText.Get("UpdateNone"));
                    response.EnsureSuccessStatusCode();
                    release = UpdatePackage.ParseReleases(await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                        Assembly.GetExecutingAssembly().GetName().Version);
                }
            }
            if (release == null) throw new InvalidOperationException(UiText.Get("UpdateNone"));
            var directory = Path.Combine(HelperTrust.DirectoryPath, "Updates");
            Directory.CreateDirectory(directory);
            HelperTrust.CheckPath(new DirectoryInfo(directory), false);
            var target = Path.Combine(directory, UpdatePackage.InstallerName(release.Version));
            var partial = target + ".partial";
            foreach (var file in new[] { target, partial })
                if (File.Exists(file)) { HelperTrust.CheckPath(new FileInfo(file), false); File.Delete(file); }
            using (var timeout = new CancellationTokenSource(120000))
            using (var source = UserDataAccess.Run(() => new FileStream(UpdatePackage.CachePath(installation, release.Version),
                FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)))
                await UpdatePackage.CopyVerifiedAsync(source, partial, release, timeout.Token).ConfigureAwait(false);
            File.Move(partial, target);
            HelperTrust.CheckPath(new FileInfo(target), false);
            // Setup is elevated, but its finish/cancel hook reopens the UI with the ordinary shell token.
            using (var process = Process.Start(CreateSetupStartInfo(target, installation)))
                if (process == null) throw new InvalidOperationException(UiText.Get("UpdateFailed"));
        }

        internal static ProcessStartInfo CreateSetupStartInfo(string installer, string installation)
        {
            // Keep progress, cancellation and error dialogs; never allow setup to reboot Windows.
            return new ProcessStartInfo(installer, "/SP- /SILENT /NORESTART /FROMAPP=1 /DIR=\"" + installation + "\"")
                { UseShellExecute = true, WorkingDirectory = HelperTrust.DirectoryPath };
        }
    }
}
