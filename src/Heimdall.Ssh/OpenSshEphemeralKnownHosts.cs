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
/// One host key line of the ephemeral <c>known_hosts</c> file: the key type written in the
/// second column and the base64 public key blob. Heimdall decided these keys; the file holds
/// nothing else.
/// </summary>
/// <param name="KeyType">Key type, one of the types in <see cref="OpenSshHostKeyTypes"/>.</param>
/// <param name="PublicKeyBase64">Base64 of the public key blob (never a certificate).</param>
public sealed record OpenSshKnownHostsKey(string KeyType, string PublicKeyBase64);

/// <summary>
/// Materializes a short-lived, UTF-8 without BOM, owner-restricted <c>known_hosts</c> file that
/// holds only the host keys Heimdall decided, under a fixed <c>HostKeyAlias</c>, so native
/// <c>ssh.exe</c> verifies the server against those keys and nothing else. It never copies the
/// host-key store and never touches the user's global OpenSSH files.
/// </summary>
public sealed class OpenSshEphemeralKnownHosts : IDisposable
{
    internal const string FilePrefix = "kh-";
    internal const string FileExtension = ".txt";

    /// <summary>
    /// Alias written in the first column of every line and passed to ssh as
    /// <c>HostKeyAlias</c>: the lookup no longer depends on the endpoint ssh dials (a loopback
    /// port opened for a gateway, or the host itself), nor on its port.
    /// </summary>
    public const string HostKeyAlias = "heimdall-pinned-host";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private int _disposed;

    public string FilePath { get; }

    private OpenSshEphemeralKnownHosts(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Builds the file content: exactly one <c>&lt;alias&gt; &lt;keytype&gt; &lt;base64&gt;</c>
    /// line per distinct key, in the order given.
    /// </summary>
    /// <exception cref="ArgumentException">No key, an unsupported key type, or a malformed blob.</exception>
    public static string BuildKnownHostsContent(IReadOnlyList<OpenSshKnownHostsKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one pinned host key is required.", nameof(keys));
        }

        var lines = new List<string>(keys.Count);
        foreach (OpenSshKnownHostsKey key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!OpenSshHostKeyTypes.IsSupported(key.KeyType))
            {
                throw new ArgumentException("Unsupported pinned host key type.", nameof(keys));
            }

            if (!IsBase64Blob(key.PublicKeyBase64))
            {
                throw new ArgumentException("Pinned host key blob is not valid base64.", nameof(keys));
            }

            string line = $"{HostKeyAlias} {key.KeyType} {key.PublicKeyBase64}";
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }
        }

        return string.Join('\n', lines) + '\n';
    }

    public static OpenSshEphemeralKnownHosts Create(
        IReadOnlyList<OpenSshKnownHostsKey> keys,
        string? baseDirectory = null)
    {
        string content = BuildKnownHostsContent(keys);

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

        File.WriteAllText(filePath, content, Utf8NoBom);
        if (OperatingSystem.IsWindows())
        {
            AclEnforcer.SetFileAcl(filePath);
        }

        return new OpenSshEphemeralKnownHosts(filePath);
    }

    public static string GetDefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Heimdall",
            "tmp",
            "openssh");

    private static bool IsBase64Blob(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (char c in value)
        {
            bool valid = (c is >= 'A' and <= 'Z')
                || (c is >= 'a' and <= 'z')
                || (c is >= '0' and <= '9')
                || c is '+' or '/' or '=';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

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
