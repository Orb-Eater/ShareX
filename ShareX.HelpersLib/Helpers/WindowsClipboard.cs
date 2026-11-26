#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShareX.HelpersLib;

// Writes image formats with the Win32 clipboard API. Avalonia synthesizes CF_DIB, CF_DIBV5 and CF_BITMAP
// from its own premultiplied, top-down 32 bpp bitmap, which Windows clipboard history and Office render
// incorrectly. Placing our own bottom-up CF_DIB lets Windows synthesize CF_BITMAP and CF_DIBV5 like
// System.Windows.Forms.Clipboard.SetImage did.
internal static class WindowsClipboard
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelay = 50;

    public static uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    // Returns false, writing nothing, when stillWanted says no. It is asked with the clipboard open,
    // so nothing can change the clipboard between the answer and the write.
    public static bool SetImage(IntPtr owner, byte[] png, byte[] dib, string html, Func<bool> stillWanted = null)
    {
        IntPtr dibHandle = dib != null ? AllocHGlobal(dib) : IntPtr.Zero;
        IntPtr pngHandle = IntPtr.Zero;
        IntPtr htmlHandle = IntPtr.Zero;
        try
        {
            if (png != null) pngHandle = AllocHGlobal(png);
            if (!string.IsNullOrEmpty(html)) htmlHandle = AllocHGlobal(Encoding.UTF8.GetBytes(html + "\0"));
        }
        catch
        {
            FreeHGlobal(htmlHandle);
            FreeHGlobal(pngHandle);
            FreeHGlobal(dibHandle);
            throw;
        }

        return SetImageHandles(owner, pngHandle, dibHandle, htmlHandle, stillWanted);
    }

    /// <summary>
    /// Commits pre-allocated GMEM_MOVEABLE clipboard handles prepared off the UI thread.
    /// Takes ownership of every non-zero handle and frees any that are not transferred to the OS clipboard.
    /// </summary>
    public static bool SetImageHandles(IntPtr owner, IntPtr pngHandle, IntPtr dibHandle, IntPtr htmlHandle, Func<bool> stillWanted = null)
    {
        try
        {
            // A null owner makes SetClipboardData fail after EmptyClipboard.
            if (owner == IntPtr.Zero) throw new InvalidOperationException("Clipboard owner window is unavailable.");
            Open(owner);
            try
            {
                if (stillWanted != null && !stillWanted()) return false;
                if (!NativeMethods.EmptyClipboard()) throw new Win32Exception();
                if (dibHandle != IntPtr.Zero)
                {
                    SetHandle(CF_DIB, ref dibHandle);
                }
                if (pngHandle != IntPtr.Zero)
                {
                    SetHandle(RegisterFormat(ClipboardHelpers.FORMAT_PNG), ref pngHandle);
                }
                if (htmlHandle != IntPtr.Zero)
                {
                    SetHandle(RegisterFormat(ClipboardDataFormats.Html), ref htmlHandle);
                }
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            // Any handle still non-zero was never handed to Windows and must be freed here.
            FreeHGlobal(htmlHandle);
            FreeHGlobal(pngHandle);
            FreeHGlobal(dibHandle);
        }
    }

    public static unsafe IntPtr AllocHGlobal(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return IntPtr.Zero;
        IntPtr handle = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (data.Length > 0)
                {
                    data.CopyTo(new Span<byte>((void*)pointer, data.Length));
                }
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
            return handle;
        }
        catch
        {
            NativeMethods.GlobalFree(handle);
            throw;
        }
    }

    public static void FreeHGlobal(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
        }
    }

    private static void Open(IntPtr owner)
    {
        // Another process may briefly hold the clipboard, for example a clipboard manager reading our last copy.
        for (int i = 0; ; i++)
        {
            if (NativeMethods.OpenClipboard(owner)) return;
            if (i >= OpenRetryCount) throw new Win32Exception();
            Thread.Sleep(OpenRetryDelay);
        }
    }

    private static uint RegisterFormat(string name)
    {
        uint format = NativeMethods.RegisterClipboardFormat(name);
        if (format == 0) throw new Win32Exception();
        return format;
    }

    private static void SetHandle(uint format, ref IntPtr handle)
    {
        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero) throw new Win32Exception();
        // The system owns the memory once SetClipboardData succeeds.
        handle = IntPtr.Zero;
    }
}
