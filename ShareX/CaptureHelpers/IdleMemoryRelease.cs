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

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX;

// One delayed pass after use, never a recurring GC; while idle, a re-check every 5 minutes pages out again what a
// collection pulled back. All state and guards belong to the UI thread.
internal static class IdleMemoryRelease
{
    private static int _ticket;
    private static bool _initialized;
    private const int QuietMilliseconds = 8000;
    private const int RecheckMilliseconds = 5 * 60 * 1000;

    internal static void Initialize()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_initialized) return;
        _initialized = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnWindowActivity(window));
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => OnWindowActivity(window));
        RegionCaptureIntegration.CaptureEnded += Schedule;
    }

    private static void OnWindowActivity(Window window)
    {
        if (RegionCaptureIntegration.IsTrimmingIdleWindow) return;
        if (HoldsImage(window)) Schedule();
    }

    internal static void Schedule()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Schedule);
            return;
        }

        _ = RunWhenQuietAsync(++_ticket);
    }

    private static async Task RunWhenQuietAsync(int ticket)
    {
        try
        {
            await Task.Delay(QuietMilliseconds);
            if (!CanRelease(ticket)) return;
            await WarmUpMemoryRelease.RunAsync(() => CanRelease(ticket));

            // A garbage collection while idle pulls the managed heap back into RAM (2026-11-21: 7.6 -> 22 MB about
            // 30 minutes after the release, then flat for hours). After one, page out again until the next use; not
            // while a ShareX window has the focus or the mouse highlighter is on (in use without a wake-up).
            int collections = GC.CollectionCount(0);
            while (WarmUpMemoryRelease.IsPagedOut)
            {
                await Task.Delay(RecheckMilliseconds);
                if (!CanRelease(ticket)) return;
                if (GC.CollectionCount(0) == collections || AnyWindowActive() ||
                    ShareX.Tools.MouseHighlighterManager.IsManuallyActive) continue;
                WarmUpMemoryRelease.PageOutAgain();
                collections = GC.CollectionCount(0);
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Idle memory release failed.");
        }
    }

    private static bool CanRelease(int ticket)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ticket != _ticket || RegionCaptureIntegration.IsCaptureOpen || TaskManager.IsBusy ||
            ScreenRecordManager.IsRecording) return false;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;

        foreach (Window window in desktop.Windows)
        {
            if (window.IsVisible && HoldsImage(window)) return false;
        }
        return true;
    }

    private static bool AnyWindowActive() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
        desktop.Windows.Any(window => window.IsActive);

    private static bool HoldsImage(Window window) => window.GetType().Name is
        "RegionCaptureWindow" or "ScrollingCaptureWindow" or "EditorWindow" or "PhotoEditorWindow" or
        "NotificationWindow" or "AfterCaptureWindow" or "BeforeUploadWindow" or "PinToScreenWindow";
}
