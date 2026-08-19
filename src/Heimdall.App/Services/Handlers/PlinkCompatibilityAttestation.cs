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
/// Answers one question about an SSH launcher executable: has its <c>-pwfile</c> behaviour been
/// measured, so that its first byte of output proves the password file was already read.
/// </summary>
/// <remarks>
/// <para>The answer is keyed on the exact bytes of the executable, because that is the only thing
/// that identifies a build. A file name, a directory, a version resource and the string printed by
/// <c>-V</c> are all attacker-chosen or simply wrong for a rebuild, and none of them says anything
/// about when the file is read.</para>
/// <para>This is <b>not</b> a defence against a hostile binary. Heimdall hands the password to
/// whatever executable it was pointed at, so an executable chosen to steal it has already won. What
/// this establishes is narrower and different: whether Heimdall may apply a timing conclusion drawn
/// from one measured build to the binary actually being launched.</para>
/// <para>Unknown bytes are not an error. The connection proceeds; only the early deletion is
/// withheld, and the password file is released at process exit as it always was.</para>
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
    /// Reports whether the first byte written by <paramref name="executablePath"/> proves it has
    /// already consumed its <c>-pwfile</c>.
    /// </summary>
    /// <param name="executablePath">The launcher about to be started.</param>
    /// <returns>
    /// <see langword="true"/> only for an executable whose bytes match a measured build. Anything
    /// else - an unknown build, a missing file, an unreadable file, any failure at all - returns
    /// <see langword="false"/>, which keeps the deletion on the process-exit path.
    /// </returns>
    internal static bool FirstByteProvesConsumption(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            byte[] digest = SHA256.HashData(stream);
            return AttestedIdentities.Contains(Convert.ToHexString(digest));
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately fail-closed: an identity that could not be read is
            // an identity that is not attested. The path is safe to log - it is a user-chosen
            // executable location, never the password file and never its content.
            Core.Logging.FileLogger.Warn(
                $"[PlinkCompatibilityAttestation] Could not read the launcher identity: {ex.Message}");
            return false;
        }
    }
}
