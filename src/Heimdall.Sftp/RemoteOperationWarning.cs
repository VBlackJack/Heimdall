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
/// Describes a non-blocking remote operation warning. <see cref="WarningKey"/> is always a
/// localization key; localized text never crosses the transport-to-application boundary.
/// </summary>
public sealed record RemoteOperationWarning
{
    private const string NonAtomicReplacementWarningKey = "WarnRemoteReplacementNonAtomic";

    private const string FtpExistingTargetReplacedWarningKey =
        "WarnFtpReplacementNonAtomicMetadataNotPreserved";

    private const string FtpBackupResidueOriginalMissingWarningKey =
        "WarnFtpBackupResidueOriginalMissing";

    private const string FtpBackupResidueOriginalPresentWarningKey =
        "WarnFtpBackupResidueOriginalPresent";

    private RemoteOperationWarning(string warningKey, string remotePath)
    {
        WarningKey = warningKey;
        RemotePath = remotePath;
    }

    /// <summary>Gets the localization key that the application layer resolves for display.</summary>
    public string WarningKey { get; }

    /// <summary>Gets the final remote path affected by the warning.</summary>
    public string RemotePath { get; }

    /// <summary>Creates the warning raised when an existing destination is replaced non-atomically.</summary>
    public static RemoteOperationWarning CreateNonAtomicReplacement(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        return new RemoteOperationWarning(NonAtomicReplacementWarningKey, remotePath);
    }

    /// <summary>
    /// Creates the single warning raised once an FTP or FTPS upload has replaced an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// One warning, not two. The FTP replacement is non-atomic AND it publishes a freshly uploaded
    /// file, so the destination comes back carrying whatever owner, mode and timestamps the server
    /// assigns to a new upload: the replaced file's ownership, permissions, timestamps, ACLs,
    /// extended attributes and capabilities are gone. FTP exposes no command that would restore
    /// them, so the localized message states that loss rather than implying a preservation that
    /// never happens.
    /// <para>
    /// Deliberately distinct from <see cref="CreateNonAtomicReplacement"/>, which stays the plain
    /// atomicity notice and claims nothing about metadata.
    /// </para>
    /// </remarks>
    public static RemoteOperationWarning CreateFtpExistingTargetReplaced(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        return new RemoteOperationWarning(FtpExistingTargetReplacedWarningKey, remotePath);
    }

    /// <summary>
    /// Creates the warning for a set-aside copy whose original is missing from the listing.
    /// </summary>
    /// <remarks>
    /// The interrupted replacement: the destination was moved aside, the upload never
    /// arrived, and this file holds the only copy of what used to be there. Naming it is the
    /// whole point - before this, the user saw a file with a hex name and no explanation.
    /// </remarks>
    public static RemoteOperationWarning CreateFtpBackupResidueOriginalMissing(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        return new RemoteOperationWarning(FtpBackupResidueOriginalMissingWarningKey, remotePath);
    }

    /// <summary>
    /// Creates the warning for a set-aside copy whose original is present in the listing.
    /// </summary>
    /// <remarks>
    /// A different thing to say, kept apart on purpose. The replacement completed and only
    /// its cleanup failed, so nothing is lost: the file the user wants is there, and this one
    /// holds the version it replaced.
    /// </remarks>
    public static RemoteOperationWarning CreateFtpBackupResidueOriginalPresent(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        return new RemoteOperationWarning(FtpBackupResidueOriginalPresentWarningKey, remotePath);
    }

    /// <summary>
    /// Chooses which of the two residue warnings a finding deserves.
    /// </summary>
    /// <remarks>
    /// The choice lives here rather than at the call site so that it can be tested without a
    /// server. A caller that made the choice itself would be a second copy of the decision,
    /// and collapsing the two branches there would leave every classifier test green while
    /// the user was told the wrong thing about their file.
    /// </remarks>
    public static RemoteOperationWarning ForBackupResidue(FtpBackupResidueFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return finding.OriginalIsPresent
            ? CreateFtpBackupResidueOriginalPresent(finding.RemotePath)
            : CreateFtpBackupResidueOriginalMissing(finding.RemotePath);
    }
}
