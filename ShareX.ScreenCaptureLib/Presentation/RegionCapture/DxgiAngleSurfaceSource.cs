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

using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Skia;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Presentation.Controls;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

/// <summary>
/// Zero-copy GPU display source for <see cref="SKCanvasControl"/> that imports a <see cref="DxgiGpuDesktopSnapshot"/>'s
/// shared D3D11 monitor textures directly into Avalonia's ANGLE EGL/Skia context without CPU readback.
/// Falls back cleanly to CPU readback if rendered on a software Skia target (e.g. RenderTargetBitmap or headless).
/// </summary>
public sealed class DxgiAngleSurfaceSource : IDirectSurfaceSource
{
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_BGRA8_EXT = 0x93A1;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_CLAMP_TO_EDGE = 0x812F;

    private const int EGL_D3D_TEXTURE_ANGLE = 0x33A3;
    private const int EGL_TEXTURE_FORMAT = 0x3080;
    private const int EGL_TEXTURE_TARGET = 0x3081;
    private const int EGL_TEXTURE_RGBA = 0x305E;
    private const int EGL_TEXTURE_2D = 0x305F;
    private const int EGL_BACK_BUFFER = 0x3084;
    private const int EGL_NONE = 0x3038;
    private const int EGL_SURFACE_TYPE = 0x3033;
    private const int EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_RED_SIZE = 0x3024;
    private const int EGL_GREEN_SIZE = 0x3023;
    private const int EGL_BLUE_SIZE = 0x3022;
    private const int EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_BIND_TO_TEXTURE_RGBA = 0x303A;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES2_BIT = 0x0004;
    private const int EGL_TRUE = 1;

    private static readonly ConcurrentQueue<TileBinding> PendingCleanup = new();
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> ConfigByDisplay = new();
    private static readonly object InteropInitLock = new();
    private static AngleInterop? _interop;
    private static bool _interopInitFailed;

    private readonly object _sync = new();
    private readonly DxgiGpuDesktopSnapshot _snapshot;
    private readonly bool _tilesCoverWholeCanvas;
    private List<TileBinding>? _bindings;
    private bool _bindingAttempted;
    private bool _retired;
    private SKBitmap? _softwareFallbackBitmap;

    public DxgiAngleSurfaceSource(DxgiGpuDesktopSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        long covered = 0;
        foreach (DxgiGpuOutputTile tile in snapshot.Tiles)
        {
            System.Drawing.Rectangle inter = System.Drawing.Rectangle.Intersect(snapshot.VirtualBounds, tile.Bounds);
            if (!inter.IsEmpty)
            {
                covered += (long)inter.Width * inter.Height;
            }
        }
        _tilesCoverWholeCanvas = covered >= (long)snapshot.Width * snapshot.Height;
    }

    public int Width => _snapshot.Width;
    public int Height => _snapshot.Height;

    /// <summary>
    /// Synchronously detaches from the underlying <see cref="DxgiGpuDesktopSnapshot"/> so the snapshot can be safely
    /// disposed on the UI thread without racing an in-flight render frame, and queues ANGLE/GL handles for cleanup on
    /// the next render thread turn.
    /// </summary>
    public void MarkRetired()
    {
        lock (_sync)
        {
            if (_retired) return;
            _retired = true;

            if (_bindings != null)
            {
                foreach (TileBinding binding in _bindings)
                {
                    PendingCleanup.Enqueue(binding);
                }
                _bindings = null;
            }

            _softwareFallbackBitmap?.Dispose();
            _softwareFallbackBitmap = null;
        }
    }

    public void Dispose() => MarkRetired();

    /// <summary>
    /// Drains retired ANGLE/GL/D3D11 shared-texture bindings on the render thread while the EGL context is current.
    /// Called automatically on every GPU render and during idle memory release.
    /// </summary>
    public static void DrainPendingGpuCleanup(ISkiaSharpApiLease lease)
    {
        // Only drain on a real GPU/EGL Skia target so software RenderTargetBitmap frames never discard GL/EGL handles.
        if (PendingCleanup.IsEmpty || lease.GrContext == null) return;

        AngleInterop? interop = _interop;
        if (interop == null) return;

        IntPtr currentDisplay = IntPtr.Zero;
        try
        {
            using ISkiaSharpPlatformGraphicsApiLease? probe = lease.TryLeasePlatformGraphicsApi();
            if (probe?.Context is EglContext eglProbe)
            {
                currentDisplay = eglProbe.Display.Handle;
            }
        }
        catch
        {
            return;
        }
        if (currentDisplay == IntPtr.Zero) return;

        List<TileBinding> matching = [];
        List<TileBinding>? deferred = null;
        while (PendingCleanup.TryDequeue(out TileBinding? binding))
        {
            if (binding.EglDisplay == currentDisplay || binding.EglDisplay == IntPtr.Zero)
            {
                matching.Add(binding);
            }
            else
            {
                (deferred ??= []).Add(binding);
            }
        }

        if (deferred != null)
        {
            foreach (TileBinding other in deferred)
            {
                PendingCleanup.Enqueue(other);
            }
        }

        if (matching.Count == 0) return;

        foreach (TileBinding b in matching)
        {
            b.Image?.Dispose();
            b.Image = null;
            b.BackendTexture?.Dispose();
            b.BackendTexture = null;
        }

        try
        {
            using ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi();
            if (api?.Context is EglContext egl && egl.Display.Handle == currentDisplay)
            {
                foreach (TileBinding b in matching)
                {
                    if (b.EglDisplay == currentDisplay)
                    {
                        // EGL spec order: release bound pbuffer texture before deleting GL texture and destroying pbuffer.
                        if (b.Pbuffer != IntPtr.Zero)
                        {
                            interop.EglReleaseTexImage(currentDisplay, b.Pbuffer, EGL_BACK_BUFFER);
                        }
                        if (b.GlTextureId != 0)
                        {
                            uint texId = b.GlTextureId;
                            interop.GlDeleteTextures(1, ref texId);
                            b.GlTextureId = 0;
                        }
                        if (b.Pbuffer != IntPtr.Zero)
                        {
                            interop.EglDestroySurface(currentDisplay, b.Pbuffer);
                            b.Pbuffer = IntPtr.Zero;
                        }
                    }
                    else if (b.EglDisplay == IntPtr.Zero && b.GlTextureId != 0)
                    {
                        uint texId = b.GlTextureId;
                        interop.GlDeleteTextures(1, ref texId);
                        b.GlTextureId = 0;
                    }
                }
            }
        }
        catch
        {
        }

        lease.GrContext.ResetContext(GRGlBackendState.TextureBinding);

        foreach (TileBinding b in matching)
        {
            if (b.Pbuffer != IntPtr.Zero || b.GlTextureId != 0)
            {
                // EGL/GL teardown did not complete (e.g. lost/changed context): re-enqueue and keep OpenedTexture alive.
                PendingCleanup.Enqueue(b);
            }
            else
            {
                b.OpenedTexture?.Dispose();
                b.OpenedTexture = null;
            }
        }
    }

    public void Render(ImmediateDrawingContext context, BitmapInterpolationMode interpolationMode,
        BitmapBlendingMode blendingMode, EdgeMode edgeMode)
    {
        ISkiaSharpApiLeaseFeature? feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (feature == null) return;

        using ISkiaSharpApiLease lease = feature.Lease();
        DrainPendingGpuCleanup(lease);

        lock (_sync)
        {
            if (_retired) return;

            SKSamplingOptions sampling = interpolationMode == BitmapInterpolationMode.HighQuality
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : interpolationMode.ToSKSamplingOptions();

            using SKPaint paint = new()
            {
                Color = new SKColor(255, 255, 255, (byte)(255 * lease.CurrentOpacity)),
                BlendMode = blendingMode.ToSKBlendMode(),
                IsAntialias = edgeMode != EdgeMode.Aliased
            };

            if (lease.GrContext != null)
            {
                if (!_bindingAttempted)
                {
                    _bindingAttempted = true;
                    _bindings = TryBindTilesOnAngle(lease, _snapshot);
                }

                if (_bindings != null)
                {
                    SKCanvas canvas = lease.SkCanvas;
                    SKRect canvasRect = new(0, 0, Width, Height);
                    canvas.Save();
                    canvas.ClipRect(canvasRect);

                    if (!_tilesCoverWholeCanvas)
                    {
                        using SKPaint bgPaint = new()
                        {
                            Color = new SKColor(0, 0, 0, (byte)(255 * lease.CurrentOpacity)),
                            BlendMode = blendingMode.ToSKBlendMode()
                        };
                        canvas.DrawRect(canvasRect, bgPaint);
                    }

                    int originX = _snapshot.VirtualBounds.X;
                    int originY = _snapshot.VirtualBounds.Y;

                    foreach (TileBinding binding in _bindings)
                    {
                        if (binding.Image == null) continue;

                        float destX = binding.Bounds.X - originX;
                        float destY = binding.Bounds.Y - originY;

                        canvas.Save();
                        switch (binding.Rotation)
                        {
                            case ModeRotation.Rotate90:
                                canvas.Translate(destX + binding.Bounds.Width, destY);
                                canvas.RotateDegrees(90f);
                                break;
                            case ModeRotation.Rotate180:
                                canvas.Translate(destX + binding.Bounds.Width, destY + binding.Bounds.Height);
                                canvas.RotateDegrees(180f);
                                break;
                            case ModeRotation.Rotate270:
                                canvas.Translate(destX, destY + binding.Bounds.Height);
                                canvas.RotateDegrees(270f);
                                break;
                            default:
                                canvas.Translate(destX, destY);
                                break;
                        }

                        canvas.DrawImage(binding.Image, 0, 0, sampling, paint);
                        canvas.Restore();
                    }

                    canvas.Restore();
                    return;
                }
            }

            // Software Skia or non-ANGLE fallback: read back once on demand so software targets never draw black.
            try
            {
                _softwareFallbackBitmap ??= _snapshot.ReadbackFullBitmap();
                using SKImage? img = SKImage.FromBitmap(_softwareFallbackBitmap);
                if (img != null)
                {
                    SKRect rect = new(0, 0, Width, Height);
                    lease.SkCanvas.DrawImage(img, rect, rect, sampling, paint);
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "DxgiAngleSurfaceSource software fallback failed.");
            }
        }
    }

    private static List<TileBinding>? TryBindTilesOnAngle(ISkiaSharpApiLease lease, DxgiGpuDesktopSnapshot snapshot)
    {
        if (!OperatingSystem.IsWindows() || lease.GrContext == null) return null;

        List<TileBinding> bindings = new(snapshot.Tiles.Count);

        try
        {
            using (ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi())
            {
                if (api?.Context is not EglContext egl) return null;

                AngleInterop? interop = EnsureInterop(egl.Display.EglInterface);
                if (interop == null) return null;

                IntPtr eglDisplay = egl.Display.Handle;
                if (!egl.Display.EglInterface.QueryDisplayAttribExt(eglDisplay, EglConsts.EGL_DEVICE_EXT, out IntPtr eglDevice) ||
                    !egl.Display.EglInterface.QueryDeviceAttribExt(eglDevice, EglConsts.EGL_D3D11_DEVICE_ANGLE, out IntPtr d3dDevicePtr) ||
                    d3dDevicePtr == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr eglConfig = GetOrChooseConfig(interop, eglDisplay);
                if (eglConfig == IntPtr.Zero) return null;

                // ANGLE owns d3dDevicePtr: AddRef before wrapping in a disposable Vortice wrapper.
                Marshal.AddRef(d3dDevicePtr);
                using ID3D11Device angleDevice = new(d3dDevicePtr);

                int[] pbufAttribs = [EGL_TEXTURE_FORMAT, EGL_TEXTURE_RGBA, EGL_TEXTURE_TARGET, EGL_TEXTURE_2D, EGL_NONE];

                foreach (DxgiGpuOutputTile tile in snapshot.Tiles)
                {
                    ID3D11Texture2D openedTex = angleDevice.OpenSharedResource<ID3D11Texture2D>(tile.SharedHandle);
                    IntPtr pbuffer = IntPtr.Zero;
                    uint glTex = 0;

                    try
                    {
                        pbuffer = interop.EglCreatePbufferFromClientBuffer(
                            eglDisplay, EGL_D3D_TEXTURE_ANGLE, openedTex.NativePointer, eglConfig, pbufAttribs);
                        if (pbuffer == IntPtr.Zero)
                        {
                            throw new InvalidOperationException("eglCreatePbufferFromClientBuffer returned null.");
                        }

                        interop.GlGenTextures(1, out glTex);
                        interop.GlBindTexture(GL_TEXTURE_2D, glTex);
                        interop.GlTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                        interop.GlTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                        interop.GlTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                        interop.GlTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

                        if (interop.EglBindTexImage(eglDisplay, pbuffer, EGL_BACK_BUFFER) == 0)
                        {
                            throw new InvalidOperationException("eglBindTexImage failed for shared D3D11 texture.");
                        }

                        interop.GlBindTexture(GL_TEXTURE_2D, 0);

                        bindings.Add(new TileBinding
                        {
                            EglDisplay = eglDisplay,
                            OpenedTexture = openedTex,
                            Pbuffer = pbuffer,
                            GlTextureId = glTex,
                            Bounds = tile.Bounds,
                            Rotation = tile.Rotation,
                            TexWidth = tile.TexWidth,
                            TexHeight = tile.TexHeight
                        });
                    }
                    catch
                    {
                        if (pbuffer != IntPtr.Zero)
                        {
                            interop.EglReleaseTexImage(eglDisplay, pbuffer, EGL_BACK_BUFFER);
                        }
                        if (glTex != 0) interop.GlDeleteTextures(1, ref glTex);
                        if (pbuffer != IntPtr.Zero)
                        {
                            interop.EglDestroySurface(eglDisplay, pbuffer);
                        }
                        openedTex.Dispose();
                        throw;
                    }
                }
            }

            // Platform API lease is now closed: sync Skia's GL state and wrap each GL texture as an SKImage.
            lease.GrContext.ResetContext(GRGlBackendState.TextureBinding);

            foreach (TileBinding binding in bindings)
            {
                GRGlTextureInfo glInfo = new(GL_TEXTURE_2D, binding.GlTextureId, GL_BGRA8_EXT);
                GRBackendTexture backendTexture = new(binding.TexWidth, binding.TexHeight, false, glInfo);
                SKImage? image = SKImage.FromTexture(
                    lease.GrContext, backendTexture, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888, SKAlphaType.Opaque);
                if (image == null)
                {
                    backendTexture.Dispose();
                    throw new InvalidOperationException("SKImage.FromTexture returned null for shared D3D11 tile.");
                }

                binding.BackendTexture = backendTexture;
                binding.Image = image;
            }

            return bindings;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Zero-copy D3D11->ANGLE texture bind failed; falling back to CPU readback.");
            foreach (TileBinding b in bindings)
            {
                PendingCleanup.Enqueue(b);
            }
            return null;
        }
    }

    private static IntPtr GetOrChooseConfig(AngleInterop interop, IntPtr eglDisplay)
    {
        if (ConfigByDisplay.TryGetValue(eglDisplay, out IntPtr cached))
        {
            return cached;
        }

        int[] attribs =
        [
            EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
            EGL_RED_SIZE, 8,
            EGL_GREEN_SIZE, 8,
            EGL_BLUE_SIZE, 8,
            EGL_ALPHA_SIZE, 8,
            EGL_BIND_TO_TEXTURE_RGBA, EGL_TRUE,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
            EGL_NONE
        ];

        IntPtr[] configs = new IntPtr[1];
        if (interop.EglChooseConfig(eglDisplay, attribs, configs, 1, out int numConfigs) != 0 &&
            numConfigs > 0 && configs[0] != IntPtr.Zero)
        {
            ConfigByDisplay[eglDisplay] = configs[0];
            return configs[0];
        }

        return IntPtr.Zero;
    }

    private static AngleInterop? EnsureInterop(EglInterface eglApi)
    {
        if (_interop != null) return _interop;
        if (_interopInitFailed) return null;

        lock (InteropInitLock)
        {
            if (_interop != null) return _interop;
            if (_interopInitFailed) return null;

            try
            {
                NativeLibrary.TryLoad("av_libglesv2.dll", out IntPtr lib);

                IntPtr Resolve(string name)
                {
                    if (lib != IntPtr.Zero && NativeLibrary.TryGetExport(lib, name, out IntPtr sym) && sym != IntPtr.Zero)
                    {
                        return sym;
                    }
                    IntPtr ansi = Marshal.StringToHGlobalAnsi(name);
                    try
                    {
                        return eglApi.GetProcAddress(ansi);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ansi);
                    }
                }

                T Load<T>(string name) where T : Delegate
                {
                    IntPtr ptr = Resolve(name);
                    if (ptr == IntPtr.Zero)
                    {
                        throw new EntryPointNotFoundException($"ANGLE/EGL symbol '{name}' not found.");
                    }
                    return Marshal.GetDelegateForFunctionPointer<T>(ptr);
                }

                _interop = new AngleInterop(
                    Load<EglCreatePbufferFromClientBufferDelegate>("eglCreatePbufferFromClientBuffer"),
                    Load<EglDestroySurfaceDelegate>("eglDestroySurface"),
                    Load<EglBindTexImageDelegate>("eglBindTexImage"),
                    Load<EglReleaseTexImageDelegate>("eglReleaseTexImage"),
                    Load<EglChooseConfigDelegate>("eglChooseConfig"),
                    Load<GlGenTexturesDelegate>("glGenTextures"),
                    Load<GlBindTextureDelegate>("glBindTexture"),
                    Load<GlDeleteTexturesDelegate>("glDeleteTextures"),
                    Load<GlTexParameteriDelegate>("glTexParameteri"));
                return _interop;
            }
            catch (Exception ex)
            {
                _interopInitFailed = true;
                DebugHelper.WriteException(ex, "Failed to resolve ANGLE EGL/GLES2 functions.");
                return null;
            }
        }
    }

    private sealed class TileBinding
    {
        public IntPtr EglDisplay;
        public ID3D11Texture2D? OpenedTexture;
        public IntPtr Pbuffer;
        public uint GlTextureId;
        public GRBackendTexture? BackendTexture;
        public SKImage? Image;
        public System.Drawing.Rectangle Bounds;
        public ModeRotation Rotation;
        public int TexWidth;
        public int TexHeight;
    }

    private sealed record AngleInterop(
        EglCreatePbufferFromClientBufferDelegate EglCreatePbufferFromClientBuffer,
        EglDestroySurfaceDelegate EglDestroySurface,
        EglBindTexImageDelegate EglBindTexImage,
        EglReleaseTexImageDelegate EglReleaseTexImage,
        EglChooseConfigDelegate EglChooseConfig,
        GlGenTexturesDelegate GlGenTextures,
        GlBindTextureDelegate GlBindTexture,
        GlDeleteTexturesDelegate GlDeleteTextures,
        GlTexParameteriDelegate GlTexParameteri);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr EglCreatePbufferFromClientBufferDelegate(IntPtr dpy, int buftype, IntPtr buffer, IntPtr config, int[] attribList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EglDestroySurfaceDelegate(IntPtr dpy, IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EglBindTexImageDelegate(IntPtr dpy, IntPtr surface, int buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EglReleaseTexImageDelegate(IntPtr dpy, IntPtr surface, int buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EglChooseConfigDelegate(IntPtr dpy, int[] attribList, [Out] IntPtr[] configs, int configSize, out int numConfig);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGenTexturesDelegate(int n, out uint textures);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindTextureDelegate(uint target, uint texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteTexturesDelegate(int n, ref uint textures);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlTexParameteriDelegate(uint target, uint pname, int param);
}
