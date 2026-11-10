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

using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using ShareX.ImageEditor.Integration.Diagnostics;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

#if MicrosoftStore
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#endif

namespace ShareX;

internal static class Program
{
    private static Stopwatch _startTimer = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationDiagnostics.Initialize();
        _startTimer = Stopwatch.StartNew();
        ApplicationCommandLine.Initialize(args);

#if STEAM
        if (ApplicationCommandLine.TryHandleUninstall())
        {
            return;
        }
#endif

        SystemOptions.UpdateSystemOptions();
        PersonalPathManager.Initialize();
        DebugHelper.Init(AppPaths.LogsFilePath);

        StartupOptions.IsAdmin = Helpers.IsAdministrator();
        StartupOptions.MultiInstance = ApplicationCommandLine.IsCommandPresent("multi", "m");

        using (SingleInstanceManager singleInstanceManager = new(
            ApplicationInfo.MutexName, ApplicationInfo.PipeName, !StartupOptions.MultiInstance, args))
        {
            if (!singleInstanceManager.IsSingleInstance || singleInstanceManager.IsFirstInstance)
            {
                singleInstanceManager.ArgumentsReceived += SingleInstanceCommandRouter.ArgumentsReceived;
                Run(args);
            }
        }

        ApplicationLifecycle.RestartIfRequested();
        DebugHelper.Flush();
    }

    private static void Run(string[] args)
    {
        DebugHelper.WriteLine("ShareX starting.");
        DebugHelper.WriteLine("Version: " + ApplicationInfo.VersionText);
        DebugHelper.WriteLine("Build: " + ApplicationInfo.Build);
        DebugHelper.WriteLine("Command line: " + Environment.CommandLine);
        DebugHelper.WriteLine("Personal path: " + AppPaths.PersonalFolder);
        if (!string.IsNullOrEmpty(PersonalPathManager.DetectionMethod))
        {
            DebugHelper.WriteLine("Personal path detection method: " + PersonalPathManager.DetectionMethod);
        }
        DebugHelper.WriteLine("Operating system: " + Helpers.GetOperatingSystemProductName(true));
        DebugHelper.WriteLine(".NET version: " + Environment.Version);
        DebugHelper.WriteLine("Running as elevated process: " + StartupOptions.IsAdmin);

        StartupOptions.SilentRun = ApplicationCommandLine.IsCommandPresent("silent", "s");
#if MicrosoftStore
        StartupOptions.SilentRun = StartupOptions.SilentRun || AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
#endif
        StartupOptions.IgnoreHotkeyWarning = ApplicationCommandLine.IsCommandPresent("NoHotkeys");

        PersonalPathManager.CreateApplicationFolders();
        FileTypeRegistration.RegisterMissingExtensions();
        ApplicationDiagnostics.WriteStartupFlags();

        DebugHelper.WriteLine("Avalonia application initializing.");
        AvaloniaBootstrapper.Initialize(args, StartApplicationAsync, ApplicationLifecycle.OnAvaloniaStopped);
        ApplicationDiagnostics.InitializeAvalonia();

        SettingManager.LoadInitialSettings();
        ApplicationState.UpdateManager = new ShareXUpdateManager();
        LanguageHelper.ChangeLanguage(ApplicationState.Settings.Language);
        CleanupManager.CleanupAsync();

        DebugHelper.WriteLine("Avalonia application starting.");
        AvaloniaBootstrapper.Run();

        ApplicationLifecycle.CloseSequence();
    }

    private static readonly object _editorLogLock = new();
    private static string? _lastEditorKey;
    private static string? _lastEditorMessage;
    private static long _lastEditorMessageTime;
    private static int _editorMessageRepeats;

    // The image editor reports its own warnings, errors and clipboard steps here. Nothing listened before,
    // so they never reached the log. Debug messages (effect preview tracing) stay out of it.
    private static void ConnectImageEditorLog()
    {
        EditorServices.Diagnostics ??= new DelegateEditorDiagnosticsSink(diagnostic =>
        {
            if (diagnostic.Level == EditorDiagnosticLevel.Debug)
            {
                Debug.WriteLine($"[ImageEditor:Debug:{diagnostic.Source}] {diagnostic.Message}"); // As before the sink was set.
                return;
            }

            string message = $"Image editor {diagnostic.Level.ToString().ToLowerInvariant()} ({diagnostic.Source}): {diagnostic.Message}";
            // The exception's type and stack trace are part of the key, so two different failures with the same text
            // both get logged. Its message is left out: one holding a changing value would make every repeat unique.
            string key = diagnostic.ExceptionText == null ? message : message + "\n" + GetExceptionShape(diagnostic.ExceptionText);

            // Some editor warnings can fire on every frame while something keeps failing: log the same one at most every 5 s.
            lock (_editorLogLock)
            {
                long now = Environment.TickCount64;
                if (key == _lastEditorKey && now - _lastEditorMessageTime < 5000)
                {
                    _editorMessageRepeats++;
                    return;
                }

                WriteEditorLogRepeats();
                _lastEditorKey = key;
                _lastEditorMessage = message;
                _lastEditorMessageTime = now;

                if (diagnostic.ExceptionText != null)
                {
                    DebugHelper.WriteException(diagnostic.ExceptionText, message);
                }
                else
                {
                    DebugHelper.WriteLine(message);
                }
            }
        });
    }

    // Writes how often the last editor message was held back. Call with _editorLogLock held.
    private static void WriteEditorLogRepeats()
    {
        if (_editorMessageRepeats == 0) return;
        DebugHelper.WriteLine($"{_lastEditorMessage} (repeated {_editorMessageRepeats} more {(_editorMessageRepeats == 1 ? "time" : "times")})");
        _editorMessageRepeats = 0;
    }

    // So a count still held back when ShareX closes reaches the log. Called from ApplicationLifecycle.CloseSequence,
    // which every exit runs, also a Windows logoff or shutdown.
    internal static void FlushEditorLogRepeats()
    {
        lock (_editorLogLock)
        {
            WriteEditorLogRepeats();
        }
    }

    // Exception.ToString() starts with "Type: message", and a message can run over several lines ("Actual value was 5.").
    // Keep only the type and the stack frame lines, which stay the same when the same failure repeats.
    private static string GetExceptionShape(string exceptionText)
    {
        string[] lines = exceptionText.Split('\n');
        int colon = lines[0].IndexOf(':');
        StringBuilder shape = new(colon < 0 ? lines[0].TrimEnd() : lines[0][..colon]);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("at ", StringComparison.Ordinal) || line.StartsWith("--- End", StringComparison.Ordinal))
            {
                shape.Append('\n').Append(line);
            }
        }

        return shape.ToString();
    }

    // The image editor's own Copy (in the region capture overlay, which sets no copy handler) used Avalonia's clipboard,
    // which is never flushed: every paste called back into ShareX, and the image was gone once ShareX quit.
    // It now goes through ShareX's clipboard writer like every other image copy: PNG + DIB at once, and one log line.
    private sealed class EditorClipboard : IClipboardService
    {
        // Runs off the UI thread while PNG and DIB are encoded (about 1 s for a 5120x2520 desktop). The clipboard is
        // emptied first, so a paste meanwhile can't give the previous item, and the image is written only if nothing
        // else copied since: a newer copy (an after-capture copy, another app) must not be overwritten by this one.
        public void SetImage(SkiaSharp.SKBitmap bitmap)
        {
            ClipboardHelpers.Clear();
            uint sequence = ClipboardHelpers.SequenceNumber;
            ClipboardHelpers.CopyImage(bitmap, stillWanted: () => ClipboardHelpers.SequenceNumber == sequence);
        }

        public SkiaSharp.SKBitmap? GetImage() => ClipboardHelpers.GetImage();
    }

    private static async Task StartApplicationAsync()
    {
        ConnectImageEditorLog();
        EditorServices.Clipboard ??= new EditorClipboard();
        ImageEditorIntegration.Initialize();

        if (ApplicationState.Settings.ShowStartScreen)
        {
            DebugHelper.WriteLine("Start screen opening.");
            StartScreenWindow startScreen = new();
            await startScreen.ShowAsync();
            DebugHelper.WriteLine("Start screen closed.");
        }

        DebugHelper.WriteLine("Hotkey host init started.");
        MainForm hotkeyHost = new();
        ApplicationLifecycle.AttachHost(hotkeyHost);
        hotkeyHost.Initialize();

        await ApplicationSettingsRuntime.InitializeAsync(hotkeyHost);

        bool showMainWindow = !(StartupOptions.SilentRun || ApplicationState.Settings.SilentRun) ||
            !ApplicationState.Settings.ShowTray;
        MainWindowIntegration.Initialize(hotkeyHost.TrayIconService, showMainWindow);
        SingleInstanceCommandRouter.MarkReady();

        ShareX.Tools.MouseHighlighterManager.ActivateOnStartup(
            ApplicationState.DefaultTaskSettings.ToolsSettings.MouseHighlighterOptions);

        if (showMainWindow)
        {
            MainWindowIntegration.Activate();
        }

        IconPackPromptWindow.ShowOnceWithMainWindow();

        DebugHelper.WriteLine("Startup time: {0} ms", _startTimer.ElapsedMilliseconds);
        await ApplicationCommandLine.ExecuteInitialAsync();

        if (ApplicationState.Settings.ActionsToolbarRunAtStartup)
        {
            TaskHelpers.OpenActionsToolbar();
        }

        DebugHelper.WriteLine("Hotkey host init finished.");

        if (ApplicationState.Settings.PrewarmRegionCapture)
        {
            _ = PrewarmRegionCaptureAsync();
        }
    }

    private static async Task PrewarmRegionCaptureAsync()
    {
        try
        {
            await Task.Delay(1000);

            // Two runs: after only one, the second real capture is still slower than the rest.
            for (int i = 0; i < 2; i++)
            {
                if (RegionCaptureIntegration.IsCaptureOpen) break;
                await new CaptureRegion().PrewarmAsync(TaskSettings.GetDefaultTaskSettings());
                await Task.Delay(200);
            }

            // Let the spare window (posted at Background priority) be built first.
            await Task.Delay(500);
            await WarmUpMemoryRelease.RunAsync();
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Region capture prewarm failed.");
        }
    }
}
