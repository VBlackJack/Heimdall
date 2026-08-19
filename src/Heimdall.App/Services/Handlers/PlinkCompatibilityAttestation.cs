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

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Answers one question about an SSH launcher executable, and binds the answer to the image that is
/// actually launched: has its <c>-pwfile</c> behaviour been measured, so that its first byte of
/// output proves the password file was already read.
/// </summary>
/// <remarks>
/// <para>The answer is keyed on the exact bytes of the executable, because that is the only thing
/// that identifies a build. A file name, a directory, a version resource and the string printed by
/// <c>-V</c> are all attacker-chosen or simply wrong for a rebuild, and none of them says anything
/// about when the file is read.</para>
/// <para>Hashing alone was not enough, twice over. First, between the hash and the launch the
/// handler can wait on an interactive password dialog, an unbounded delay during which a legitimate
/// update could replace the executable; so the file is opened once, the digest is computed from that
/// same handle, and the handle stays open - denying writes and deletion - until the launch has been
/// issued.</para>
/// <para>Second, and this is what an absolute path does <b>not</b> give you: the open handle pins
/// the file, not the directories named on the way to it. A junction anywhere in the path can be
/// deleted and recreated pointing elsewhere while the handle is held, and the same absolute string
/// then resolves to a different image. Measured, not theorised: with the handle held on an attested
/// plink under <c>...\current\plink.exe</c>, repointing the <c>current</c> junction and launching the
/// identical string ran a different executable. <see cref="Path.GetFullPath(string)"/> does not help,
/// because the string was already absolute and already normalised.</para>
/// <para>So an attested lease also carries <see cref="PlinkAttestationLease.LaunchPath"/>, obtained
/// from the handle itself through <c>GetFinalPathNameByHandle</c>, with every reparse point already
/// traversed. That is the path the caller must launch. Measured: the returned
/// <c>\\?\</c>-prefixed form starts the image, and starts the attested one even after the junction
/// has been repointed.</para>
/// <para>This is <b>not</b> a defence against a hostile binary. Heimdall hands the password to
/// whatever executable it was pointed at, so an executable chosen to steal it has already won. What
/// this establishes is narrower: that the timing conclusion drawn from one measured build belongs to
/// the image that runs.</para>
/// <para>Failure is not an error. If the file cannot be opened, the digest does not match, or the
/// final path cannot be resolved, the connection proceeds on the configured path; only the early
/// deletion is withheld, and the password file is released at process exit as it always was.</para>
/// </remarks>
internal static class PlinkCompatibilityAttestation
{
    /// <summary>
    /// SHA-256 of the plink build whose <c>-pwfile</c> behaviour was measured: the copy shipped at
    /// <c>Assets/Tools/plink.exe</c>, PuTTY 0.83, 1037936 bytes.
    /// </summary>
    /// <remarks>
    /// In that build, <c>-pwfile</c> is handled inside <c>cmdline_process_param</c> while the
    /// command line is parsed: one line is read and the handle is closed at once, before any network
    /// activity. Measured on the binary itself - an unreadable <c>-pwfile</c> against an unreachable
    /// host reports the file error immediately, where a readable one against the same host instead
    /// spends the full network timeout. Any output therefore comes after the read.
    /// </remarks>
    private const string MeasuredPuttyRelease083Sha256 =
        "460E62E304361FB6F73EF215530B93D6F97C263B1442EE48134CDCDF94D3F1DE";

    /// <summary>
    /// <c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: the drive-letter form, fully normalised, with
    /// reparse points resolved. Returned with a <c>\\?\</c> prefix, which is deliberately kept - it
    /// is what makes the path immune to a second round of resolution, and it was measured to launch.
    /// </summary>
    private const int FinalPathNormalizedDos = 0x0;

    private static readonly HashSet<string> AttestedIdentities =
        new(StringComparer.OrdinalIgnoreCase) { MeasuredPuttyRelease083Sha256 };

    /// <summary>
    /// Identifies <paramref name="executablePath"/> and, when it is a measured build, pins that
    /// exact image and reports the path that reaches it.
    /// </summary>
    /// <param name="executablePath">The launcher about to be started.</param>
    /// <returns>
    /// A lease that grants the early deletion, and carries a
    /// <see cref="PlinkAttestationLease.LaunchPath"/>, only for an executable whose bytes match a
    /// measured build, whose image could be pinned, and whose final path could be resolved from that
    /// pin. Anything else yields a lease that grants nothing, which keeps the deletion on the
    /// process-exit path and leaves the caller launching the path it already had. Never null.
    /// </returns>
    internal static PlinkAttestationLease Acquire(string? executablePath)
    {
        return Acquire(executablePath, ResolveFinalPath);
    }

    /// <summary>
    /// Same contract, with the final-path resolution supplied. Exists so a test can exercise a
    /// resolution that fails: <c>GetFinalPathNameByHandle</c> does not fail on a handle that was
    /// just opened successfully, so that branch is otherwise unreachable from outside.
    /// </summary>
    internal static PlinkAttestationLease Acquire(
        string? executablePath,
        Func<SafeFileHandle, string?> resolveFinalPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return PlinkAttestationLease.NotAttested;
        }

        FileStream? pin = null;
        try
        {
            // FileShare.Read and nothing else: the image can still be launched while this is held -
            // measured, not assumed - but the file can no longer be replaced or written to. Both
            // are what an update would need to do, and either would invalidate the digest below.
            pin = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            // From this same handle, so the bytes hashed are the bytes now pinned. Re-opening the
            // path to hash it would measure a file that is only assumed to be the pinned one.
            byte[] digest = SHA256.HashData(pin);
            if (!AttestedIdentities.Contains(Convert.ToHexString(digest)))
            {
                pin.Dispose();
                return PlinkAttestationLease.NotAttested;
            }

            // From the handle as well, for the same reason: this is the path that reaches the file
            // just hashed, with the junctions and symbolic links already followed. Asking the file
            // system about the string again would only re-resolve whatever the reparse points say
            // at that later moment.
            string? launchPath = resolveFinalPath(pin.SafeFileHandle);
            if (launchPath is null)
            {
                pin.Dispose();
                return PlinkAttestationLease.NotAttested;
            }

            PlinkAttestationLease lease = new(pin, launchPath);
            pin = null;
            return lease;
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately fail-closed: an identity that could not be read,
            // an image that could not be pinned, or a path that could not be resolved, is not
            // attested. The path is safe to log - it is a user-chosen executable location, never the
            // file holding the secret.
            Core.Logging.FileLogger.Warn(
                $"[PlinkCompatibilityAttestation] Could not pin the launcher: {ex.Message}");
            pin?.Dispose();
            return PlinkAttestationLease.NotAttested;
        }
    }

    private static string? ResolveFinalPath(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        StringBuilder buffer = new(1024);
        int length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FinalPathNormalizedDos);
        if (length > buffer.Capacity)
        {
            buffer.EnsureCapacity(length);
            length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FinalPathNormalizedDos);
        }

        if (length <= 0 || length > buffer.Capacity)
        {
            Core.Logging.FileLogger.Warn(
                "[PlinkCompatibilityAttestation] Could not resolve the final path of the pinned image.");
            return null;
        }

        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        int pathLength,
        int flags);
}

/// <summary>
/// Holds the attested image in place until the launch has been issued, and names the path that
/// reaches it.
/// </summary>
/// <remarks>
/// Disposing releases the pin. Dispose only once the real call to start the process has returned,
/// because until then the verdict is not yet bound to anything that has been executed.
/// </remarks>
internal sealed class PlinkAttestationLease : IDisposable
{
    /// <summary>
    /// The lease granted when nothing could be attested. Disposing it does nothing.
    /// </summary>
    internal static PlinkAttestationLease NotAttested { get; } = new(null, null);

    private readonly FileStream? _pin;

    internal PlinkAttestationLease(FileStream? pin, string? launchPath)
    {
        _pin = pin;
        LaunchPath = launchPath;
    }

    /// <summary>
    /// Whether the launcher's first byte of output proves it already read the password file.
    /// </summary>
    internal bool FirstByteProvesConsumption => _pin is not null;

    /// <summary>
    /// The path that reaches the pinned image, taken from the handle with every reparse point
    /// already followed, or <see langword="null"/> when nothing was attested.
    /// </summary>
    /// <remarks>
    /// Launch this rather than the configured path. The configured one names directories that can be
    /// repointed between the attestation and the launch, which was measured to run a different
    /// executable; being absolute does not prevent it.
    /// </remarks>
    internal string? LaunchPath { get; }

    /// <summary>
    /// Releases the pinned image. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        _pin?.Dispose();
    }
}
