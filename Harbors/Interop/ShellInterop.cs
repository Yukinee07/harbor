using System.Runtime.InteropServices;
using System.Text;

namespace Harbor.Interop;

/// <summary>Shell COM helpers: window/shortcut property stores, the Apps folder and packaged-app activation.</summary>
internal static class ShellInterop
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(string guid, uint id) { fmtid = new Guid(guid); pid = id; }
    }

    // PROPVARIANT is 24 bytes on x64: vt + 3 reserved words, then a 16 byte union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    public static readonly PROPERTYKEY PKEY_AppUserModel_ID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    public static readonly PROPERTYKEY PKEY_ItemNameDisplay = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
    public static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25", 2);

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var pv) < 0) return null;
        try
        {
            return pv.vt == 31 /* VT_LPWSTR */ && pv.ptr != IntPtr.Zero ? Marshal.PtrToStringUni(pv.ptr) : null;
        }
        finally { PropVariantClear(ref pv); }
    }

    /// <summary>Explicit AppUserModelID a window set for itself (also the hosted app's id for UWP frames).</summary>
    public static string? GetWindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) < 0 || store == null) return null;
            try { return ReadString(store, PKEY_AppUserModel_ID); }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- shortcuts

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        [PreserveSig] int GetIDList(out IntPtr pidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        [PreserveSig] int SetDescription(string name);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        [PreserveSig] int SetWorkingDirectory(string dir);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        [PreserveSig] int SetArguments(string args);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int cmd);
        [PreserveSig] int SetShowCmd(int cmd);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        [PreserveSig] int SetIconLocation(string path, int index);
        [PreserveSig] int SetRelativePath(string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint flags);
        [PreserveSig] int SetPath(string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    public sealed record ShortcutInfo(string Name, string? TargetPath, string? Arguments, string? AppId);

    public static ShortcutInfo? ReadShortcut(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                if (((IPersistFile)link).Load(lnkPath, 0) < 0) return null;
                var sb = new StringBuilder(1024);
                string? target = link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */) == 0 ? sb.ToString() : null;
                if (!string.IsNullOrEmpty(target)) target = Environment.ExpandEnvironmentVariables(target);
                sb.Clear();
                string? args = link.GetArguments(sb, sb.Capacity) == 0 ? sb.ToString() : null;
                string? appId = link is IPropertyStore ps ? ReadString(ps, PKEY_AppUserModel_ID) : null;
                return new ShortcutInfo(System.IO.Path.GetFileNameWithoutExtension(lnkPath),
                    string.IsNullOrEmpty(target) ? null : target, string.IsNullOrEmpty(args) ? null : args, appId);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- Apps folder

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems e);
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    const uint SIGDN_NORMALDISPLAY = 0;
    const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    public sealed record InstalledApp(string Name, string AppId)
    {
        public string ParsingName => @"shell:AppsFolder\" + AppId;
    }

    /// <summary>Everything in Start's "All apps" list, Win32 and packaged, with their AppUserModelIDs.</summary>
    public static List<InstalledApp> EnumerateAppsFolder()
    {
        var result = new List<InstalledApp>();
        var folderId = new Guid("1e87508d-89c2-42f0-8a7e-645a0f50ca58"); // FOLDERID_AppsFolder
        var iidItem = typeof(IShellItem).GUID;
        if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out var folder) < 0 || folder == null) return result;
        try
        {
            var bhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039"); // BHID_EnumItems
            var iidEnum = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out IntPtr pEnum) < 0) return result;
            var e = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            try
            {
                while (e.Next(1, out var item, out uint fetched) == 0 && fetched == 1 && item != null)
                {
                    try
                    {
                        string? name = TakeString(item, SIGDN_NORMALDISPLAY);
                        string? id = TakeString(item, SIGDN_PARENTRELATIVEPARSING);
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) result.Add(new InstalledApp(name, id));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(e); }
        }
        finally { Marshal.ReleaseComObject(folder); }
        return result;
    }

    /// <summary>Display name of a shell parsing name, e.g. "shell:AppsFolder\{AUMID}".</summary>
    public static string? GetDisplayName(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) < 0 || item == null) return null;
            try { return TakeString(item, SIGDN_NORMALDISPLAY); }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    static string? TakeString(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr p) < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ---------------------------------------------------------------- activation

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication(string appUserModelId, string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    /// <summary>Launches a packaged app by AUMID. Returns false if it isn't a packaged app.</summary>
    public static bool ActivatePackagedApp(string appId, string? arguments = null)
    {
        if (!appId.Contains('!')) return false;
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            try { return mgr.ActivateApplication(appId, arguments, 0, out _) >= 0; }
            finally { Marshal.ReleaseComObject(mgr); }
        }
        catch { return false; }
    }
}
