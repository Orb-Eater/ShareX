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

using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Pure user-mode DXGI Desktop Duplication capturer with warm per-output sessions, pre-allocated staging textures,
/// pooled pinned virtual-desktop buffers, rotation-aware parallel sub-rectangle readback, and GPU-shared snapshots.
/// Releases duplication handles and staging/pooled buffers during idle so idle RAM and gaming MPO stay unaffected.
/// </summary>
public static unsafe class DxgiDesktopCapturer
{
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    private const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    private const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private static readonly object SyncLock = new();
    private static IDXGIFactory1? _factory;
    private static readonly List<AdapterSession> _adapters = [];
    private static readonly List<OutputSlot> _outputs = [];
    private static bool _topologyInitialized;
    private static bool _shutdown;

    // Pooled pinned buffers for full/repeated desktop sizes, cleared on TrimIdle via epoch bump.
    private static readonly object PoolLock = new();
    private static readonly Dictionary<int, Stack<byte[]>> _bufferPools = [];
    private static int _poolEpoch;
    private const int MaxBuffersPerSize = 2;

    /// <summary>
    /// Pre-creates D3D11 device(s) on background startup so the first capture avoids ~130 ms cold D3D11 init.
    /// Does not start desktop duplication (no DWM/MPO impact while idle).
    /// </summary>
    public static void WarmUpDevice()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (SyncLock)
        {
            if (_shutdown) return;
            try
            {
                EnsureTopologyLocked();
                foreach (AdapterSession adapter in _adapters)
                {
                    adapter.EnsureDevice();
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "DXGI device warm-up failed.");
            }
        }
    }

    /// <summary>
    /// Captures <paramref name="rect"/> (virtual desktop screen coordinates) via warm DXGI Desktop Duplication.
    /// Returns null on any access loss, unsupported HDR format, desktop switch, UAC, or sandbox restriction so
    /// callers cleanly fall back to GDI BitBlt.
    /// </summary>
    public static SKBitmap? TryCaptureToBitmap(Rectangle rect, bool allowHdrOutputs = false)
    {
        if (!OperatingSystem.IsWindows() || rect.Width <= 0 || rect.Height <= 0) return null;

        lock (SyncLock)
        {
            if (_shutdown) return null;
            try
            {
                if (!EnsureTopologyLocked() || _outputs.Count == 0) return null;

                List<OutputSlot> intersecting = FindIntersectingOutputsLocked(rect);
                if (intersecting.Count == 0) return null;

                foreach (OutputSlot slot in intersecting)
                {
                    if (!allowHdrOutputs && slot.IsHdr) return null;
                }

                if (!AcquireOutputsLocked(intersecting))
                {
                    return null;
                }

                return ReadbackOutputsToBitmapLocked(rect, intersecting);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "DXGI desktop capture failed; falling back to GDI.");
                ReleaseAllDuplicationsLocked();
                return null;
            }
        }
    }

    /// <summary>
    /// Captures a frozen GPU-resident multi-monitor snapshot covering <paramref name="rect"/> with zero CPU readback.
    /// Each intersecting output's latest frame is copied GPU-to-GPU into a shared D3D11 texture that ANGLE/Skia can
    /// render directly, and any later crop performs CPU staging readback only on the cropped rectangle.
    /// </summary>
    public static DxgiGpuDesktopSnapshot? TryCaptureGpuSnapshot(Rectangle rect, bool allowHdrOutputs = false)
    {
        if (!OperatingSystem.IsWindows() || rect.Width <= 0 || rect.Height <= 0) return null;

        lock (SyncLock)
        {
            if (_shutdown) return null;
            try
            {
                if (!EnsureTopologyLocked() || _outputs.Count == 0) return null;

                List<OutputSlot> intersecting = FindIntersectingOutputsLocked(rect);
                if (intersecting.Count == 0) return null;

                foreach (OutputSlot slot in intersecting)
                {
                    if (!allowHdrOutputs && slot.IsHdr) return null;
                }

                if (!AcquireOutputsLocked(intersecting))
                {
                    return null;
                }

                List<DxgiGpuOutputTile> tiles = new(intersecting.Count);
                try
                {
                    HashSet<ID3D11DeviceContext> contextsToFlush = [];
                    foreach (OutputSlot slot in intersecting)
                    {
                        if (slot.LastFrameTexture == null || slot.Adapter.Device == null || slot.Adapter.Context == null)
                        {
                            throw new InvalidOperationException("Output texture unavailable.");
                        }

                        // Frozen shared GPU snapshot texture for this capture overlay so live desktop updates don't tear it.
                        Texture2DDescription snapDesc = new()
                        {
                            Width = (uint)slot.TexWidth,
                            Height = (uint)slot.TexHeight,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                            CPUAccessFlags = CpuAccessFlags.None,
                            MiscFlags = ResourceOptionFlags.Shared
                        };

                        ID3D11Texture2D snapTex = slot.Adapter.Device.CreateTexture2D(snapDesc);
                        try
                        {
                            slot.Adapter.Context.CopyResource(snapTex, slot.LastFrameTexture);
                            contextsToFlush.Add(slot.Adapter.Context);

                            using IDXGIResource dxgiRes = snapTex.QueryInterface<IDXGIResource>();
                            IntPtr sharedHandle = dxgiRes.SharedHandle;
                            if (sharedHandle == IntPtr.Zero)
                            {
                                throw new InvalidOperationException("Shared handle unavailable for DXGI snapshot texture.");
                            }

                            tiles.Add(new DxgiGpuOutputTile(
                                slot.Adapter,
                                snapTex,
                                sharedHandle,
                                slot.Bounds,
                                slot.Rotation,
                                slot.TexWidth,
                                slot.TexHeight));
                        }
                        catch
                        {
                            snapTex.Dispose();
                            throw;
                        }
                    }

                    // Flush so the cross-device shared textures contain the copied frames before ANGLE samples them.
                    foreach (ID3D11DeviceContext ctx in contextsToFlush)
                    {
                        ctx.Flush();
                    }

                    return new DxgiGpuDesktopSnapshot(rect, tiles);
                }
                catch
                {
                    foreach (DxgiGpuOutputTile tile in tiles) tile.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "DXGI GPU snapshot capture failed; falling back.");
                ReleaseAllDuplicationsLocked();
                return null;
            }
        }
    }

    /// <summary>
    /// Releases all active DXGI duplication sessions, staging/cache textures, and pooled virtual-desktop buffers,
    /// and trims the kept D3D11 device(s) via IDXGIDevice3::Trim. Called by WarmUpMemoryRelease during idle so
    /// idle RAM stays ~6-7 MB and games see zero active DWM duplication / MPO interference.
    /// </summary>
    public static void TrimIdle()
    {
        ClearBufferPools();

        if (!OperatingSystem.IsWindows()) return;
        lock (SyncLock)
        {
            ReleaseAllDuplicationsLocked();
            foreach (AdapterSession adapter in _adapters)
            {
                adapter.ReleaseTransientResourcesAndTrim();
            }
        }
    }

    /// <summary>Releases all DXGI/D3D11 devices and buffers on application shutdown.</summary>
    public static void Shutdown()
    {
        ClearBufferPools();

        if (!OperatingSystem.IsWindows()) return;
        lock (SyncLock)
        {
            _shutdown = true;
            DisposeAllLocked();
        }
    }

    private static bool EnsureTopologyLocked()
    {
        if (_topologyInitialized && _factory != null && _factory.IsCurrent)
        {
            return true;
        }

        DisposeAllLocked();

        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; _factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ai++)
        {
            List<OutputSlot> adapterOutputs = [];
            AdapterSession? session = null;

            for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; oi++)
            {
                using (output)
                {
                    OutputDescription desc = output.Description;
                    if (!desc.AttachedToDesktop) continue;

                    Rectangle bounds = Rectangle.FromLTRB(
                        desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right,
                        desc.DesktopCoordinates.Bottom);
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                    bool isHdr = false;
                    try
                    {
                        using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                        isHdr = output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                    }
                    catch
                    {
                    }

                    session ??= new AdapterSession(adapter);
                    bool rotated90 = desc.Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
                    int texW = rotated90 ? bounds.Height : bounds.Width;
                    int texH = rotated90 ? bounds.Width : bounds.Height;

                    OutputSlot slot = new(session, oi, desc.DeviceName, bounds, desc.Rotation, texW, texH, isHdr);
                    adapterOutputs.Add(slot);
                }
            }

            if (session != null && adapterOutputs.Count > 0)
            {
                _adapters.Add(session);
                _outputs.AddRange(adapterOutputs);
            }
            else
            {
                adapter.Dispose();
            }
        }

        _topologyInitialized = true;
        return _outputs.Count > 0;
    }

    private static List<OutputSlot> FindIntersectingOutputsLocked(Rectangle rect)
    {
        List<OutputSlot> result = new(_outputs.Count);
        foreach (OutputSlot slot in _outputs)
        {
            if (rect.IntersectsWith(slot.Bounds))
            {
                result.Add(slot);
            }
        }
        return result;
    }

    private static bool AcquireOutputsLocked(List<OutputSlot> slots)
    {
        foreach (OutputSlot slot in slots)
        {
            if (!AcquireSingleOutputLocked(slot))
            {
                return false;
            }
        }
        return true;
    }

    private static bool AcquireSingleOutputLocked(OutputSlot slot)
    {
        if (!slot.Adapter.EnsureDevice()) return false;

        for (int recreateAttempt = 0; recreateAttempt < 2; recreateAttempt++)
        {
            if (slot.Duplication == null)
            {
                if (!TryCreateDuplicationLocked(slot, out int createHr))
                {
                    // E_ACCESSDENIED (locked/sandbox desktop) or UNSUPPORTED: do not retry, fall back cleanly to GDI.
                    return false;
                }
            }

            IDXGIOutputDuplication dupl = slot.Duplication!;
            bool acquired = false;
            IDXGIResource? resource = null;

            try
            {
                SharpGen.Runtime.Result hr = dupl.AcquireNextFrame(0, out OutduplFrameInfo frameInfo, out resource);

                if (hr.Success)
                {
                    acquired = true;
                    if (frameInfo.LastPresentTime != 0)
                    {
                        if (!CopyDesktopResourceToCacheLocked(slot, resource!)) return false;
                        slot.HasInitialFrame = true;
                        return true;
                    }

                    // Pointer-only update (LastPresentTime == 0): safe to reuse cache if we already have an initial frame.
                    if (slot.HasInitialFrame && slot.LastFrameTexture != null)
                    {
                        return true;
                    }

                    // Newly created duplication gave a pointer-only frame first: release and wait briefly for first desktop image.
                    dupl.ReleaseFrame();
                    acquired = false;
                    resource?.Dispose();
                    resource = null;
                    return TryAcquireInitialFrameLocked(slot);
                }

                int code = hr.Code;
                if (code == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    if (slot.HasInitialFrame && slot.LastFrameTexture != null)
                    {
                        // Zero desktop pixel updates since the previous acquire: LastFrameTexture is exact.
                        return true;
                    }

                    return TryAcquireInitialFrameLocked(slot);
                }

                if (code == DXGI_ERROR_ACCESS_LOST)
                {
                    slot.ReleaseDuplication();
                    continue; // Retry once on transient mode/desktop access loss.
                }

                if (code == DXGI_ERROR_SESSION_DISCONNECTED || code == E_ACCESSDENIED)
                {
                    slot.ReleaseDuplication();
                    return false;
                }

                slot.ReleaseDuplication();
                return false;
            }
            finally
            {
                if (acquired)
                {
                    try { dupl.ReleaseFrame(); } catch { }
                }
                resource?.Dispose();
            }
        }

        return false;
    }

    private static bool TryAcquireInitialFrameLocked(OutputSlot slot)
    {
        IDXGIOutputDuplication? dupl = slot.Duplication;
        if (dupl == null) return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            DwmFlush();
            IDXGIResource? resource = null;
            bool acquired = false;
            try
            {
                SharpGen.Runtime.Result hr = dupl.AcquireNextFrame(35, out OutduplFrameInfo info, out resource);
                if (hr.Success)
                {
                    acquired = true;
                    if (info.LastPresentTime != 0)
                    {
                        if (!CopyDesktopResourceToCacheLocked(slot, resource!)) return false;
                        slot.HasInitialFrame = true;
                        return true;
                    }
                }
                else if (hr.Code != DXGI_ERROR_WAIT_TIMEOUT)
                {
                    slot.ReleaseDuplication();
                    return false;
                }
            }
            finally
            {
                if (acquired)
                {
                    try { dupl.ReleaseFrame(); } catch { }
                }
                resource?.Dispose();
            }
        }

        // Static output produced no initial frame in time; fall back to GDI rather than returning a black frame.
        return false;
    }

    private static bool CopyDesktopResourceToCacheLocked(OutputSlot slot, IDXGIResource resource)
    {
        using ID3D11Texture2D desktopTex = resource.QueryInterface<ID3D11Texture2D>();
        Texture2DDescription desc = desktopTex.Description;
        if (desc.Format != Format.B8G8R8A8_UNorm)
        {
            return false;
        }

        if (slot.LastFrameTexture == null ||
            slot.LastFrameTexture.Description.Width != desc.Width ||
            slot.LastFrameTexture.Description.Height != desc.Height)
        {
            slot.LastFrameTexture?.Dispose();
            slot.StagingTexture?.Dispose();
            slot.StagingTexture = null;
            slot.TexWidth = (int)desc.Width;
            slot.TexHeight = (int)desc.Height;

            Texture2DDescription cacheDesc = new()
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            slot.LastFrameTexture = slot.Adapter.Device!.CreateTexture2D(cacheDesc);
        }

        slot.Adapter.Context!.CopyResource(slot.LastFrameTexture, desktopTex);
        return true;
    }

    private static bool TryCreateDuplicationLocked(OutputSlot slot, out int hresult)
    {
        hresult = 0;
        if (slot.Adapter.Device == null) return false;

        if (slot.Adapter.Adapter.EnumOutputs(slot.OutputIndex, out IDXGIOutput output).Failure)
        {
            return false;
        }

        using (output)
        {
            try
            {
                using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                slot.Duplication = output1.DuplicateOutput(slot.Adapter.Device);
                slot.HasInitialFrame = false;
                return true;
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                hresult = ex.HResult;
                if (hresult != E_ACCESSDENIED && hresult != DXGI_ERROR_NOT_CURRENTLY_AVAILABLE && hresult != DXGI_ERROR_UNSUPPORTED)
                {
                    DebugHelper.WriteLine($"DXGI DuplicateOutput failed on {slot.DeviceName}: 0x{hresult:X8}");
                }
                return false;
            }
        }
    }

    private static SKBitmap ReadbackOutputsToBitmapLocked(Rectangle captureRect, List<OutputSlot> slots)
    {
        int width = captureRect.Width;
        int height = captureRect.Height;
        int stride = checked(width * 4);

        SKBitmap bitmap = RentPooledBitmap(width, height, out byte[] buffer);
        try
        {
            // Determine whether the monitors completely cover captureRect; only fill dead L-shaped corners with opaque black.
            long coveredArea = 0;
            List<ReadbackJob> jobs = new(slots.Count);

            foreach (OutputSlot slot in slots)
            {
                Rectangle inter = Rectangle.Intersect(captureRect, slot.Bounds);
                if (inter.IsEmpty) continue;
                coveredArea += (long)inter.Width * inter.Height;

                int localX = inter.X - slot.Bounds.X;
                int localY = inter.Y - slot.Bounds.Y;
                Box srcBox = TransformDesktopRectToTextureBox(localX, localY, inter.Width, inter.Height,
                    slot.TexWidth, slot.TexHeight, slot.Rotation);

                ID3D11Texture2D staging = slot.EnsureStagingTexture();
                if (srcBox.Left == 0 && srcBox.Top == 0 && srcBox.Right == slot.TexWidth && srcBox.Bottom == slot.TexHeight)
                {
                    slot.Adapter.Context!.CopyResource(staging, slot.LastFrameTexture!);
                }
                else
                {
                    slot.Adapter.Context!.CopySubresourceRegion(staging, 0, (uint)srcBox.Left, (uint)srcBox.Top, 0, slot.LastFrameTexture!, 0, srcBox);
                }

                jobs.Add(new ReadbackJob(slot.Adapter.Context!, staging, srcBox, inter.X - captureRect.X, inter.Y - captureRect.Y,
                    inter.Width, inter.Height, slot.Rotation));
            }

            fixed (byte* dstPtr = buffer)
            {
                IntPtr dstBaseInt = (IntPtr)dstPtr;
                if (coveredArea < (long)width * height)
                {
                    FillOpaqueBlack(dstPtr, width, height, stride);
                }

                ExecuteMappedReadbackJobs(jobs, dstBaseInt, stride);
            }

            bitmap.NotifyPixelsChanged();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static SKBitmap ReadbackGpuTilesSubrect(Rectangle virtualBounds, IReadOnlyList<DxgiGpuOutputTile> tiles, Rectangle localCropRect)
    {
        Rectangle captureRect = new(
            virtualBounds.X + localCropRect.X,
            virtualBounds.Y + localCropRect.Y,
            localCropRect.Width,
            localCropRect.Height);

        int width = captureRect.Width;
        int height = captureRect.Height;
        int stride = checked(width * 4);

        lock (SyncLock)
        {
            SKBitmap bitmap = RentPooledBitmap(width, height, out byte[] buffer);
            try
            {
                long coveredArea = 0;
                List<ReadbackJob> jobs = new(tiles.Count);

                foreach (DxgiGpuOutputTile tile in tiles)
                {
                    Rectangle inter = Rectangle.Intersect(captureRect, tile.Bounds);
                    if (inter.IsEmpty) continue;
                    coveredArea += (long)inter.Width * inter.Height;

                    int localX = inter.X - tile.Bounds.X;
                    int localY = inter.Y - tile.Bounds.Y;
                    Box srcBox = TransformDesktopRectToTextureBox(localX, localY, inter.Width, inter.Height,
                        tile.TexWidth, tile.TexHeight, tile.Rotation);

                    int boxW = srcBox.Right - srcBox.Left;
                    int boxH = srcBox.Bottom - srcBox.Top;

                    // Each intersecting tile gets its own reusable staging texture slot on the adapter so multi-monitor crops
                    // never overwrite or dispose an earlier tile's staging texture before ExecuteMappedReadbackJobs maps them.
                    ID3D11Texture2D staging = tile.Adapter.EnsureSubrectStagingTexture(jobs.Count, boxW, boxH);
                    tile.Adapter.Context!.CopySubresourceRegion(staging, 0, 0, 0, 0, tile.Texture, 0, srcBox);

                    // The cropped data sits at (0,0) in staging with dimensions (boxW x boxH).
                    Box mappedBox = new(0, 0, 0, boxW, boxH, 1);
                    jobs.Add(new ReadbackJob(tile.Adapter.Context!, staging, mappedBox,
                        inter.X - captureRect.X, inter.Y - captureRect.Y,
                        inter.Width, inter.Height, tile.Rotation));
                }

                fixed (byte* dstPtr = buffer)
                {
                    IntPtr dstBaseInt = (IntPtr)dstPtr;
                    if (coveredArea < (long)width * height)
                    {
                        FillOpaqueBlack(dstPtr, width, height, stride);
                    }

                    ExecuteMappedReadbackJobs(jobs, dstBaseInt, stride);
                }

                bitmap.NotifyPixelsChanged();
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    internal static void ReadGpuTilesPixelPatch(Rectangle virtualBounds, IReadOnlyList<DxgiGpuOutputTile> tiles,
        int left, int top, int patchWidth, int patchHeight, Span<uint> destBgra)
    {
        destBgra.Fill(0xFF000000u);
        Rectangle patchRect = new(virtualBounds.X + left, virtualBounds.Y + top, patchWidth, patchHeight);

        lock (SyncLock)
        {
            foreach (DxgiGpuOutputTile tile in tiles)
            {
                Rectangle inter = Rectangle.Intersect(patchRect, tile.Bounds);
                if (inter.IsEmpty) continue;

                int localX = inter.X - tile.Bounds.X;
                int localY = inter.Y - tile.Bounds.Y;
                Box srcBox = TransformDesktopRectToTextureBox(localX, localY, inter.Width, inter.Height,
                    tile.TexWidth, tile.TexHeight, tile.Rotation);
                int boxW = srcBox.Right - srcBox.Left;
                int boxH = srcBox.Bottom - srcBox.Top;

                ID3D11Texture2D staging = tile.Adapter.EnsurePatchStagingTexture(boxW, boxH);
                ID3D11DeviceContext ctx = tile.Adapter.Context!;
                ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tile.Texture, 0, srcBox);
                if (ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).Failure)
                {
                    continue;
                }

                try
                {
                    fixed (uint* dstBase = destBgra)
                    {
                        CopyRotatedSubrect(
                            (byte*)mapped.DataPointer, (int)mapped.RowPitch,
                            0, 0, boxW, boxH,
                            (byte*)dstBase, patchWidth * 4,
                            inter.X - patchRect.X, inter.Y - patchRect.Y,
                            tile.Rotation, parallel: false);
                    }
                }
                finally
                {
                    ctx.Unmap(staging, 0);
                }
            }
        }
    }

    /// <summary>
    /// Transforms a desktop-oriented rectangle inside an output into its corresponding source rectangle
    /// inside the output's native (unrotated) scanout texture. Publicly testable for all 4 rotations.
    /// </summary>
    public static Box TransformDesktopRectToTextureBox(int dx, int dy, int dw, int dh, int texW, int texH, ModeRotation rotation)
    {
        int dx1 = dx + dw;
        int dy1 = dy + dh;
        return rotation switch
        {
            ModeRotation.Rotate90 => new Box(dy, texH - dx1, 0, dy1, texH - dx, 1),
            ModeRotation.Rotate180 => new Box(texW - dx1, texH - dy1, 0, texW - dx, texH - dy, 1),
            ModeRotation.Rotate270 => new Box(texW - dy1, dx, 0, texW - dy, dx1, 1),
            _ => new Box(dx, dy, 0, dx1, dy1, 1)
        };
    }

    private readonly struct ReadbackJob(ID3D11DeviceContext context, ID3D11Texture2D staging, Box srcBox,
        int dstX, int dstY, int dstW, int dstH, ModeRotation rotation)
    {
        public readonly ID3D11DeviceContext Context = context;
        public readonly ID3D11Texture2D Staging = staging;
        public readonly Box SrcBox = srcBox;
        public readonly int DstX = dstX;
        public readonly int DstY = dstY;
        public readonly int DstW = dstW;
        public readonly int DstH = dstH;
        public readonly ModeRotation Rotation = rotation;
    }

    private static void ExecuteMappedReadbackJobs(List<ReadbackJob> jobs, IntPtr dstBaseInt, int dstStride)
    {
        List<(ReadbackJob job, MappedSubresource mapped)> mappedList = new(jobs.Count);
        try
        {
            // Map all staging textures sequentially on their D3D11 contexts (D3D11 immediate context is single-threaded).
            foreach (ReadbackJob job in jobs)
            {
                if (job.Context.Map(job.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).Failure)
                {
                    throw new InvalidOperationException("Failed to map D3D11 staging texture for readback.");
                }
                mappedList.Add((job, mapped));
            }

            // Copy + rotate + set alpha=255 across all monitors and bands in parallel!
            if (mappedList.Count == 1)
            {
                var (job, mapped) = mappedList[0];
                CopyRotatedSubrect(
                    (byte*)mapped.DataPointer, (int)mapped.RowPitch,
                    job.SrcBox.Left, job.SrcBox.Top, job.SrcBox.Right - job.SrcBox.Left, job.SrcBox.Bottom - job.SrcBox.Top,
                    (byte*)dstBaseInt, dstStride, job.DstX, job.DstY, job.Rotation, parallel: true);
            }
            else
            {
                Parallel.For(0, mappedList.Count, i =>
                {
                    var (job, mapped) = mappedList[i];
                    CopyRotatedSubrect(
                        (byte*)mapped.DataPointer, (int)mapped.RowPitch,
                        job.SrcBox.Left, job.SrcBox.Top, job.SrcBox.Right - job.SrcBox.Left, job.SrcBox.Bottom - job.SrcBox.Top,
                        (byte*)dstBaseInt, dstStride, job.DstX, job.DstY, job.Rotation, parallel: true);
                });
            }
        }
        finally
        {
            for (int i = mappedList.Count - 1; i >= 0; i--)
            {
                mappedList[i].job.Context.Unmap(mappedList[i].job.Staging, 0);
            }
        }
    }

    /// <summary>
    /// Copies a sub-rectangle from a mapped BGRA texture into the destination virtual desktop buffer, applying
    /// clockwise DXGI ModeRotation and forcing every pixel's alpha byte to 255 in a single pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void CopyRotatedSubrect(byte* srcBase, int srcPitch, int srcX0, int srcY0, int srcW, int srcH,
        byte* dstBase, int dstStride, int dstX0, int dstY0, ModeRotation rotation, bool parallel)
    {
        if (srcW <= 0 || srcH <= 0) return;

        IntPtr srcBaseInt = (IntPtr)srcBase;
        IntPtr dstBaseInt = (IntPtr)dstBase;
        bool useParallel = parallel && (long)srcW * srcH >= 250_000;

        if (rotation is ModeRotation.Identity or ModeRotation.Unspecified)
        {
            int bands = useParallel ? Math.Min(8, Math.Max(1, srcH / 64)) : 1;
            void CopyBand(int y0, int y1)
            {
                byte* sb = (byte*)srcBaseInt;
                byte* db = (byte*)dstBaseInt;
                Vector<uint> alpha = new(0xFF000000u);
                int vecLen = Vector<uint>.Count;

                for (int y = y0; y < y1; y++)
                {
                    uint* s = (uint*)(sb + (long)(srcY0 + y) * srcPitch) + srcX0;
                    uint* d = (uint*)(db + (long)(dstY0 + y) * dstStride) + dstX0;
                    int x = 0;
                    for (; x <= srcW - vecLen; x += vecLen)
                    {
                        (new Vector<uint>(new ReadOnlySpan<uint>(s + x, vecLen)) | alpha)
                            .CopyTo(new Span<uint>(d + x, vecLen));
                    }
                    for (; x < srcW; x++) d[x] = s[x] | 0xFF000000u;
                }
            }

            if (bands <= 1) CopyBand(0, srcH);
            else Parallel.For(0, bands, b => CopyBand(srcH * b / bands, srcH * (b + 1) / bands));
            return;
        }

        if (rotation == ModeRotation.Rotate180)
        {
            int bands = useParallel ? Math.Min(8, Math.Max(1, srcH / 64)) : 1;
            void CopyBand180(int y0, int y1)
            {
                byte* sb = (byte*)srcBaseInt;
                byte* db = (byte*)dstBaseInt;
                for (int y = y0; y < y1; y++)
                {
                    uint* s = (uint*)(sb + (long)(srcY0 + y) * srcPitch) + srcX0;
                    uint* d = (uint*)(db + (long)(dstY0 + (srcH - 1 - y)) * dstStride) + dstX0;
                    for (int x = 0; x < srcW; x++)
                    {
                        d[srcW - 1 - x] = s[x] | 0xFF000000u;
                    }
                }
            }

            if (bands <= 1) CopyBand180(0, srcH);
            else Parallel.For(0, bands, b => CopyBand180(srcH * b / bands, srcH * (b + 1) / bands));
            return;
        }

        // Rotate90 (dx = srcH - 1 - y, dy = x) or Rotate270 (dx = y, dy = srcW - 1 - x) using 32x32 cache-friendly tiles.
        bool rot90 = rotation == ModeRotation.Rotate90;
        const int Tile = 32;
        int tileRows = (srcH + Tile - 1) / Tile;

        void CopyTileRow(int tr)
        {
            byte* sb = (byte*)srcBaseInt;
            byte* db = (byte*)dstBaseInt;
            int y0 = tr * Tile;
            int y1 = Math.Min(srcH, y0 + Tile);

            for (int x0 = 0; x0 < srcW; x0 += Tile)
            {
                int x1 = Math.Min(srcW, x0 + Tile);
                for (int y = y0; y < y1; y++)
                {
                    uint* sRow = (uint*)(sb + (long)(srcY0 + y) * srcPitch) + srcX0;
                    int dx = rot90 ? (srcH - 1 - y) : y;
                    int absDstX = dstX0 + dx;

                    if (rot90)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            uint* dRow = (uint*)(db + (long)(dstY0 + x) * dstStride);
                            dRow[absDstX] = sRow[x] | 0xFF000000u;
                        }
                    }
                    else
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            uint* dRow = (uint*)(db + (long)(dstY0 + (srcW - 1 - x)) * dstStride);
                            dRow[absDstX] = sRow[x] | 0xFF000000u;
                        }
                    }
                }
            }
        }

        if (!useParallel || tileRows <= 1)
        {
            for (int tr = 0; tr < tileRows; tr++) CopyTileRow(tr);
        }
        else
        {
            Parallel.For(0, tileRows, CopyTileRow);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FillOpaqueBlack(byte* ptr, int width, int height, int stride)
    {
        Vector<uint> black = new(0xFF000000u);
        int vecLen = Vector<uint>.Count;
        IntPtr baseInt = (IntPtr)ptr;
        int bands = (long)width * height >= 500_000 ? Math.Min(8, Math.Max(1, height / 64)) : 1;

        void FillBand(int y0, int y1)
        {
            byte* b = (byte*)baseInt;
            for (int y = y0; y < y1; y++)
            {
                uint* row = (uint*)(b + (long)y * stride);
                int x = 0;
                for (; x <= width - vecLen; x += vecLen)
                {
                    black.CopyTo(new Span<uint>(row + x, vecLen));
                }
                for (; x < width; x++) row[x] = 0xFF000000u;
            }
        }

        if (bands <= 1) FillBand(0, height);
        else Parallel.For(0, bands, band => FillBand(height * band / bands, height * (band + 1) / bands));
    }

    private static SKBitmap RentPooledBitmap(int width, int height, out byte[] buffer)
    {
        int stride = checked(width * 4);
        int byteCount = checked(stride * height);
        int epoch;

        lock (PoolLock)
        {
            epoch = _poolEpoch;
            if (_bufferPools.TryGetValue(byteCount, out Stack<byte[]>? stack) && stack.Count > 0)
            {
                buffer = stack.Pop();
            }
            else
            {
                buffer = GC.AllocateUninitializedArray<byte>(byteCount, pinned: true);
            }
        }

        // Hold a normal GCHandle while SKBitmap owns the POH buffer so GC cannot reclaim it before releaseProc runs.
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Normal);
        IntPtr pixelPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
        int capturedEpoch = epoch;
        int capturedSize = byteCount;
        byte[] capturedBuffer = buffer;

        SKBitmap bitmap = new();
        bool installed = false;
        try
        {
            installed = bitmap.InstallPixels(
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                pixelPtr,
                stride,
                (_, _) =>
                {
                    handle.Free();
                    ReturnPooledBuffer(capturedBuffer, capturedSize, capturedEpoch);
                });

            if (!installed)
            {
                handle.Free();
                ReturnPooledBuffer(capturedBuffer, capturedSize, capturedEpoch);
                bitmap.Dispose();
                throw new InvalidOperationException("Unable to install pooled pixel buffer into SKBitmap.");
            }

            return bitmap;
        }
        catch
        {
            if (!installed)
            {
                if (handle.IsAllocated) handle.Free();
                ReturnPooledBuffer(capturedBuffer, capturedSize, capturedEpoch);
                bitmap.Dispose();
            }
            throw;
        }
    }

    private static void ReturnPooledBuffer(byte[] buffer, int byteCount, int epoch)
    {
        lock (PoolLock)
        {
            if (epoch != _poolEpoch) return;
            if (!_bufferPools.TryGetValue(byteCount, out Stack<byte[]>? stack))
            {
                stack = new Stack<byte[]>(MaxBuffersPerSize);
                _bufferPools[byteCount] = stack;
            }
            if (stack.Count < MaxBuffersPerSize)
            {
                stack.Push(buffer);
            }
        }
    }

    private static void ClearBufferPools()
    {
        lock (PoolLock)
        {
            _poolEpoch++;
            _bufferPools.Clear();
        }
    }

    private static void ReleaseAllDuplicationsLocked()
    {
        foreach (OutputSlot slot in _outputs)
        {
            slot.ReleaseTransient();
        }
    }

    private static void DisposeAllLocked()
    {
        foreach (OutputSlot slot in _outputs)
        {
            slot.ReleaseTransient();
        }
        _outputs.Clear();

        foreach (AdapterSession adapter in _adapters)
        {
            adapter.Dispose();
        }
        _adapters.Clear();

        _factory?.Dispose();
        _factory = null;
        _topologyInitialized = false;
    }

    internal sealed class AdapterSession : IDisposable
    {
        public IDXGIAdapter1 Adapter { get; }
        public ID3D11Device? Device { get; private set; }
        public ID3D11DeviceContext? Context { get; private set; }
        private readonly List<(ID3D11Texture2D Tex, int W, int H)> _subrectStagings = [];
        private ID3D11Texture2D? _patchStaging;
        private int _patchW, _patchH;

        public AdapterSession(IDXGIAdapter1 adapter) => Adapter = adapter;

        public bool EnsureDevice()
        {
            if (Device != null && Context != null) return true;
            if (D3D11.D3D11CreateDevice(Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? dev, out ID3D11DeviceContext? ctx).Failure ||
                dev == null || ctx == null)
            {
                return false;
            }

            Device = dev;
            Context = ctx;
            return true;
        }

        public ID3D11Texture2D EnsureSubrectStagingTexture(int slotIndex, int width, int height)
        {
            while (_subrectStagings.Count <= slotIndex)
            {
                _subrectStagings.Add((null!, 0, 0));
            }

            var entry = _subrectStagings[slotIndex];
            if (entry.Tex != null && entry.W >= width && entry.H >= height)
            {
                return entry.Tex;
            }

            entry.Tex?.Dispose();
            int newW = Math.Max(width, entry.W);
            int newH = Math.Max(height, entry.H);
            ID3D11Texture2D tex = Device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)newW,
                Height = (uint)newH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
            _subrectStagings[slotIndex] = (tex, newW, newH);
            return tex;
        }

        public ID3D11Texture2D EnsurePatchStagingTexture(int width, int height)
        {
            int targetW = Math.Max(64, width);
            int targetH = Math.Max(64, height);
            if (_patchStaging != null && _patchW >= targetW && _patchH >= targetH)
            {
                return _patchStaging;
            }

            _patchStaging?.Dispose();
            _patchW = Math.Max(targetW, _patchW);
            _patchH = Math.Max(targetH, _patchH);
            _patchStaging = Device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_patchW,
                Height = (uint)_patchH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
            return _patchStaging;
        }

        public void ReleaseTransientResourcesAndTrim()
        {
            foreach (var entry in _subrectStagings)
            {
                entry.Tex?.Dispose();
            }
            _subrectStagings.Clear();

            _patchStaging?.Dispose();
            _patchStaging = null;
            _patchW = _patchH = 0;

            if (Device != null)
            {
                try
                {
                    Context?.ClearState();
                    Context?.Flush();
                    using IDXGIDevice3 dxgi3 = Device.QueryInterface<IDXGIDevice3>();
                    dxgi3.Trim();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            foreach (var entry in _subrectStagings)
            {
                entry.Tex?.Dispose();
            }
            _subrectStagings.Clear();
            _patchStaging?.Dispose();
            _patchStaging = null;
            Context?.Dispose();
            Context = null;
            Device?.Dispose();
            Device = null;
            Adapter.Dispose();
        }
    }

    private sealed class OutputSlot(AdapterSession adapter, uint outputIndex, string deviceName,
        Rectangle bounds, ModeRotation rotation, int texWidth, int texHeight, bool isHdr)
    {
        public AdapterSession Adapter { get; } = adapter;
        public uint OutputIndex { get; } = outputIndex;
        public string DeviceName { get; } = deviceName;
        public Rectangle Bounds { get; } = bounds;
        public ModeRotation Rotation { get; } = rotation;
        public int TexWidth { get; set; } = texWidth;
        public int TexHeight { get; set; } = texHeight;
        public bool IsHdr { get; } = isHdr;

        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? LastFrameTexture { get; set; }
        public ID3D11Texture2D? StagingTexture { get; set; }
        public bool HasInitialFrame { get; set; }

        public ID3D11Texture2D EnsureStagingTexture()
        {
            if (StagingTexture != null &&
                StagingTexture.Description.Width == (uint)TexWidth &&
                StagingTexture.Description.Height == (uint)TexHeight)
            {
                return StagingTexture;
            }

            StagingTexture?.Dispose();
            StagingTexture = Adapter.Device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)TexWidth,
                Height = (uint)TexHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
            return StagingTexture;
        }

        public void ReleaseDuplication()
        {
            Duplication?.Dispose();
            Duplication = null;
            HasInitialFrame = false;
        }

        public void ReleaseTransient()
        {
            ReleaseDuplication();
            StagingTexture?.Dispose();
            StagingTexture = null;
            LastFrameTexture?.Dispose();
            LastFrameTexture = null;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

/// <summary>One monitor's frozen shared D3D11 snapshot texture inside a <see cref="DxgiGpuDesktopSnapshot"/>.</summary>
public sealed class DxgiGpuOutputTile : IDisposable
{
    internal DxgiDesktopCapturer.AdapterSession Adapter { get; }
    internal ID3D11Texture2D Texture { get; }
    public IntPtr SharedHandle { get; }
    public Rectangle Bounds { get; }
    public ModeRotation Rotation { get; }
    public int TexWidth { get; }
    public int TexHeight { get; }

    internal DxgiGpuOutputTile(DxgiDesktopCapturer.AdapterSession adapter, ID3D11Texture2D texture, IntPtr sharedHandle,
        Rectangle bounds, ModeRotation rotation, int texWidth, int texHeight)
    {
        Adapter = adapter;
        Texture = texture;
        SharedHandle = sharedHandle;
        Bounds = bounds;
        Rotation = rotation;
        TexWidth = texWidth;
        TexHeight = texHeight;
    }

    public void Dispose()
    {
        Texture.Dispose();
    }
}

/// <summary>
/// Frozen multi-monitor GPU snapshot captured via DXGI Desktop Duplication with zero CPU readback.
/// Supports zero-copy ANGLE/Skia display, tiny magnifier patch readback, cropped rectangle readback on mouse release,
/// and lazy full-desktop CPU materialization only if annotation effects require CPU pixels.
/// </summary>
public sealed class DxgiGpuDesktopSnapshot : IDisposable
{
    private readonly List<DxgiGpuOutputTile> _tiles;
    private bool _disposed;

    public Rectangle VirtualBounds { get; }
    public int Width => VirtualBounds.Width;
    public int Height => VirtualBounds.Height;
    public IReadOnlyList<DxgiGpuOutputTile> Tiles => _tiles;

    internal DxgiGpuDesktopSnapshot(Rectangle virtualBounds, List<DxgiGpuOutputTile> tiles)
    {
        VirtualBounds = virtualBounds;
        _tiles = tiles;
    }

    /// <summary>Reads back only the specified local (0-based in VirtualBounds) crop rectangle to a CPU SKBitmap.</summary>
    public SKBitmap ReadbackSubrect(Rectangle localCropRect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DxgiDesktopCapturer.ReadbackGpuTilesSubrect(VirtualBounds, _tiles, localCropRect);
    }

    /// <summary>Materializes the entire desktop snapshot to a CPU SKBitmap (used lazily for annotation effects).</summary>
    public SKBitmap ReadbackFullBitmap()
    {
        return ReadbackSubrect(new Rectangle(0, 0, Width, Height));
    }

    /// <summary>Reads a small BGRA pixel patch for the region-capture magnifier without full-screen readback.</summary>
    public void ReadPixelPatch(int left, int top, int width, int height, Span<uint> destBgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DxgiDesktopCapturer.ReadGpuTilesPixelPatch(VirtualBounds, _tiles, left, top, width, height, destBgra);
    }

    /// <summary>Reads a small BGRA pixel patch directly into a new SKBitmap (e.g. cursor background patch).</summary>
    public unsafe SKBitmap ReadPixelPatchToBitmap(int left, int top, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SKBitmap patch = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        IntPtr ptr = patch.GetPixels();
        if (ptr != IntPtr.Zero)
        {
            ReadPixelPatch(left, top, width, height, new Span<uint>((void*)ptr, checked(width * height)));
            patch.NotifyPixelsChanged();
        }
        return patch;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (DxgiGpuOutputTile tile in _tiles)
        {
            tile.Dispose();
        }
        _tiles.Clear();
    }
}
