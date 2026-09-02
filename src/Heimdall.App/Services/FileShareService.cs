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

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Wraps the lifetime of an <see cref="EphemeralFileServer"/> so views can
/// expose a "Quick File Server" toggle without owning the server, ports,
/// base URL or shutdown logic themselves. The service is intentionally
/// UI-agnostic: it does not resolve localization keys, show dialogs, or
/// touch the clipboard. Consumers wire <see cref="SharingStarted"/>,
/// <see cref="SharingStopped"/> and <see cref="FileServed"/> to drive the
/// view from the payloads provided. Enabling TFTP exposes the shared directory
/// unauthenticated on the LAN because RFC 1350 has no authentication mechanism.
/// </summary>
public sealed class FileShareService : IAsyncDisposable, INotifyPropertyChanged
{
    private EphemeralFileServer? _server;
    private readonly object _startGate = new();
    private bool _disposed;
    private bool _startInProgress;

    /// <summary>True while either the HTTP or TFTP listener is running.</summary>
    public bool IsSharing => _server is { IsHttpRunning: true } or { IsTftpRunning: true };

    /// <summary>Tokenized base URL of the running HTTP listener, or <c>null</c> when stopped.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>Filesystem directory currently being shared, or <c>null</c> when stopped.</summary>
    public string? CurrentDirectory { get; private set; }

    /// <summary>
    /// Raised once after <see cref="StartAsync"/> successfully brings up at
    /// least one of the HTTP or TFTP listeners. Carries the base URL,
    /// resolved local IP, ports, and pre-formatted helper command lines.
    /// </summary>
    public event EventHandler<FileShareStartedEventArgs>? SharingStarted;

    /// <summary>Raised once after <see cref="StopAsync"/> finishes tearing the server down.</summary>
    public event EventHandler? SharingStopped;

    /// <summary>
    /// Re-raised whenever the underlying <see cref="EphemeralFileServer"/>
    /// serves a file. The string is the same payload the inner server emits
    /// (e.g. <c>"HTTP: foo.txt"</c> or <c>"TFTP: bar.bin"</c>).
    /// </summary>
    public event EventHandler<string>? FileServed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Brings up the HTTP and TFTP listeners over <paramref name="directory"/>
    /// using ports resolved from <paramref name="settings"/> (or the framework
    /// defaults when <paramref name="settings"/> is <c>null</c>). Returns
    /// <c>true</c> if at least one listener came up; <c>false</c> when both
    /// failed (in which case no event is raised and the server is disposed).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a share is already active. Callers must <see cref="StopAsync"/>
    /// first before starting a new one.
    /// </exception>
    public async Task<bool> StartAsync(string directory, AppSettings? settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        lock (_startGate)
        {
            if (IsSharing || _startInProgress)
            {
                throw new InvalidOperationException(
                    "FileShareService is already sharing. Call StopAsync before starting a new share.");
            }

            _startInProgress = true;
        }

        try
        {
            EphemeralFileServer server = new();
            if (settings is not null)
            {
                server.ShutdownTimeoutMs = settings.ServerShutdownTimeoutMs;
            }

            // Ports stay fixed and predictable for firewall-pinned enterprise and SecNumCloud
            // networks; a collision fails the share cleanly instead of publishing an unreachable URL.
            int httpPort = settings?.EphemeralHttpPort ?? DefaultPorts.Http;
            int tftpPort = settings?.EphemeralTftpPort ?? DefaultPorts.Tftp;
            bool enableTftp = settings?.FileShareEnableTftp ?? false;

            try
            {
                await server.StartHttpServerAsync(directory, httpPort).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                FileLogger.Error($"Failed to start HTTP server: {ex.Message}");
            }

            if (enableTftp)
            {
                try
                {
                    await server.StartTftpServerAsync(directory, tftpPort).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"Failed to start TFTP server: {ex.Message}");
                }
            }

            if (!server.IsHttpRunning && !server.IsTftpRunning)
            {
                server.Dispose();
                return false;
            }

            string localIp = EphemeralFileServer.GetLocalIpAddress();
            string httpHost = server.IsHttpLocalOnly ? "localhost" : localIp;
            string publishedBaseUrl = $"http://{httpHost}:{httpPort}";
            string baseUrl = server.BuildUrl(publishedBaseUrl);
            string folderName = Path.GetFileName(directory) ?? directory;
            string wgetUrl = server.BuildUrl(publishedBaseUrl, "<filename>");
            string curlUrl = $"{publishedBaseUrl}/<filename>";

            server.FileServed += OnInnerFileServed;

            _server = server;
            CurrentDirectory = directory;
            BaseUrl = baseUrl;
            NotifyShareStateChanged();

            FileLogger.Info(
                $"[FileShareService] Sharing {directory} at {publishedBaseUrl}"
                + (server.IsHttpLocalOnly ? " (localhost-only fallback)" : ""));

            SharingStarted?.Invoke(this, new FileShareStartedEventArgs
            {
                BaseUrl = baseUrl,
                Directory = directory,
                FolderName = folderName,
                LocalIpAddress = localIp,
                HttpPort = httpPort,
                TftpPort = tftpPort,
                WgetCommand = $"wget \"{wgetUrl}\"",
                CurlCommand = $"curl -H \"Authorization: Bearer {server.AccessToken}\" -O \"{curlUrl}\"",
                TftpCommand = server.IsTftpRunning ? $"tftp {localIp} -c get <filename>" : null,
                IsTftpEnabled = server.IsTftpRunning
            });

            return true;
        }
        finally
        {
            lock (_startGate)
            {
                _startInProgress = false;
            }
        }
    }

    /// <summary>
    /// Tears the running server down, releasing ports and clearing all state.
    /// Idempotent - calling it while already stopped is a no-op (no event is raised).
    /// </summary>
    public async Task StopAsync()
    {
        if (_server is null)
        {
            return;
        }

        var server = _server;
        _server = null;
        BaseUrl = null;
        CurrentDirectory = null;
        NotifyShareStateChanged();

        server.FileServed -= OnInnerFileServed;
        await server.DisposeAsync().ConfigureAwait(true);

        FileLogger.Info("[FileShareService] Sharing stopped");
        SharingStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void OnInnerFileServed(string payload)
    {
        FileServed?.Invoke(this, payload);
    }

    private void NotifyShareStateChanged()
    {
        OnPropertyChanged(nameof(IsSharing));
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(CurrentDirectory));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Payload of <see cref="FileShareService.SharingStarted"/>. Provides the
/// view with everything it needs to render the helper dialog and status bar
/// without the service knowing about WPF or localization.
/// </summary>
public sealed class FileShareStartedEventArgs : EventArgs
{
    /// <summary>Tokenized base URL of the HTTP listener, e.g. <c>http://192.168.1.10:8080/?token=...</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Absolute filesystem path that is being shared.</summary>
    public required string Directory { get; init; }

    /// <summary>Display-friendly folder name (last path segment of <see cref="Directory"/>).</summary>
    public required string FolderName { get; init; }

    /// <summary>Resolved local IPv4 address used to build <see cref="BaseUrl"/>.</summary>
    public required string LocalIpAddress { get; init; }

    /// <summary>HTTP listener port.</summary>
    public required int HttpPort { get; init; }

    /// <summary>TFTP listener port.</summary>
    public required int TftpPort { get; init; }

    /// <summary>Pre-formatted <c>wget</c> command template with a <c>&lt;filename&gt;</c> placeholder.</summary>
    public required string WgetCommand { get; init; }

    /// <summary>Pre-formatted <c>curl</c> command template with a <c>&lt;filename&gt;</c> placeholder.</summary>
    public required string CurlCommand { get; init; }

    /// <summary>Pre-formatted <c>tftp</c> command template when the unauthenticated listener is enabled.</summary>
    public string? TftpCommand { get; init; }

    /// <summary>Whether the TFTP listener was explicitly enabled and started for this share.</summary>
    public required bool IsTftpEnabled { get; init; }
}
