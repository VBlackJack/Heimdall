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
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles Citrix session launch logic.
/// </summary>
internal sealed class CitrixHandler : IProtocolHandler
{
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;

    public CitrixHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer)
    {
        _connectionSm = connectionSm;
        _localizer = localizer;
    }

    public string Protocol => "CITRIX";

    /// <summary>
    /// Launches a Citrix session via storebrowse.exe, SelfService.exe, or a direct .ica file.
    /// </summary>
    public Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);

        Core.Logging.FileLogger.Info(
            $"ConnectCitrixAsync: {server.DisplayName} hasLaunchCmd={!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine)} storeFront={server.CitrixStoreFrontUrl ?? "none"} icaFile={server.CitrixIcaFilePath ?? "none"}");

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);
        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingCitrix);

        Process? process = null;
        var mode = CitrixLaunchMode.Unknown;
        string? resultStoreFrontUrl = null;
        string? resultAppName = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine))
            {
                if (server.CitrixLaunchCommandLine.AsSpan().IndexOfAny(
                    ['|', '&', ';', '`', '$', '\n', '\r']) >= 0)
                {
                    var msg = _localizer["CitrixLaunchCommandRejected"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                mode = CitrixLaunchMode.SelfServiceCache;
                var selfServicePath = ResolveSelfServicePath();
                if (selfServicePath is null)
                {
                    var msg = _localizer["CitrixWorkspaceNotFound"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                Core.Logging.FileLogger.Info(
                    $"Citrix launch (SelfService cache): {selfServicePath} {server.CitrixLaunchCommandLine}");

                /*
                 * CitrixLaunchCommandLine is treated as an opaque launch blob produced by
                 * CitrixCacheScanner. ImportedProfileSanitizer.Sanitize is the live gate
                 * that strips this field from externally imported profiles before they are
                 * persisted into configuration. Manual servers.json edits still bypass that
                 * gate; wiring SchemaValidator into the ConfigManager load path with an
                 * explicit failure policy is tracked as follow-up backlog work.
                 *
                 * Keep the launch path narrow here and avoid introducing a second ad-hoc
                 * string sanitizer with rules that could drift from the import boundary.
                 */
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = selfServicePath,
                    Arguments = server.CitrixLaunchCommandLine,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else if (!string.IsNullOrWhiteSpace(server.CitrixIcaFilePath) &&
                     File.Exists(server.CitrixIcaFilePath))
            {
                mode = CitrixLaunchMode.IcaFile;
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = server.CitrixIcaFilePath,
                    UseShellExecute = true
                });
            }
            else if (!string.IsNullOrWhiteSpace(server.CitrixStoreFrontUrl) &&
                     !string.IsNullOrWhiteSpace(server.CitrixAppName))
            {
                if (!TryValidateStoreFrontUrl(server.CitrixStoreFrontUrl, out var validatedStoreFrontUrl))
                {
                    var msg = _localizer["CitrixInvalidStoreFrontUrl"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                mode = CitrixLaunchMode.StoreFront;
                resultStoreFrontUrl = validatedStoreFrontUrl;
                resultAppName = server.CitrixAppName;
                var launcher = ResolveCitrixLauncher();
                if (launcher is null)
                {
                    var msg = _localizer["CitrixWorkspaceNotFound"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                var startInfo = CreateStoreFrontStartInfo(
                    launcher,
                    server.CitrixAppName,
                    validatedStoreFrontUrl,
                    server.CitrixUseSso);

                Core.Logging.FileLogger.Info(
                    $"Citrix launch (StoreFront): launcher={launcher} app={server.CitrixAppName} store={validatedStoreFrontUrl} sso={server.CitrixUseSso}");

                process = Process.Start(startInfo);
            }
            else
            {
                var msg = _localizer["CitrixNoConnectionConfigured"];
                _connectionSm.SetError(server.Id, msg);
                return Task.FromResult(new ConnectionResult(false, msg, null));
            }

            _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
            return Task.FromResult(new ConnectionResult(
                true,
                null,
                new CitrixSessionResult(
                    process,
                    resultStoreFrontUrl,
                    resultAppName,
                    mode,
                    server.SessionLoggingOverride)));
        }
        catch (Exception ex)
        {
            process?.Dispose();
            var userMsg = _localizer.Format("ErrorCitrixLaunchFailed", ex.Message);
            _connectionSm.SetError(server.Id, userMsg);
            return Task.FromResult(new ConnectionResult(false, userMsg, null));
        }
    }

    /// <summary>
    /// Resolves the Citrix Workspace launcher executable.
    /// </summary>
    private static string? ResolveCitrixLauncher()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        IReadOnlyList<string> paths = BuildCitrixLauncherCandidates(programFilesX86, programFiles);

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return ConnectionHelpers.FindInPath("storebrowse.exe") ??
               ConnectionHelpers.FindInPath("SelfService.exe");
    }

    /// <summary>
    /// Builds Citrix Workspace launcher candidate paths in probe order.
    /// </summary>
    internal static IReadOnlyList<string> BuildCitrixLauncherCandidates(
        string programFilesX86,
        string programFiles)
    {
        // storebrowse.exe is preferred for StoreFront launches (-L / -S). On Citrix Workspace
        // App 2507+ it ships under "ICA Client\AuthManager"; older layouts kept it directly in
        // "ICA Client". SelfService.exe lives under "ICA Client\SelfServicePlugin" on current
        // builds. Probe the modern subfolders first, then the legacy flat layout.
        return new[]
        {
            Path.Combine(programFilesX86, "Citrix", "ICA Client", "AuthManager", "storebrowse.exe"),
            Path.Combine(programFiles, "Citrix", "ICA Client", "AuthManager", "storebrowse.exe"),
            Path.Combine(programFilesX86, "Citrix", "ICA Client", "storebrowse.exe"),
            Path.Combine(programFiles, "Citrix", "ICA Client", "storebrowse.exe"),
            Path.Combine(programFilesX86, "Citrix", "ICA Client", "SelfServicePlugin", "SelfService.exe"),
            Path.Combine(programFiles, "Citrix", "ICA Client", "SelfServicePlugin", "SelfService.exe"),
            Path.Combine(programFilesX86, "Citrix", "ICA Client", "SelfService.exe"),
            Path.Combine(programFiles, "Citrix", "ICA Client", "SelfService.exe"),
        };
    }

    /// <summary>
    /// Resolves the SelfService.exe path specifically (used for cache-based launch).
    /// </summary>
    private static string? ResolveSelfServicePath()
    {
        var paths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Citrix", "ICA Client", "SelfServicePlugin", "SelfService.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Citrix", "ICA Client", "SelfService.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Citrix", "ICA Client", "SelfServicePlugin", "SelfService.exe"),
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return ConnectionHelpers.FindInPath("SelfService.exe");
    }

    internal static bool TryValidateStoreFrontUrl(string? rawUrl, out string validatedUrl)
    {
        validatedUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        validatedUrl = uri.AbsoluteUri;
        return true;
    }

    internal static ProcessStartInfo CreateStoreFrontStartInfo(
        string launcher,
        string appName,
        string storeFrontUrl,
        bool useSso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        if (!TryValidateStoreFrontUrl(storeFrontUrl, out var validatedStoreFrontUrl))
        {
            throw new ArgumentException(
                "StoreFront URL must be an absolute HTTP or HTTPS URI.",
                nameof(storeFrontUrl));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launcher,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-L");
        if (useSso)
        {
            startInfo.ArgumentList.Add("-S");
        }

        startInfo.ArgumentList.Add(appName);
        startInfo.ArgumentList.Add(validatedStoreFrontUrl);
        return startInfo;
    }
}
