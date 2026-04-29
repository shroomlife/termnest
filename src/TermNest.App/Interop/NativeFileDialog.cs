using System.Runtime.InteropServices;

namespace TermNest.App.Interop;

internal static class NativeFileDialog
{
    private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public static string? PickExecutable(nint ownerHwnd, string title, string? initialFolder, string? suggestedFileName)
    {
        Type? dialogType = Type.GetTypeFromCLSID(FileOpenDialogClsid);
        if (dialogType == null)
        {
            return null;
        }

        IFileOpenDialog? dialog = null;
        IShellItem? folderItem = null;
        IShellItem? resultItem = null;
        IntPtr resultPath = IntPtr.Zero;

        try
        {
            dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Select");
            dialog.SetDefaultExtension("exe");
            dialog.SetOptions(FileOpenOptions.ForceFileSystem | FileOpenOptions.FileMustExist | FileOpenOptions.PathMustExist);
            dialog.SetFileTypes(2, new[]
            {
                new DialogFilterSpec("Executable files", "*.exe"),
                new DialogFilterSpec("All files", "*.*"),
            });
            dialog.SetFileTypeIndex(1);

            if (!string.IsNullOrWhiteSpace(suggestedFileName))
            {
                dialog.SetFileName(suggestedFileName);
            }

            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            {
                Guid shellItemIid = ShellItemIid;
                int folderHr = SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref shellItemIid, out folderItem);
                if (folderHr >= 0 && folderItem != null)
                {
                    dialog.SetFolder(folderItem);
                }
            }

            int hr = dialog.Show(ownerHwnd);
            if (hr == ErrorCancelled)
            {
                return null;
            }
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out resultItem);
            resultItem.GetDisplayName(ShellItemDisplayName.FileSystemPath, out resultPath);
            return Marshal.PtrToStringUni(resultPath);
        }
        finally
        {
            if (resultPath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(resultPath);
            }
            ReleaseComObject(resultItem);
            ReleaseComObject(folderItem);
            ReleaseComObject(dialog);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(nint parent);
        void SetFileTypes(
            uint cFileTypes,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] DialogFilterSpec[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FileOpenOptions fos);
        void GetOptions(out FileOpenOptions pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(ShellItemDisplayName sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct DialogFilterSpec
    {
        public DialogFilterSpec(string name, string spec)
        {
            Name = name;
            Spec = spec;
        }

        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string Spec;
    }

    [Flags]
    private enum FileOpenOptions : uint
    {
        FileMustExist = 0x00001000,
        PathMustExist = 0x00000800,
        ForceFileSystem = 0x00000040,
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000,
    }
}
