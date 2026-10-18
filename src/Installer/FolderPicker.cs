using System;
using System.Runtime.InteropServices;

namespace SwyfInstaller
{
    /// <summary>The modern Windows "Select folder" dialog (IFileOpenDialog with FOS_PICKFOLDERS).</summary>
    internal static class FolderPicker
    {
        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        internal static string Pick(IntPtr owner, string title, string initialFolder)
        {
            var dialog = (IFileDialog)new FileOpenDialogRcw();
            try
            {
                dialog.SetOptions(FosPickFolders | FosForceFileSystem);
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder))
                {
                    var iid = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out var item);
                    dialog.SetFolder(item);
                }
                var hr = dialog.Show(owner);
                if (hr == ErrorCancelled) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out var result);
                result.GetDisplayName(SigdnFileSysPath, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
