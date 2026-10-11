using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace FileDock.Native;

/// <summary>
/// Windows Shell declarations: item icons, the shell data object used for outgoing drags,
/// the drag-image helper for incoming drags, and "show in folder".
/// </summary>
internal static class ShellInterop
{
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    public static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    public const int S_OK = 0;
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DROPEFFECT_NONE = 0;
    public const int DROPEFFECT_COPY = 1;

    // ---------- Icons ----------

    [Flags]
    public enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int x, int y) { cx = x; cy = y; }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out DIBSECTION lpvObject);

    // ---------- Outgoing drag: shell data object ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(int flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IntPtr ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    [DllImport("shell32.dll")]
    public static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);

    /// <summary>
    /// Runs a modal OLE drag loop. With a null drop source the shell supplies a default one
    /// (Esc cancels, releasing the button drops) and renders the standard drag image.
    /// </summary>
    [DllImport("shell32.dll")]
    public static extern int SHDoDragDrop(IntPtr hwnd, IntPtr pdata, IntPtr pdsrc, int dwEffect, out int pdwEffect);

    // ---------- Incoming drag: drag image + drop description ----------

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwndTarget, ComIDataObject pDataObject, ref Win32.POINT ppt, int dwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref Win32.POINT ppt, int dwEffect);
        [PreserveSig] int Drop(ComIDataObject pDataObject, ref Win32.POINT ppt, int dwEffect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
    }

    public const int DROPIMAGE_INVALID = -1;
    public const int DROPIMAGE_NONE = 0;
    public const int DROPIMAGE_COPY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DROPDESCRIPTION
    {
        public int type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szMessage;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szInsert;
    }

    public const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    // ---------- Show in folder ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);
}
