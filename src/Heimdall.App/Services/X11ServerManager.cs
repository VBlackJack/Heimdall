/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Diagnostics;
using System.IO;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Manages an X11 display server process for X11 forwarding over SSH.
/// Detects running X servers (VcXsrv, Xming, X410, XWin), starts a
/// configured server if none is found, and sets the DISPLAY variable.
/// </summary>
public sealed class X11ServerManager : IDisposable
{
    private static readonly string DefaultDisplay = "localhost:0.0";

    /// <summary>
    /// Known X server process names to detect.
    /// </summary>
    private static readonly string[] KnownProcessNames =
        ["vcxsrv", "xming", "x410", "xwin"];

    /// <summary>
    /// Common installation paths to search when no X server process is running.
    /// </summary>
    private static readonly string[] KnownInstallPaths =
    [
        @"C:\Program Files\VcXsrv\vcxsrv.exe",
        @"C:\Program Files (x86)\Xming\Xming.exe",
        @"C:\Program Files\Xming\Xming.exe",
        @"C:\cygwin64\bin\XWin.exe",
        @"C:\cygwin\bin\XWin.exe"
    ];

    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;

    private Process? _managedProcess;
    private readonly object _lock = new();

    public X11ServerManager(IConfigManager configManager, LocalizationManager localizer)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>
    /// Whether an X11 server (external or managed) is currently running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                if (_managedProcess is not null && !_managedProcess.HasExited)
                    return true;
            }

            return DetectRunningServer() is not null;
        }
    }

    /// <summary>
    /// Ensures an X11 server is available. Checks for running servers first,
    /// then attempts to start one from the configured or auto-detected path.
    /// Returns true if an X server is available, false otherwise.
    /// </summary>
    public async Task<bool> EnsureRunningAsync()
    {
        // Check if an X server is already running
        var runningName = DetectRunningServer();
        if (runningName is not null)
        {
            FileLogger.Info(_localizer.Format("X11ServerDetected", runningName));
            SetDisplay();
            return true;
        }

        // Load settings to check auto-start preference and custom path
        var settings = await _configManager.LoadSettingsAsync();
        if (!settings.X11AutoStart)
        {
            FileLogger.Info("X11 auto-start disabled in settings");
            return false;
        }

        // Try to find and start an X server
        var serverPath = ResolveServerPath(settings.X11ServerPath);
        if (serverPath is null)
        {
            FileLogger.Warn(_localizer["X11ServerNotFound"]);
            return false;
        }

        return StartServer(serverPath);
    }

    /// <summary>
    /// Stops the managed X server process, if one was started by this manager.
    /// Does not kill externally started X servers.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_managedProcess is null)
                return;

            try
            {
                if (!_managedProcess.HasExited)
                {
                    _managedProcess.Kill(entireProcessTree: true);
                    FileLogger.Info("Managed X11 server process stopped");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"Failed to stop X11 server: {ex.Message}");
            }
            finally
            {
                _managedProcess.Dispose();
                _managedProcess = null;
            }
        }
    }

    /// <summary>
    /// Sets the DISPLAY environment variable for X11 forwarding.
    /// </summary>
    public static void SetDisplay()
    {
        Environment.SetEnvironmentVariable("DISPLAY", DefaultDisplay);
        FileLogger.Info($"DISPLAY set to {DefaultDisplay}");
    }

    /// <summary>
    /// Checks running processes for a known X11 server.
    /// </summary>
    /// <returns>The detected process name, or null if none found.</returns>
    private static string? DetectRunningServer()
    {
        foreach (var name in KnownProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    foreach (var p in processes)
                        p.Dispose();
                    return name;
                }
            }
            catch
            {
                // Process enumeration can fail for access reasons; continue
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the X server executable path from user settings, known
    /// install locations, or PATH.
    /// </summary>
    private static string? ResolveServerPath(string? configuredPath)
    {
        // User-configured path takes priority
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // Search known installation directories
        foreach (var path in KnownInstallPaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Search PATH for known executables
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var name in KnownProcessNames)
            {
                var candidate = Path.Combine(dir, name + ".exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Command line handed to VcXsrv: display :0, one native window per X client,
    /// clipboard integration without primary-selection sync. Host access control is
    /// deliberately left on: the only client Heimdall points at this display is the
    /// local one (<c>localhost:0.0</c>), and <c>-ac</c> would let any host that can
    /// reach TCP 6000 read the forwarded windows and the clipboard.
    /// </summary>
    internal const string VcXsrvArguments = ":0 -multiwindow -clipboard -noprimary";

    /// <summary>
    /// Starts the X server process at the given path.
    /// VcXsrv is launched with <see cref="VcXsrvArguments"/>.
    /// </summary>
    private bool StartServer(string serverPath)
    {
        lock (_lock)
        {
            if (_managedProcess is not null && !_managedProcess.HasExited)
                return true;

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(serverPath).ToLowerInvariant();

                var psi = new ProcessStartInfo
                {
                    FileName = serverPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (fileName == "vcxsrv")
                {
                    psi.Arguments = VcXsrvArguments;
                }

                _managedProcess = Process.Start(psi);

                if (_managedProcess is not null)
                {
                    FileLogger.Info(_localizer.Format("X11ServerStarted", serverPath));
                    SetDisplay();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error($"Failed to start X11 server at {serverPath}", ex);
            }

            return false;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
