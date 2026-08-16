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
    // Launcher file names Heimdall has a documented grammar for. Anything else is refused rather
    // than driven on a guess: the two executables take different, non-interchangeable commands.
    private const string StoreBrowseExecutableName = "storebrowse.exe";
    private const string SelfServiceExecutableName = "SelfService.exe";

    // storebrowse commands. The documentation defines these as two DISTINCT commands, so exactly
    // one is ever emitted - passing both is not a richer invocation, it is a malformed one.
    // https://docs.citrix.com/en-us/citrix-workspace-app-for-windows/store-browse.html
    private const string StoreBrowseNonSsoCommand = "-L";
    private const string StoreBrowseSsoCommand = "-S";

    // SelfService.exe reaches the same store through its own storebrowse sub-command, which the
    // documentation describes for the single sign-on case only.
    // https://docs.citrix.com/en-us/citrix-workspace-app-for-windows/storebrowse-for-workspace.html
    private const string SelfServiceStoreBrowseCommand = "storebrowse";
    private const string SelfServiceSsoFlag = "-q";

    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<string, string?> _unprotectSecret;
    private readonly Func<string?> _resolveSelfServicePath;
    private readonly Func<string?> _resolveCitrixLauncher;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarning;

    /// <summary>
    /// Baseline of visible windows, taken before the launcher starts. Injectable so a test can
    /// prove the capture happens before the process start rather than after it.
    /// </summary>
    private readonly Func<IReadOnlySet<nint>> _capturePreLaunchWindows;

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
        Func<string?>? resolveCitrixLauncher = null,
        Func<IReadOnlySet<nint>>? capturePreLaunchWindows = null)
    {
        _capturePreLaunchWindows = capturePreLaunchWindows ?? VisibleWindowSnapshot.Capture;
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
            safeStoreFrontLogValue = CitrixStoreFrontUrlSanitizer.Sanitize(storeFrontUri);
        }

        _logInfo(
            $"ConnectCitrixAsync: {server.DisplayName} hasLaunchCmd={!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine)} storeFront={safeStoreFrontLogValue} icaFile={server.CitrixIcaFilePath ?? "none"}");

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingCitrix);

        Process? process = null;
        var mode = CitrixLaunchMode.Unknown;
        string? resultStoreFrontUrl = null;
        string? resultAppName = null;

        // Captured before any launch path runs, and carried to the view in the session result.
        // Taking it after the process has started races single sign-on and a warm cache, which can
        // surface the session window first: it would then be classified as pre-existing for the
        // whole detection window and the embedded capture would fail on an embeddable session.
        IReadOnlySet<nint> preLaunchWindows = _capturePreLaunchWindows();

        try
        {
            if (!string.IsNullOrWhiteSpace(server.CitrixLaunchCommandLine))
            {
                mode = CitrixLaunchMode.SelfServiceCache;
                if (settings.VaultEnabled &&
                    !VaultSecretBlob.IsSecretBlob(server.CitrixLaunchCommandLine))
                {
                    _logWarning(
                        "Citrix launch blocked: mode=SelfServiceCache unprotectedToken=true vaultEnabled=true");
                    string message = _localizer["CitrixLaunchFailed"];
                    _connectionSm.SetError(server.Id, message);
                    return Task.FromResult(new ConnectionResult(false, message, null));
                }

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
                 * SettingsViewModel protects new local-cache launch tokens at its persistence
                 * boundary when the vault is enabled. ImportedProfileSanitizer covers external
                 * profile imports only; it is not the gate for the local Citrix cache flow.
                 *
                 * Vault-disabled legacy configurations may contain plaintext tokens. When the
                 * vault is enabled, lifecycle reconciliation protects persisted tokens and the
                 * guard above rejects any unprotected value that still reaches this consumer.
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
                    string msg = _localizer["CitrixWorkspaceNotFound"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

                // Refused BEFORE the launch rather than driven on a guess: an executable with no
                // documented grammar, and SelfService.exe without single sign-on, have no
                // invocation Heimdall can form. Starting the process anyway would hand the wrong
                // command line to a real launcher.
                if (!TryCreateStoreFrontStartInfo(
                        launcher,
                        server.CitrixAppName,
                        validatedStoreFrontUrl,
                        server.CitrixUseSso,
                        out ProcessStartInfo? startInfo)
                    || startInfo is null)
                {
                    _logWarning(
                        $"Citrix launch blocked: mode=StoreFront launcher={Path.GetFileName(launcher)} sso={server.CitrixUseSso} unsupportedLauncherGrammar=true");
                    string msg = _localizer["CitrixLaunchFailed"];
                    _connectionSm.SetError(server.Id, msg);
                    return Task.FromResult(new ConnectionResult(false, msg, null));
                }

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
                    server.SessionLoggingOverride,
                    PreLaunchWindows: preLaunchWindows)));
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
        // storebrowse.exe is preferred for StoreFront launches (-L or -S, never both; see
        // TryCreateStoreFrontStartInfo, which drives each executable by its own grammar and
        // refuses the combinations that have none). On Citrix Workspace
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

    /// <summary>
    /// Builds the StoreFront invocation for the launcher that was actually resolved.
    /// </summary>
    /// <remarks>
    /// The grammar is chosen from the executable, never assumed: <c>storebrowse.exe</c> and
    /// <c>SelfService.exe</c> take different commands, and a resolver that falls back from one to
    /// the other would otherwise hand the wrong command line to a real launcher.
    /// <para>
    /// Returns false, rather than throwing or guessing, for the two combinations that have no
    /// documented invocation: <c>SelfService.exe</c> without single sign-on, and any executable
    /// this class has no grammar for. The caller refuses the launch on that answer.
    /// </para>
    /// </remarks>
    /// <param name="startInfo">The invocation to start, or null when this returns false.</param>
    /// <returns>True when a documented invocation exists for this launcher and SSO combination.</returns>
    internal static bool TryCreateStoreFrontStartInfo(
        string launcher,
        string appName,
        string storeFrontUrl,
        bool useSso,
        out ProcessStartInfo? startInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        startInfo = null;

        if (!TryValidateStoreFrontUrl(storeFrontUrl, out string validatedStoreFrontUrl))
        {
            throw new ArgumentException(
                "StoreFront URL must be an absolute HTTP or HTTPS URI.",
                nameof(storeFrontUrl));
        }

        CitrixLauncherGrammar grammar = ResolveLauncherGrammar(launcher);
        if (grammar == CitrixLauncherGrammar.Unsupported)
        {
            return false;
        }

        if (grammar == CitrixLauncherGrammar.SelfService && !useSso)
        {
            return false;
        }

        ProcessStartInfo created = new()
        {
            FileName = launcher,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (grammar == CitrixLauncherGrammar.StoreBrowse)
        {
            // Exactly one command, never both.
            created.ArgumentList.Add(useSso ? StoreBrowseSsoCommand : StoreBrowseNonSsoCommand);
        }
        else
        {
            created.ArgumentList.Add(SelfServiceStoreBrowseCommand);
            created.ArgumentList.Add(SelfServiceSsoFlag);
        }

        // Separate entries, so a space or a quote in either survives to the launcher intact.
        created.ArgumentList.Add(appName);
        created.ArgumentList.Add(validatedStoreFrontUrl);
        startInfo = created;
        return true;
    }

    /// <summary>
    /// Classifies the resolved launcher by file name, case-insensitively, so a full path and a
    /// bare executable name are recognized alike.
    /// </summary>
    private static CitrixLauncherGrammar ResolveLauncherGrammar(string launcher)
    {
        string fileName = Path.GetFileName(launcher);

        if (string.Equals(fileName, StoreBrowseExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return CitrixLauncherGrammar.StoreBrowse;
        }

        if (string.Equals(fileName, SelfServiceExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return CitrixLauncherGrammar.SelfService;
        }

        return CitrixLauncherGrammar.Unsupported;
    }

    /// <summary>Which documented command line the resolved executable expects.</summary>
    private enum CitrixLauncherGrammar
    {
        /// <summary>No documented grammar. The launch is refused rather than guessed.</summary>
        Unsupported = 0,

        /// <summary><c>storebrowse.exe</c>: one of two distinct commands, never both.</summary>
        StoreBrowse = 1,

        /// <summary><c>SelfService.exe</c>: its own storebrowse sub-command, documented SSO-only.</summary>
        SelfService = 2,
    }
}
