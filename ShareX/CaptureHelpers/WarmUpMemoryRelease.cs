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
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShareX;

// Hands back the memory the region capture warm-up leaves behind, without dropping what makes the next capture fast.
internal static class WarmUpMemoryRelease
{
    public static async Task RunAsync()
    {
        if (RegionCaptureIntegration.IsCaptureOpen) return;

        await Dispatcher.UIThread.InvokeAsync(RenderJanitorFramesAsync);

        if (RegionCaptureIntegration.IsCaptureOpen) return;

        // Off the UI thread, so a hotkey pressed meanwhile does not wait for the finalizers. The collections
        // themselves still pause every thread briefly, as any blocking collection does.
        // An aggressive collection also hands the GC's free space back to Windows, which a normal one keeps.
        await Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });

        // Checked and trimmed back to back on the UI thread (it takes milliseconds), so a capture cannot open in
        // between and have its fresh memory paged out.
        if (RegionCaptureIntegration.IsCaptureOpen) return;

        // The warm-up touched memory that is only needed again at the next capture.
        using Process self = Process.GetCurrentProcess();
        NativeMethods.SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    // A closed window's GPU surfaces are only destroyed at the next rendered frame, and a tray-only ShareX renders
    // none. Two frames in a 1x1 window that is never on screen (like the tray menu warm-up) free them.
    private static async Task RenderJanitorFramesAsync()
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

            for (int i = 0; i < 2 && compositor != null; i++)
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
