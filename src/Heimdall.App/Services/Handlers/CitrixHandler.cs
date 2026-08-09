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
using Heimdall.Core.Security.Vault;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles Citrix session launch logic.
/// </summary>
internal sealed class CitrixHandler : IProtocolHandler
{
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<string, string?> _unprotectSecret;
    private readonly Func<string?> _resolveSelfServicePath;
    private readonly Func<string?> _resolveCitrixLauncher;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarning;

    public CitrixHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer)
        : this(
            connectionSm,
            localizer,
            static startInfo => Process.Start(startInfo),
            CredentialProtector.Unprotect,
            ResolveSelfServicePath,
            Core.Logging.FileLogger.Info,
            Core.Logging.FileLogger.Warn)
    {
    }

    internal CitrixHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        Func<ProcessStartInfo, Process?> startProcess,
        Func<string, string?> unprotectSecret,
        Func<string?> resolveSelfServicePath,
        Action<string> logInfo,
        Action<string> logWarning,
        Func<string?>? resolveCitrixLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(connectionSm);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(startProcess);
        ArgumentNullException.ThrowIfNull(unprotectSecret);
        ArgumentNullException.ThrowIfNull(resolveSelfServicePath);
        ArgumentNullException.ThrowIfNull(logInfo);
        ArgumentNullException.ThrowIfNull(logWarning);

        _connectionSm = connectionSm;
        _localizer = localizer;
        _startProcess = startProcess;
        _unprotectSecret = unprotectSecret;
        _resolveSelfServicePath = resolveSelfServicePath;
        _resolveCitrixLauncher = resolveCitrixLauncher ?? ResolveCitrixLauncher;
        _logInfo = logInfo;
        _logWarning = logWarning;
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

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        string? validatedStoreFrontUrl = null;
        string safeStoreFrontLogValue = "none";
        if (!string.IsNullOrWhiteSpace(server.CitrixStoreFrontUrl))
        {
            Uri? storeFrontUri;
            bool containsCredentials;
            if (!TryParseStoreFrontUrl(
                    server.CitrixStoreFrontUrl,
                    out storeFrontUri,
                    out containsCredentials) ||
                storeFrontUri is null)
            {
                string errorKey = containsCredentials
                    ? "CitrixStoreFrontCredentialsNotAllowed"
                    : "CitrixInvalidStoreFrontUrl";
                string message = _localizer[errorKey];
                _connectionSm.SetError(server.Id, message);
                return Task.FromResult(new ConnectionResult(false, message, null));
            }

            validatedStoreFrontUrl = storeFrontUri.AbsoluteUri;
            safeStoreFrontLogValue = BuildSafeStoreFrontLogValue(storeFrontUri);
        }

        _logInfo(
            $"ConnectCitrixAsync: {server.DisplayName} hasLaunchCmd={!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine)} storeFront={safeStoreFrontLogValue} icaFile={server.CitrixIcaFilePath ?? "none"}");

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingCitrix);

        Process? process = null;
        var mode = CitrixLaunchMode.Unknown;
        string? resultStoreFrontUrl = null;
        string? resultAppName = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine))
            {
                mode = CitrixLaunchMode.SelfServiceCache;
                string launchArguments = ResolveLaunchArguments(server.CitrixLaunchCommandLine);
                if (string.IsNullOrWhiteSpace(launchArguments))
                {
                    _logWarning(
                        "Citrix launch blocked: mode=SelfServiceCache launchTokenUnavailable=true");
                    string msg = _localizer["CitrixLaunchVaultLocked"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                if (launchArguments.AsSpan().IndexOfAny(
                    ['|', '&', ';', '`', '$', '\n', '\r']) >= 0)
                {
                    var msg = _localizer["CitrixLaunchCommandRejected"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                var selfServicePath = _resolveSelfServicePath();
                if (selfServicePath is null)
                {
                    var msg = _localizer["CitrixWorkspaceNotFound"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                _logInfo(
                    $"Citrix launch: mode=SelfServiceCache launcher={selfServicePath} hasLaunchCmd=true");

                /*
                 * CitrixLaunchCommandLine is stored as an encrypted opaque blob produced
                 * from CitrixCacheScanner data and is decrypted only in this launch branch.
                 * ImportedProfileSanitizer.Sanitize is the live gate that strips this field
                 * from externally imported profiles before they are persisted into
                 * configuration. Manual servers.json edits still bypass that gate; wiring
                 * SchemaValidator into the ConfigManager load path with an explicit failure
                 * policy is tracked as follow-up backlog work.
                 *
                 * Keep the launch path narrow here and avoid introducing a second ad-hoc
                 * string sanitizer with rules that could drift from the import boundary.
                 */
                var startInfo = new ProcessStartInfo
                {
                    FileName = selfServicePath,
                    Arguments = launchArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                try
                {
                    process = _startProcess(startInfo);
                }
                finally
                {
                    startInfo.Arguments = string.Empty;
                    launchArguments = string.Empty;
                }
            }
            else if (!string.IsNullOrWhiteSpace(server.CitrixIcaFilePath) &&
                     File.Exists(server.CitrixIcaFilePath))
            {
                mode = CitrixLaunchMode.IcaFile;
                process = _startProcess(new ProcessStartInfo
                {
                    FileName = server.CitrixIcaFilePath,
                    UseShellExecute = true
                });
            }
            else if (validatedStoreFrontUrl is not null &&
                     !string.IsNullOrWhiteSpace(server.CitrixAppName))
            {
                mode = CitrixLaunchMode.StoreFront;
                resultStoreFrontUrl = validatedStoreFrontUrl;
                resultAppName = server.CitrixAppName;
                string? launcher = _resolveCitrixLauncher();
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

                _logInfo(
                    $"Citrix launch (StoreFront): launcher={launcher} app={server.CitrixAppName} store={safeStoreFrontLogValue} sso={server.CitrixUseSso}");

                process = _startProcess(startInfo);
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
        catch (VaultLockedException)
        {
            process?.Dispose();
            _logWarning("Citrix launch blocked: mode=SelfServiceCache vaultLocked=true");
            string userMsg = _localizer["CitrixLaunchVaultLocked"];
            _connectionSm.SetError(server.Id, userMsg);
            return Task.FromResult(new ConnectionResult(false, userMsg, null));
        }
        catch (Exception ex)
        {
            process?.Dispose();
            _logWarning(
                $"Citrix launch failed: mode={mode} error={ex.GetType().Name}");
            var userMsg = _localizer["CitrixLaunchFailed"];
            _connectionSm.SetError(server.Id, userMsg);
            return Task.FromResult(new ConnectionResult(false, userMsg, null));
        }
    }

    private string ResolveLaunchArguments(string storedLaunchCommandLine)
    {
        if (!VaultSecretBlob.IsSecretBlob(storedLaunchCommandLine))
        {
            return storedLaunchCommandLine;
        }

        return _unprotectSecret(storedLaunchCommandLine) ?? string.Empty;
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

        Uri? uri;
        bool containsCredentials;
        if (!TryParseStoreFrontUrl(rawUrl, out uri, out containsCredentials) || uri is null)
        {
            return false;
        }

        validatedUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool TryParseStoreFrontUrl(
        string? rawUrl,
        out Uri? validatedUri,
        out bool containsCredentials)
    {
        validatedUri = null;
        containsCredentials = false;

        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            containsCredentials = true;
            return false;
        }

        validatedUri = uri;
        return true;
    }

    private static string BuildSafeStoreFrontLogValue(Uri storeFrontUri)
    {
        int port = storeFrontUri.IsDefaultPort ? -1 : storeFrontUri.Port;
        UriBuilder safeUri = new(
            storeFrontUri.Scheme,
            storeFrontUri.Host,
            port,
            storeFrontUri.AbsolutePath);
        return safeUri.Uri.AbsoluteUri;
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
