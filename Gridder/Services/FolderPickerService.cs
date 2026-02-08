using System.Runtime.InteropServices;

namespace Gridder.Services;

public class FolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        // COM dialog must run on the UI (STA) thread
        return await MainThread.InvokeOnMainThreadAsync(PickFolderWin32);
#elif MACCATALYST
        return await PickFolderMacAsync();
#else
        return null;
#endif
    }

#if WINDOWS
    /// <summary>
    /// Uses the classic Win32 IFileOpenDialog COM interface with FOS_PICKFOLDERS.
    /// This avoids the WinUI3 FolderPicker bug where "Select Folder" stays disabled.
    /// </summary>
    private static string? PickFolderWin32()
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);
            dialog.SetTitle("Select Music Folder");

            var hr = dialog.Show(GetMainWindowHandle());
            if (hr != 0) return null; // cancelled or error

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            var result = Marshal.PtrToStringUni(path);
            Marshal.FreeCoTaskMem(path);
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static nint GetMainWindowHandle()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winuiWindow)
            return WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
        return nint.Zero;
    }

    // COM interop for IFileOpenDialog
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(nint parent);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(FOS fos);
        void GetOptions();
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
        // ... more methods not needed
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(SIGDN sigdnName, out nint ppszName);
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_PICKFOLDERS = 0x20,
        FOS_FORCEFILESYSTEM = 0x40,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }
#endif

#if MACCATALYST
    private Task<string?> PickFolderMacAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var viewController = Platform.GetCurrentUIViewController();
                if (viewController == null)
                {
                    tcs.SetResult(null);
                    return;
                }

                var documentPicker = new UIKit.UIDocumentPickerViewController(
                    new[] { UniformTypeIdentifiers.UTTypes.Folder });
                documentPicker.AllowsMultipleSelection = false;
                documentPicker.Delegate = new FolderPickerDelegate(tcs);

                viewController.PresentViewController(documentPicker, true, null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private class FolderPickerDelegate : UIKit.UIDocumentPickerDelegate
    {
        private readonly TaskCompletionSource<string?> _tcs;

        public FolderPickerDelegate(TaskCompletionSource<string?> tcs)
        {
            _tcs = tcs;
        }

        public override void DidPickDocument(UIKit.UIDocumentPickerViewController controller, Foundation.NSUrl url)
        {
            url.StartAccessingSecurityScopedResource();
            _tcs.TrySetResult(url.Path);
        }

        public override void DidPickDocument(UIKit.UIDocumentPickerViewController controller, Foundation.NSUrl[] urls)
        {
            if (urls.Length > 0)
            {
                urls[0].StartAccessingSecurityScopedResource();
                _tcs.TrySetResult(urls[0].Path);
            }
            else
            {
                _tcs.TrySetResult(null);
            }
        }

        public override void WasCancelled(UIKit.UIDocumentPickerViewController controller)
        {
            _tcs.TrySetResult(null);
        }
    }
#endif
}
