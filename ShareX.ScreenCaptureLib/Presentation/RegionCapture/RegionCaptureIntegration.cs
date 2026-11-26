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

using Avalonia.Threading;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

/// <summary>UI-thread boundary for the Avalonia region capture experience.</summary>
public static class RegionCaptureIntegration
{
    /// <summary>Bounds of the last captured region, or of all regions when several were captured.</summary>
    public static Rectangle LastRegionRectangle { get; private set; }

    /// <summary>Screen rectangles of the last captured regions. Holds more than one item after a multi region capture.</summary>
    public static IReadOnlyList<Rectangle> LastRegionRectangles { get; private set; } = [];

    internal static void SetLastRegion(Rectangle bounds, IReadOnlyList<Rectangle> regions)
    {
        LastRegionRectangle = bounds;
        LastRegionRectangles = regions;
    }

    // Pre-warmed hidden window reused across captures to avoid per-hotkey visual tree + HWND + swapchain construction.
    private static RegionCaptureWindow? _parkedWindow;
    private static RegionCaptureWindow? _trimmingWindow;
    private static CultureInfo? _parkedWindowCulture;
    private static string? _parkedWindowScreens;
    private static Avalonia.Styling.ThemeVariant? _parkedWindowTheme;
    private static int _openCaptures;
    private static bool _isShutdown;

    /// <summary>True while a real capture overlay is open. Warm-up runs do not count.</summary>
    public static bool IsCaptureOpen => _openCaptures > 0;

    /// <summary>True while <see cref="TrimParkedWindowForIdleAsync"/> briefly renders a 1x1 off-screen frame to shrink the swapchain.</summary>
    public static bool IsTrimmingIdleWindow { get; private set; }

    /// <summary>Raised on the UI thread when a real (non-prewarm) region capture finishes so idle memory release can schedule.</summary>
    public static event Action? CaptureEnded;

    private static void CloseWindowQuietly(RegionCaptureWindow? window)
    {
        if (window != null && !window.IsPermanentlyClosed)
        {
            window.AllowReuse = false;
            try { window.Close(); } catch { }
        }
    }

    private static void CreateSpareWindow()
    {
        try
        {
            if (_parkedWindow == null && !IsCaptureOpen && !IsTrimmingIdleWindow && !_isShutdown)
            {
                _parkedWindowCulture = CultureInfo.CurrentUICulture;
                _parkedWindowScreens = GetScreenSetup();
                _parkedWindowTheme = ShareX.AvaloniaUI.Theming.ThemeManager.GetCurrentTheme();
                _parkedWindow = new RegionCaptureWindow { AllowReuse = true };
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
        }
    }

    /// <summary>Closes any parked region capture window on application shutdown.</summary>
    public static void Shutdown()
    {
        void CloseParked()
        {
            _isShutdown = true;
            RegionCaptureWindow? parked = _parkedWindow;
            RegionCaptureWindow? trimming = _trimmingWindow;
            _parkedWindow = null;
            _trimmingWindow = null;
            CloseWindowQuietly(parked);
            if (!ReferenceEquals(trimming, parked))
            {
                CloseWindowQuietly(trimming);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            CloseParked();
        }
        else
        {
            try { Dispatcher.UIThread.Post(CloseParked); } catch { }
        }
    }

    /// <summary>
    /// Shrinks the parked hidden region capture window's GPU swapchain down to 1x1 during idle so idle RAM and GPU/DWM
    /// commit stay at baseline while keeping the visual tree, EditorView, toolbar, and HWND warm for fast wake-up.
    /// </summary>
    public static async Task TrimParkedWindowForIdleAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess() || IsCaptureOpen || IsTrimmingIdleWindow || _isShutdown) return;
        RegionCaptureWindow? parked = _parkedWindow;
        if (parked == null || parked.IsPermanentlyClosed || parked.IsVisible) return;

        // Detach the parked window while awaiting the 1x1 trim render so a capture hotkey triggered mid-await
        // cannot race on the same window instance, while keeping _trimmingWindow visible to Shutdown/Release.
        CultureInfo? culture = _parkedWindowCulture;
        string? screens = _parkedWindowScreens;
        Avalonia.Styling.ThemeVariant? theme = _parkedWindowTheme;
        _parkedWindow = null;
        _trimmingWindow = parked;
        IsTrimmingIdleWindow = true;
        try
        {
            await parked.TrimSwapchainForIdleAsync();
        }
        finally
        {
            bool stillOwned = ReferenceEquals(_trimmingWindow, parked);
            if (stillOwned)
            {
                _trimmingWindow = null;
            }
            IsTrimmingIdleWindow = false;

            if (stillOwned && !_isShutdown && !parked.IsPermanentlyClosed &&
                parked.TryGetPlatformHandle() != null && _parkedWindow == null && !IsCaptureOpen)
            {
                _parkedWindow = parked;
                _parkedWindowCulture = culture;
                _parkedWindowScreens = screens;
                _parkedWindowTheme = theme;
            }
            else
            {
                CloseWindowQuietly(parked);
            }
        }
    }

    /// <summary>
    /// Drops the parked region capture window when idle memory release requests reclaiming its swapchain/HWND.
    /// Does nothing while a real capture is open.
    /// </summary>
    public static void ReleaseParkedWindowForIdle()
    {
        if (!Dispatcher.UIThread.CheckAccess() || IsCaptureOpen) return;
        RegionCaptureWindow? parked = _parkedWindow;
        RegionCaptureWindow? trimming = _trimmingWindow;
        _parkedWindow = null;
        _trimmingWindow = null;
        CloseWindowQuietly(parked);
        if (!ReferenceEquals(trimming, parked))
        {
            CloseWindowQuietly(trimming);
        }
    }

    // Bounds and DPI of every monitor, read fresh from Windows. A spare window built for another screen setup
    // could open with the wrong scale.
    private static string GetScreenSetup()
    {
        StringBuilder setup = new();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) =>
        {
            // An exception must not cross the native callback frame: stop and return what was read so far.
            try
            {
                MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref info);
                if (GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) != 0) dpiX = dpiY = 0;
                setup.Append(info.rcMonitor.left).Append(',').Append(info.rcMonitor.top).Append(',')
                    .Append(info.rcMonitor.right).Append(',').Append(info.rcMonitor.bottom).Append('@')
                    .Append(dpiX).Append(',').Append(dpiY).Append(';');
                return true;
            }
            catch
            {
                setup.Append("error;");
                return false;
            }
        }, IntPtr.Zero);

        return setup.ToString();
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    // dpiType 0: MDT_EFFECTIVE_DPI.
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static Task<AvaloniaRegionCaptureResult?> CaptureAsync(AvaloniaRegionCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Screenshot == null && request.GpuSnapshot == null)
        {
            request.CursorBitmap?.Dispose();
            throw new ArgumentException("Either Screenshot or GpuSnapshot must be provided.", nameof(request));
        }

        void DisposeRequestResources()
        {
            request.Screenshot?.Dispose();
            request.GpuSnapshot?.Dispose();
            request.CursorBitmap?.Dispose();
        }

        try
        {
            AvaloniaBootstrapper.EnsureInitialized();
        }
        catch (Exception ex)
        {
            DisposeRequestResources();
            return Task.FromException<AvaloniaRegionCaptureResult?>(ex);
        }

        // A warm-up window that cannot sit entirely above the desktop (a very tall capture area) would show on screen.
        if (request.Prewarm && !RegionCaptureWindow.CanWarmUpOffScreen(request.ScreenBounds, CaptureHelpers.GetScreenBounds().Y))
        {
            DisposeRequestResources();
            return Task.FromResult<AvaloniaRegionCaptureResult?>(null);
        }

        TaskCompletionSource<AvaloniaRegionCaptureResult?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            RegionCaptureWindow? window = null;
            if (!request.Prewarm) _openCaptures++;
            long start = Stopwatch.GetTimestamp();

            try
            {
                ImageEditorIntegration.Initialize();
                RegionCaptureWindow? parked = _parkedWindow;
                _parkedWindow = null;

                CultureInfo currentCulture = CultureInfo.CurrentUICulture;
                string currentScreens = GetScreenSetup();
                Avalonia.Styling.ThemeVariant currentTheme = ShareX.AvaloniaUI.Theming.ThemeManager.GetCurrentTheme();

                // A parked window built before a language, theme, or display change cannot be reused safely.
                if (parked != null && (parked.IsPermanentlyClosed ||
                    parked.TryGetPlatformHandle() == null ||
                    !Equals(_parkedWindowCulture, currentCulture) ||
                    _parkedWindowScreens != currentScreens ||
                    !Equals(_parkedWindowTheme, currentTheme)))
                {
                    parked.AllowReuse = false;
                    if (!parked.IsPermanentlyClosed) parked.Close();
                    parked = null;
                }

                bool reused = parked != null && parked.HasOpenedOnce;
                window = parked ?? new RegionCaptureWindow { AllowReuse = true };
                window.AllowReuse = true;
                window.Bind(request);
                AvaloniaRegionCaptureResult? result = await window.CaptureAsync();

                if (!request.Prewarm) LogCaptureEnd(window.EndReason, result, start, reused);

                // Park the warm hidden window for the next capture unless another capture already took the slot or shutdown ran.
                if (!_isShutdown && !window.IsPermanentlyClosed && window.TryGetPlatformHandle() != null && _parkedWindow == null)
                {
                    _parkedWindow = window;
                    _parkedWindowCulture = currentCulture;
                    _parkedWindowScreens = currentScreens;
                    _parkedWindowTheme = currentTheme;
                }
                else if (!window.IsPermanentlyClosed)
                {
                    window.AllowReuse = false;
                    window.Close();
                }

                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                if (!request.Prewarm)
                {
                    try { DebugHelper.WriteLine($"Region capture ended: failed after {ElapsedMilliseconds(start)} ms ({ex.GetType().Name}: {ex.Message})."); }
                    catch { } // Logging only: the failure below must still reach the caller.
                }

                if (window == null || !window.IsVisible)
                {
                    DisposeRequestResources();
                }

                if (window != null && !window.IsPermanentlyClosed)
                {
                    window.AllowReuse = false;
                    try { window.Close(); } catch { }
                }

                completion.TrySetException(ex);
            }
            finally
            {
                if (!request.Prewarm)
                {
                    _openCaptures--;
                    try { CaptureEnded?.Invoke(); } catch { }
                }
                if (_parkedWindow == null)
                {
                    Dispatcher.UIThread.Post(CreateSpareWindow, DispatcherPriority.Background);
                }
            }
        });

        return completion.Task;
    }

    // One line per capture, so the log shows whether a capture was taken or cancelled and why.
    // It must never turn a good capture into a failed one, so nothing it does may throw.
    private static void LogCaptureEnd(string reason, AvaloniaRegionCaptureResult? result, long start, bool reusedWindow)
    {
        try
        {
            long elapsed = ElapsedMilliseconds(start);
            string reuseTag = reusedWindow ? ", reused" : "";

            if (result == null)
            {
                DebugHelper.WriteLine($"Region capture ended: {reason}, nothing captured, open {elapsed} ms{reuseTag}.");
                return;
            }

            Rectangle area = result.ScreenRectangle;
            string annotated = result.ImageModified ? ", with annotations" : "";
            DebugHelper.WriteLine($"Region capture ended: {reason}, captured {result.Image.Width}x{result.Image.Height} at {area.X},{area.Y}{annotated}, open {elapsed} ms{reuseTag}.");
        }
        catch
        {
            // Logging only.
        }
    }

    private static long ElapsedMilliseconds(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    /// <summary>
    /// Lets the user draw a rectangle over the live desktop and returns it in screen pixels, or null when cancelled.
    /// The selection window is closed when the task completes, so the caller can capture the screen right after.
    /// </summary>
    public static Task<Rectangle?> SelectTransparentRegionAsync(Rectangle screenBounds, bool lockCursorToWindow)
    {
        AvaloniaBootstrapper.EnsureInitialized();

        TaskCompletionSource<Rectangle?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                RegionCaptureTransparentWindow window = new(screenBounds, lockCursorToWindow);
                Rectangle? selection = await window.SelectAsync();

                if (selection is Rectangle rectangle)
                {
                    SetLastRegion(rectangle, [rectangle]);
                }

                completion.TrySetResult(selection);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }
}
