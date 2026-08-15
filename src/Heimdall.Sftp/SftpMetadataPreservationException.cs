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

namespace Heimdall.Sftp;

/// <summary>
/// Raised when an unprivileged replacement is refused because the destination carries metadata
/// this session could not reproduce.
/// </summary>
/// <remarks>
/// Carries a localization key rather than a message: localized text never crosses the
/// transport-to-application boundary, and the remote shell's own standard error is deliberately
/// not surfaced to the user, since it is unlocalized and may quote paths the operator cannot act
/// on.
/// </remarks>
public sealed class SftpMetadataPreservationException : Exception
{
    public SftpMetadataPreservationException(
        SftpMetadataPreflightVerdict verdict,
        string remotePath)
        : base($"Replacement of '{remotePath}' refused: {verdict}.")
    {
        Verdict = verdict;
        RemotePath = remotePath;
        MessageKey = SftpMetadataPreflight.GetRefusalLocaleKey(verdict);
    }

    /// <summary>Gets the verdict that refused the replacement.</summary>
    public SftpMetadataPreflightVerdict Verdict { get; }

    /// <summary>Gets the destination that was left untouched.</summary>
    public string RemotePath { get; }

    /// <summary>Gets the localization key the application layer resolves for display.</summary>
    public string MessageKey { get; }
}
