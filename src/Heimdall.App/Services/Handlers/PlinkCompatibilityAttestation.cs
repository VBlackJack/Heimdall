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
using System.Security.Cryptography;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Answers one question about an SSH launcher executable, and holds the answer to the image that is
/// actually launched: has its <c>-pwfile</c> behaviour been measured, so that its first byte of
/// output proves the password file was already read.
/// </summary>
/// <remarks>
/// <para>The answer is keyed on the exact bytes of the executable, because that is the only thing
/// that identifies a build. A file name, a directory, a version resource and the string printed by
/// <c>-V</c> are all attacker-chosen or simply wrong for a rebuild, and none of them says anything
/// about when the file is read.</para>
/// <para>Hashing alone was not enough. Between the hash and the launch the handler can wait on an
/// interactive password dialog, an unbounded delay during which a perfectly legitimate update could
/// replace the executable; the new, unmeasured build would then inherit the previous verdict. So the
/// answer comes with a lease: the file is opened once, the digest is computed from that same handle,
/// and when it matches the handle stays open - denying writes and deletion - until the launch has
/// been issued. Measured on a temporary copy: while that lease is held the image still starts, and
/// both replacing and writing to the file are refused.</para>
/// <para>This is <b>not</b> a defence against a hostile binary. Heimdall hands the password to
/// whatever executable it was pointed at, so an executable chosen to steal it has already won. What
/// this establishes is narrower: that the timing conclusion drawn from one measured build belongs to
/// the image that runs.</para>
/// <para>One residual, stated rather than glossed: the pin is taken on a path, and the launch
/// resolves that same string again. For an absolute path - the default setting, and what the file
/// dialog produces - both name the same file, so the binding holds. A relative path would be
/// resolved once against the current directory here and once against the process search order at
/// launch, and those need not agree.</para>
/// <para>Failure is not an error. The connection proceeds; only the early deletion is withheld, and
/// the password file is released at process exit as it always was.</para>
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

    private static readonly HashSet<string> AttestedIdentities =
        new(StringComparer.OrdinalIgnoreCase) { MeasuredPuttyRelease083Sha256 };

    /// <summary>
    /// Identifies <paramref name="executablePath"/> and, when it is a measured build, pins that
    /// exact image until the returned lease is disposed.
    /// </summary>
    /// <param name="executablePath">The launcher about to be started.</param>
    /// <returns>
    /// A lease that grants the early deletion only for an executable whose bytes match a measured
    /// build and whose image could be pinned. Anything else - an unknown build, a missing file, a
    /// file already open for writing elsewhere, any failure at all - yields a lease that grants
    /// nothing, which keeps the deletion on the process-exit path. Never null; always dispose it.
    /// </returns>
    internal static PlinkAttestationLease Acquire(string? executablePath)
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

            PlinkAttestationLease lease = new(pin);
            pin = null;
            return lease;
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately fail-closed: an identity that could not be read,
            // or an image that could not be pinned, is not attested. The path is safe to log - it is
            // a user-chosen executable location, never the file holding the secret.
            Core.Logging.FileLogger.Warn(
                $"[PlinkCompatibilityAttestation] Could not pin the launcher: {ex.Message}");
            pin?.Dispose();
            return PlinkAttestationLease.NotAttested;
        }
    }
}

/// <summary>
/// Holds the attested image in place until the launch has been issued.
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
    internal static PlinkAttestationLease NotAttested { get; } = new(null);

    private readonly FileStream? _pin;

    internal PlinkAttestationLease(FileStream? pin)
    {
        _pin = pin;
    }

    /// <summary>
    /// Whether the launcher's first byte of output proves it already read the password file.
    /// </summary>
    internal bool FirstByteProvesConsumption => _pin is not null;

    /// <summary>
    /// Releases the pinned image. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        _pin?.Dispose();
    }
}
