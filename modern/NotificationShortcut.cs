using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MemReduct.Fluent;

internal static partial class NotificationShortcut
{
    internal static string DefaultPath
    {
        get
        {
            var relativePath = Path.Combine("memreduct-winui", "Mem Reduct.lnk");
            var commonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), relativePath);
            return Installation.IsInstalled && File.Exists(commonPath) ? commonPath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), relativePath);
        }
    }

    internal static void Ensure(string path, string applicationId) => UserDataAccess.Run(() =>
    {
        var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
        try
        {
            var identityKey = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            var properties = (IPropertyStore)link;
            if (File.Exists(path))
            {
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                properties.GetValue(ref identityKey, out var existingIdentity);
                try
                {
                    if (existingIdentity.Type is 8 or 31 && Marshal.PtrToStringUni(existingIdentity.Pointer) == applicationId
                        && string.Equals(target.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
                }
                finally { PropVariantClear(ref existingIdentity); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            link.SetPath(Environment.ProcessPath!);
            link.SetArguments("");
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription(TrayIcon.DisplayName);
            link.SetIconLocation(TrayIcon.NotificationIconPath, 0);
            var identity = new PropertyValue { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(applicationId) };
            try { properties.SetValue(ref identityKey, ref identity); properties.Commit(); }
            finally { Marshal.FreeCoTaskMem(identity.Pointer); }
            ((IPersistFile)link).Save(path, true);
            SHChangeNotify(0x00002000, 0x1005, path, IntPtr.Zero); // Update the Shell's shortcut/name cache before notifying.
        }
        finally { Marshal.ReleaseComObject(link); }
    });

    internal static string? GetDisplayName(string applicationId)
    {
        string? name = null;
        UserDataAccess.WithDesktopShell(shell =>
        {
            object? folder = null, item = null;
            try
            {
                folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
                item = ((dynamic)folder!).ParseName(applicationId);
                if (item is not null) name = ((dynamic)item).Name;
            }
            finally
            {
                if (item is not null) Marshal.ReleaseComObject(item);
                if (folder is not null) Marshal.ReleaseComObject(folder);
            }
        });
        return name;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value);
        void Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint eventId, uint flags, string path, IntPtr secondItem);
}
