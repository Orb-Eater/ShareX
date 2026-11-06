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
using System.Drawing;
using System.Globalization;
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

    // Built ahead of the next capture on an idle UI thread, because loading the window takes longer than using it.
    private static RegionCaptureWindow? _spareWindow;
    private static CultureInfo? _spareWindowCulture;
    private static int _openCaptures;

    /// <summary>True while a real capture overlay is open. Warm-up runs do not count.</summary>
    public static bool IsCaptureOpen => _openCaptures > 0;

    private static void CreateSpareWindow()
    {
        try
        {
            if (_spareWindow == null)
            {
                _spareWindowCulture = CultureInfo.CurrentUICulture;
                _spareWindow = new RegionCaptureWindow();
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
        }
    }

    public static Task<AvaloniaRegionCaptureResult?> CaptureAsync(AvaloniaRegionCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Screenshot == null) request.CursorBitmap?.Dispose();
        ArgumentNullException.ThrowIfNull(request.Screenshot);

        try
        {
            AvaloniaBootstrapper.EnsureInitialized();
        }
        catch (Exception ex)
        {
            request.Screenshot.Dispose();
            request.CursorBitmap?.Dispose();
            return Task.FromException<AvaloniaRegionCaptureResult?>(ex);
        }

        TaskCompletionSource<AvaloniaRegionCaptureResult?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            RegionCaptureWindow? window = null;
            if (!request.Prewarm) _openCaptures++;

            try
            {
                ImageEditorIntegration.Initialize();
                RegionCaptureWindow? spare = _spareWindow;
                _spareWindow = null;
                // A spare built before a language change would show the old language. One that was closed cannot be shown.
                if (spare != null && (spare.TryGetPlatformHandle() == null || !Equals(_spareWindowCulture, CultureInfo.CurrentUICulture)))
                {
                    spare.Close(); // A window that was never shown must still be closed, or its HWND stays alive.
                    spare = null;
                }
                window = spare ?? new RegionCaptureWindow();
                window.Bind(request);
                AvaloniaRegionCaptureResult? result = await window.CaptureAsync();
                Dispatcher.UIThread.Post(CreateSpareWindow, DispatcherPriority.Background);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                if (window == null || !window.IsVisible)
                {
                    request.Screenshot.Dispose();
                    request.CursorBitmap?.Dispose();
                }

                completion.TrySetException(ex);
            }
            finally
            {
                if (!request.Prewarm) _openCaptures--;
            }
        });

        return completion.Task;
    }

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
