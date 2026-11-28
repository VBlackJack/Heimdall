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
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Materializes a short-lived, UTF-8 without BOM, owner-restricted <c>known_hosts</c> file
/// populated from Heimdall's <see cref="HostKeyStore"/> so native <c>ssh.exe</c> trusts pinned
/// keys without touching the user's global OpenSSH files.
/// </summary>
public sealed class OpenSshEphemeralKnownHosts : IDisposable
{
    internal const string FilePrefix = "kh-";
    internal const string FileExtension = ".txt";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private int _disposed;

    public string FilePath { get; }

    public bool TargetHostPinned { get; }

    private OpenSshEphemeralKnownHosts(string filePath, bool targetHostPinned)
    {
        FilePath = filePath;
        TargetHostPinned = targetHostPinned;
    }

    public static string BuildKnownHostsContent(
        HostKeyStore store,
        string transportHost,
        int transportPort,
        string? logicalHost = null,
        int? logicalPort = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportHost);

        var lines = new List<string>();
        var entries = store.GetAllEntries();

        string effectiveHost = string.IsNullOrWhiteSpace(logicalHost) ? transportHost : logicalHost;
        int effectivePort = logicalPort ?? transportPort;

        if (entries.TryGetValue(HostKeyFormats.MakeKey(effectiveHost, effectivePort), out HostKeyEntry? targetEntry)
            && !string.IsNullOrWhiteSpace(targetEntry.PublicKeyBase64)
            && !store.IsRevoked(effectiveHost, effectivePort, targetEntry.Fingerprint))
        {
            string transportToken = FormatHostToken(transportHost, transportPort);
            lines.Add($"{transportToken} {targetEntry.Algorithm} {targetEntry.PublicKeyBase64}");
        }

        foreach (var (hostPort, entry) in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.PublicKeyBase64)
                || !HostKeyFormats.TryParseKey(hostPort, out string host, out int port)
                || host.StartsWith("|1|", StringComparison.Ordinal)
                || store.IsRevoked(host, port, entry.Fingerprint))
            {
                continue;
            }

            string token = FormatHostToken(host, port);
            string line = $"{token} {entry.Algorithm} {entry.PublicKeyBase64}";
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines) + '\n';
    }

    public static OpenSshEphemeralKnownHosts Create(
        HostKeyStore store,
        string transportHost,
        int transportPort,
        string? logicalHost = null,
        int? logicalPort = null,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        string effectiveHost = string.IsNullOrWhiteSpace(logicalHost) ? transportHost : logicalHost;
        int effectivePort = logicalPort ?? transportPort;
        HostKeyEntry? pinned = store.GetEntry(effectiveHost, effectivePort);
        bool targetPinned = pinned is not null
            && !string.IsNullOrWhiteSpace(pinned.PublicKeyBase64)
            && !store.IsRevoked(effectiveHost, effectivePort, pinned.Fingerprint);

        string dir = !string.IsNullOrWhiteSpace(baseDirectory)
            ? baseDirectory
            : GetDefaultDirectory();

        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            AclEnforcer.SetDirectoryAcl(dir);
        }

        int pid = Environment.ProcessId;
        string fileName = $"{FilePrefix}{pid.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}{FileExtension}";
        string filePath = Path.Combine(dir, fileName);

        string content = BuildKnownHostsContent(store, transportHost, transportPort, logicalHost, logicalPort);
        File.WriteAllText(filePath, content, Utf8NoBom);
        if (OperatingSystem.IsWindows())
        {
            AclEnforcer.SetFileAcl(filePath);
        }

        return new OpenSshEphemeralKnownHosts(filePath, targetPinned);
    }

    public static string GetDefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Heimdall",
            "tmp",
            "openssh");

    private static string FormatHostToken(string host, int port)
        => port == 22 ? host : $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"[OpenSshEphemeralKnownHosts] Cleanup failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Sweeps leftover ephemeral OpenSSH <c>known_hosts</c> files from terminated Heimdall processes.
/// Verifies current-user ownership before deleting any file.
/// </summary>
public sealed class OpenSshKnownHostsJanitor
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(12);

    private readonly string _directory;
    private readonly TimeSpan _maxAge;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<string, bool> _isOwnedByCurrentUser;
    private readonly Func<DateTime> _utcNow;

    public OpenSshKnownHostsJanitor(
        string? directory = null,
        TimeSpan? maxAge = null,
        Func<int, bool>? isProcessAlive = null,
        Func<string, bool>? isOwnedByCurrentUser = null,
        Func<DateTime>? utcNow = null)
    {
        _directory = directory ?? OpenSshEphemeralKnownHosts.GetDefaultDirectory();
        _maxAge = maxAge ?? DefaultMaxAge;
        _isProcessAlive = isProcessAlive ?? DefaultIsProcessAlive;
        _isOwnedByCurrentUser = isOwnedByCurrentUser ?? DefaultIsOwnedByCurrentUser;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int SweepStaleFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        int deleted = 0;
        DateTime threshold = _utcNow() - _maxAge;

        try
        {
            foreach (string path in Directory.EnumerateFiles(
                _directory,
                OpenSshEphemeralKnownHosts.FilePrefix + "*" + OpenSshEphemeralKnownHosts.FileExtension))
            {
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    bool staleAge = info.LastWriteTimeUtc < threshold;
                    bool deadOwnerPid = TryExtractPid(Path.GetFileName(path), out int pid) && !_isProcessAlive(pid);
                    if (!staleAge && !deadOwnerPid)
                    {
                        continue;
                    }

                    if (!_isOwnedByCurrentUser(path))
                    {
                        continue;
                    }

                    info.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLogger.Warn($"[OpenSshKnownHostsJanitor] Delete skipped: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"[OpenSshKnownHostsJanitor] Enumerate failed: {ex.Message}");
        }

        return deleted;
    }

    internal static bool TryExtractPid(string fileName, out int pid)
    {
        pid = 0;
        if (!fileName.StartsWith(OpenSshEphemeralKnownHosts.FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int start = OpenSshEphemeralKnownHosts.FilePrefix.Length;
        int dash = fileName.IndexOf('-', start);
        if (dash <= start)
        {
            return false;
        }

        return int.TryParse(fileName.AsSpan(start, dash - start), NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && pid > 0;
    }

    private static bool DefaultIsProcessAlive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using Process proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool DefaultIsOwnedByCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return false;
            }

            FileSecurity security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
            return owner is SecurityIdentifier ownerSid && ownerSid == currentUser;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
