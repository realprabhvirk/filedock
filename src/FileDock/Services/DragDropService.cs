using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using FileDock.Native;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using IDataObject = System.Windows.IDataObject;

namespace FileDock.Services;

/// <param name="Started">A drag loop actually ran.</param>
/// <param name="Dropped">The user released over a target that accepted the data. This does not prove
/// the target finished copying or attaching anything.</param>
/// <param name="Unavailable">Paths left out because they no longer exist or cannot be reached.</param>
public sealed record DragOutResult(bool Started, bool Dropped, IReadOnlyList<string> Unavailable);

/// <summary>
/// Native Windows drag-and-drop in both directions.
/// <para>
/// Outgoing drags use the shell's own data object (the same one Explorer creates), which carries
/// CF_HDROP plus the Shell ID list formats that Outlook and other apps look for, and the shell
/// renders the standard drag image. Only DROPEFFECT_COPY is offered so a target can never move
/// or delete the originals.
/// </para>
/// </summary>
public static class DragDropService
{
    private static readonly short PreferredDropEffectFormat =
        unchecked((short)ShellInterop.RegisterClipboardFormat("Preferred DropEffect"));

    private static readonly short DropDescriptionFormat =
        unchecked((short)ShellInterop.RegisterClipboardFormat("DropDescription"));

    /// <summary>Runs a modal copy-only drag of the given paths. Must be called on the UI thread.</summary>
    public static DragOutResult DragOut(IntPtr hwnd, DependencyObject source, IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>(paths.Count);
        var resolved = new List<string>(paths.Count);
        var unavailable = new List<string>();

        try
        {
            foreach (string path in paths)
            {
                // SHParseDisplayName fails for paths that no longer exist, which is exactly the check we need.
                if (ShellInterop.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) == ShellInterop.S_OK && pidl != IntPtr.Zero)
                {
                    pidls.Add(pidl);
                    resolved.Add(path);
                }
                else
                {
                    unavailable.Add(path);
                }
            }

            if (resolved.Count == 0)
                return new DragOutResult(false, false, unavailable);

            IntPtr dataObject = CreateShellDataObject(pidls);
            if (dataObject != IntPtr.Zero)
            {
                try
                {
                    SetPreferredDropEffect(dataObject);
                    int hr = ShellInterop.SHDoDragDrop(hwnd, dataObject, IntPtr.Zero, ShellInterop.DROPEFFECT_COPY, out int effect);
                    Log.Info($"Drag out of {resolved.Count} item(s) ended: hr=0x{hr:X8}, effect={effect}");
                    if (hr >= 0)
                        return new DragOutResult(true, hr == ShellInterop.DRAGDROP_S_DROP && effect != ShellInterop.DROPEFFECT_NONE, unavailable);
                    Log.Warn("SHDoDragDrop failed; falling back to a plain file drop");
                }
                finally
                {
                    Marshal.Release(dataObject);
                }
            }
        }
        finally
        {
            foreach (IntPtr pidl in pidls)
                ShellInterop.ILFree(pidl);
        }

        // Fallback: a plain CF_HDROP payload through WPF. No shell drag image, but the same files.
        var data = new DataObject(DataFormats.FileDrop, resolved.ToArray());
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(ShellInterop.DROPEFFECT_COPY)));
        DragDropEffects result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        return new DragOutResult(true, result != DragDropEffects.None, unavailable);
    }

    /// <summary>Builds the shell data object for absolute ID lists, which may come from different folders.</summary>
    private static IntPtr CreateShellDataObject(List<IntPtr> pidls)
    {
        ShellInterop.IShellItemArray? array = null;
        try
        {
            if (ShellInterop.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out array) != ShellInterop.S_OK)
                return IntPtr.Zero;
            int hr = array.BindToHandler(IntPtr.Zero, ShellInterop.BHID_DataObject, ShellInterop.IID_IDataObject, out IntPtr dataObject);
            if (hr != ShellInterop.S_OK)
            {
                Log.Warn($"BindToHandler(BHID_DataObject) failed: 0x{hr:X8}");
                return IntPtr.Zero;
            }
            return dataObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn("Could not create the shell data object", ex);
            return IntPtr.Zero;
        }
        finally
        {
            if (array is not null)
                Marshal.FinalReleaseComObject(array);
        }
    }

    /// <summary>Tells targets that copying is the intended operation (Explorer would otherwise move within a volume).</summary>
    private static void SetPreferredDropEffect(IntPtr dataObject)
    {
        object rcw = Marshal.GetObjectForIUnknown(dataObject);
        try
        {
            if (rcw is ComIDataObject com)
                SetHGlobal(com, PreferredDropEffectFormat, BitConverter.GetBytes(ShellInterop.DROPEFFECT_COPY));
        }
        catch (COMException ex)
        {
            Log.Warn("Could not set Preferred DropEffect", ex);
        }
        finally
        {
            Marshal.ReleaseComObject(rcw);
        }
    }

    /// <summary>Reads a file-drop payload. Returns null when the data is not a file drop (text, URLs, images...).</summary>
    public static string[]? TryGetFileDrop(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
                return null;
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException or OutOfMemoryException)
        {
            // Some sources advertise a format and then fail to render it.
            Log.Warn("Could not read dropped data", ex);
            return null;
        }
    }

    /// <summary>
    /// Sets the caption shown under the shell drag image (for example "Add to FileDock").
    /// Only shell-aware sources such as Explorer display it; others ignore it.
    /// </summary>
    public static void SetDropDescription(IDataObject data, int type, string message, string insert)
    {
        if (data is not ComIDataObject com)
            return;
        var description = new ShellInterop.DROPDESCRIPTION { type = type, szMessage = message, szInsert = insert };
        int size = Marshal.SizeOf<ShellInterop.DROPDESCRIPTION>();
        var buffer = new byte[size];
        IntPtr temp = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(description, temp, false);
            Marshal.Copy(temp, buffer, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(temp);
        }

        try
        {
            SetHGlobal(com, DropDescriptionFormat, buffer);
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException)
        {
            // Not all sources accept extra formats; the drop itself still works.
        }
    }

    private static void SetHGlobal(ComIDataObject data, short format, byte[] bytes)
    {
        IntPtr handle = ShellInterop.GlobalAlloc(ShellInterop.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
            return;
        IntPtr pointer = ShellInterop.GlobalLock(handle);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        ShellInterop.GlobalUnlock(handle);

        var formatEtc = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null };
        try
        {
            // fRelease = true: the data object now owns the memory.
            data.SetData(ref formatEtc, ref medium, true);
        }
        catch
        {
            ShellInterop.GlobalFree(handle);
            throw;
        }
    }
}

/// <summary>
/// Wraps the shell's IDropTargetHelper so the drag image from Explorer stays visible while it is
/// over FileDock. Purely cosmetic; every method is safe to call if the helper is unavailable.
/// </summary>
internal sealed class DropImageHelper
{
    private readonly ShellInterop.IDropTargetHelper? _helper;

    public DropImageHelper()
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(ShellInterop.CLSID_DragDropHelper);
            _helper = type is null ? null : Activator.CreateInstance(type) as ShellInterop.IDropTargetHelper;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            Log.Warn("Drag image helper unavailable", ex);
        }
    }

    public void DragEnter(IntPtr hwnd, IDataObject data, Win32.POINT point, DragDropEffects effect)
    {
        if (_helper is not null && data is ComIDataObject com)
            Try(() => _helper.DragEnter(hwnd, com, ref point, (int)effect));
    }

    public void DragOver(Win32.POINT point, DragDropEffects effect)
    {
        if (_helper is not null)
            Try(() => _helper.DragOver(ref point, (int)effect));
    }

    public void DragLeave()
    {
        if (_helper is not null)
            Try(() => _helper.DragLeave());
    }

    public void Drop(IDataObject data, Win32.POINT point, DragDropEffects effect)
    {
        if (_helper is not null && data is ComIDataObject com)
            Try(() => _helper.Drop(com, ref point, (int)effect));
    }

    private static void Try(Func<int> call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn("Drag image helper call failed", ex);
        }
    }
}
