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

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

// Hands back the memory the region capture warm-up leaves behind, without dropping what makes the next capture fast.
internal static class WarmUpMemoryRelease
{
    private static readonly SemaphoreSlim ReleaseGate = new(1, 1);

    public static async Task RunAsync(Func<bool> canRelease)
    {
        Dispatcher.UIThread.VerifyAccess();
        await ReleaseGate.WaitAsync();
        try
        {
            if (!canRelease()) return;
            await ShareX.ScreenCaptureLib.Presentation.RegionCapture.RegionCaptureIntegration.TrimParkedWindowForIdleAsync();
            if (!canRelease()) return;
            await RenderJanitorFramesAsync(canRelease);
            if (!canRelease()) return;

            // Skia keeps shrunken copies of the last large image it scaled down (thumbnails, previews): ~12 MB after a
            // 4K capture, kept until the next scaled-down image replaces them.
            SKGraphics.PurgeResourceCache();
            FastPngWriter.ReleaseBuffers();
            ShareX.ScreenCaptureLib.DxgiDesktopCapturer.TrimIdle();

            // A collection already started cannot be cancelled. Recheck activity at every awaited boundary.
            await Task.Run(() =>
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true));
            if (!canRelease()) return;
            await Task.Run(GC.WaitForPendingFinalizers);
            if (!canRelease()) return;
            await Task.Run(() =>
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true));
            if (!canRelease()) return;

            // Guard and page-out are back-to-back on the UI thread; no freshly opened window is paged out.
            Volatile.Write(ref _pagedOut, PagePrivateDataOut());
        }
        finally
        {
            ReleaseGate.Release();
        }
    }

    // What the last release paged out; taken (once) by the next wake-up.
    private static MemoryRange[]? _pagedOut;

    // True from a release until the next wake-up.
    public static bool IsPagedOut => Volatile.Read(ref _pagedOut) != null;

    // Pages out again what came back since the release; the next wake-up reads back both. Starts no garbage collection.
    public static void PageOutAgain()
    {
        Dispatcher.UIThread.VerifyAccess();
        MemoryRange[]? earlier = Volatile.Read(ref _pagedOut);
        if (earlier == null) return;
        MemoryRange[] again = PagePrivateDataOut();
        if (again.Length == 0) return;
        if (Interlocked.CompareExchange(ref _pagedOut, Union(earlier, again), earlier) != earlier) Prefetch(again); // woken meanwhile
    }

    // Both lists in address order, overlapping ranges joined, so passes that page out the same memory add nothing.
    private static MemoryRange[] Union(MemoryRange[] a, MemoryRange[] b)
    {
        MemoryRange[] all = [.. a, .. b];
        Array.Sort(all, static (x, y) => ((long)x.VirtualAddress).CompareTo((long)y.VirtualAddress));
        var joined = new List<MemoryRange>(all.Length);
        foreach (MemoryRange range in all)
        {
            long start = (long)range.VirtualAddress, end = start + (long)range.NumberOfBytes;
            if (joined.Count > 0 && start < (long)joined[^1].VirtualAddress + (long)joined[^1].NumberOfBytes)
            {
                long lastStart = (long)joined[^1].VirtualAddress;
                long lastEnd = Math.Max(lastStart + (long)joined[^1].NumberOfBytes, end);
                joined[^1] = new MemoryRange { VirtualAddress = (IntPtr)lastStart, NumberOfBytes = (IntPtr)(lastEnd - lastStart) };
            }
            else
            {
                joined.Add(range);
            }
        }
        return joined.ToArray();
    }

    // First thing on a hotkey, a command from another ShareX, a job and a tray icon click: reads the paged-out data back
    // in a few large reads on a worker thread while the action starts, instead of one read per page as it touches them.
    public static void Wake()
    {
        MemoryRange[]? ranges = Interlocked.Exchange(ref _pagedOut, null);
        if (ranges != null) Prefetch(ranges);
    }

    private static void Prefetch(MemoryRange[] ranges)
    {
        if (ranges.Length == 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static r => PrefetchVirtualMemory(GetCurrentProcess(), (UIntPtr)r.Length, r, 0),
            ranges, preferLocal: false);
    }

    // Takes out of the working set the pages Task Manager's Memory column counts (resident pages that cannot be
    // shared), and nothing else; that includes JIT-compiled code, which is private. Code loaded from files and shared
    // sections stay: they are not counted there, Windows can drop them at no cost when it needs the RAM, and when they
    // were paged out too, the first capture after hours idle waited seconds for its code to be read back from disk page
    // by page (2026-11-19). The private pages go to Windows' standby or compressed memory, or the pagefile when RAM is
    // short, and come back on the next touch. Locked and large pages cannot leave RAM and are left alone.
    private static MemoryRange[] PagePrivateDataOut()
    {
        var ranges = new List<MemoryRange>();
        int infoSize = Marshal.SizeOf<MemoryBasicInformation>();
        int pageInfoSize = Marshal.SizeOf<WorkingSetPage>();
        IntPtr process = GetCurrentProcess();
        WorkingSetPage[] pages = new WorkingSetPage[4096];

        for (long address = 0; VirtualQuery((IntPtr)address, out MemoryBasicInformation region, (IntPtr)infoSize) != IntPtr.Zero;)
        {
            if (region.State == MEM_COMMIT && (region.Protect & PAGE_UNREADABLE) == 0)
            {
                long end = (long)region.BaseAddress + (long)region.RegionSize;
                for (long batch = (long)region.BaseAddress; batch < end; batch += pages.Length * PageSize)
                {
                    int count = (int)Math.Min(pages.Length, (end - batch) / PageSize);
                    for (int i = 0; i < count; i++) pages[i] = new WorkingSetPage { VirtualAddress = (IntPtr)(batch + i * PageSize) };
                    if (!QueryWorkingSetEx(process, pages, count * pageInfoSize)) continue;

                    long runStart = 0;
                    for (int i = 0; i <= count; i++)
                    {
                        bool counted = i < count && (pages[i].Attributes & Resident) != 0 && (pages[i].Attributes & LeaveAlone) == 0;
                        long page = batch + i * PageSize;
                        if (counted && runStart == 0)
                        {
                            runStart = page;
                        }
                        else if (!counted && runStart != 0)
                        {
                            // On memory that is not locked, VirtualUnlock only takes the pages out of the working set
                            // (and reports ERROR_NOT_LOCKED). Nothing is freed or changed. Locked pages are never in a
                            // run: on a run of only locked pages it would remove the lock.
                            VirtualUnlock((IntPtr)runStart, (IntPtr)(page - runStart));
                            ranges.Add(new MemoryRange { VirtualAddress = (IntPtr)runStart, NumberOfBytes = (IntPtr)(page - runStart) });
                            runStart = 0;
                        }
                    }
                }
            }

            long next = (long)region.BaseAddress + (long)region.RegionSize;
            if (next <= address) break;
            address = next;
        }

        return ranges.ToArray();
    }

    private const long PageSize = 4096;
    // QueryWorkingSetEx flags: resident (bit 0); shareable (15), locked by other code (22), large page (23).
    private const ulong Resident = 1;
    private const ulong LeaveAlone = (1UL << 15) | (1UL << 22) | (1UL << 23);
    private const uint MEM_COMMIT = 0x1000;
    // No-access and guard pages (the edge of a thread's stack) are left alone.
    private const uint PAGE_UNREADABLE = 0x01 | 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetPage
    {
        public IntPtr VirtualAddress;
        public ulong Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRange
    {
        public IntPtr VirtualAddress;
        public IntPtr NumberOfBytes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    private static extern bool QueryWorkingSetEx(IntPtr process, [In, Out] WorkingSetPage[] pages, int length);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualUnlock(IntPtr address, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern bool PrefetchVirtualMemory(IntPtr process, UIntPtr count, MemoryRange[] ranges, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // A closed window's GPU surfaces are only destroyed at the next rendered frame, and a tray-only ShareX renders
    // none. Two frames in a 1x1 window that is never on screen (like the tray menu warm-up) free them.
    private static async Task RenderJanitorFramesAsync(Func<bool> canRelease)
    {
        Window window = new()
        {
            Width = 1,
            Height = 1,
            MinWidth = 1,
            MinHeight = 1,
            Position = new PixelPoint(-32000, -32000),
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Topmost = false,
            // Not Opacity 0: Avalonia skips drawing a fully transparent window. It sits far off screen instead.
            Background = Brushes.Transparent,
            Content = new TrimControl { Width = 1, Height = 1 }
        };

        try
        {
            window.Show();
            Compositor? compositor = ElementComposition.GetElementVisual(window)?.Compositor;

            for (int i = 0; i < 2 && compositor != null && canRelease(); i++)
            {
                (window.Content as Control)?.InvalidateVisual();
                await Task.WhenAny(compositor.RequestCompositionBatchCommitAsync().Rendered, Task.Delay(1000));
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Warm-up memory release frame failed.");
        }
        finally
        {
            window.Close();
        }
    }

    // Draws nothing. On the render thread it flushes Skia and trims the Direct3D device.
    private sealed class TrimControl : Control
    {
        public override void Render(DrawingContext context) => context.Custom(new TrimOperation());
    }

    private sealed class TrimOperation : ICustomDrawOperation
    {
        // IDXGIDevice3
        private static readonly Guid IID_IDXGIDevice3 = new("6007896c-3244-4afd-bf18-a6d3beda5023");

        // IDXGIDevice3::Trim: IUnknown (3) + IDXGIObject (4) + IDXGIDevice (5) + IDXGIDevice1 (2) + IDXGIDevice2 (3) = slot 17.
        private const int TrimVtableSlot = 17;

        public Rect Bounds => new(0, 0, 1, 1);

        public bool HitTest(Point p) => false;

        // Never equal, so every frame draws it again.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;

            using ISkiaSharpApiLease lease = feature.Lease();
            ShareX.ScreenCaptureLib.Presentation.RegionCapture.DxgiAngleSurfaceSource.DrainPendingGpuCleanup(lease);
            GRContext? gr = lease.GrContext;
            if (gr == null) return;

            gr.Flush(true);
            TrimDirect3D(lease);
        }

        // The graphics driver keeps internal buffers to speed up later rendering. IDXGIDevice3::Trim lets it drop them.
        private static void TrimDirect3D(ISkiaSharpApiLease lease)
        {
            try
            {
                using ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi();
                if (api?.Context is not EglContext egl) return;

                EglInterface eglApi = egl.Display.EglInterface;
                if (!eglApi.QueryDisplayAttribExt(egl.Display.Handle, EglConsts.EGL_DEVICE_EXT, out IntPtr eglDevice) ||
                    !eglApi.QueryDeviceAttribExt(eglDevice, EglConsts.EGL_D3D11_DEVICE_ANGLE, out IntPtr d3dDevice) ||
                    d3dDevice == IntPtr.Zero)
                {
                    return;
                }

                // ANGLE's device pointer is borrowed. QueryInterface adds our own reference, released below.
                if (Marshal.QueryInterface(d3dDevice, in IID_IDXGIDevice3, out IntPtr dxgiDevice) != 0 || dxgiDevice == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    IntPtr vtable = Marshal.ReadIntPtr(dxgiDevice);
                    IntPtr trim = Marshal.ReadIntPtr(vtable, TrimVtableSlot * IntPtr.Size);
                    Marshal.GetDelegateForFunctionPointer<TrimDelegate>(trim)(dxgiDevice);
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Direct3D trim failed.");
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void TrimDelegate(IntPtr self);
    }
}
