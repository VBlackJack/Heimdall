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
/// Why an unprivileged replacement refuses to touch an existing destination.
/// </summary>
public enum SftpMetadataPreflightVerdict
{
    /// <summary>Nothing on the destination prevents an exact replacement.</summary>
    Proceed,

    /// <summary>The destination is absent, so there is no metadata to reproduce.</summary>
    NoExistingTarget,

    /// <summary>The destination carries file capabilities, which need CAP_SETFCAP to write.</summary>
    CapabilitiesPresent,

    /// <summary>The destination carries security namespace extended attributes.</summary>
    SecurityXattrsPresent,

    /// <summary>The destination carries an ACL beyond the base permission bits.</summary>
    AclPresent,

    /// <summary>A tool needed to read or reproduce the metadata is missing on the server.</summary>
    ToolingUnavailable,

    /// <summary>The metadata could not be read, so it cannot be shown to be reproducible.</summary>
    MetadataUnreadable,
}

/// <summary>
/// Decides, before a single byte is written, whether an unprivileged SFTP replacement can
/// reproduce everything the destination currently carries.
/// </summary>
/// <remarks>
/// The unprivileged path cannot promise what the privileged one does. Writing
/// <c>security.capability</c> requires <c>CAP_SETFCAP</c>, and the <c>security</c> extended
/// attribute namespace is privileged in general, so an ordinary user is unable to put back what
/// it removed. The arbitration for this lot is therefore fail-closed: characterise the target
/// first, and refuse before modifying it rather than replace it and report the loss afterwards.
/// The honest-warning shape used for FTP is deliberately NOT reused here, because FTP has no
/// channel able to see the metadata at all while SFTP does.
/// <para>
/// The verdict travels in the command's EXIT STATUS rather than its output.
/// <see cref="SftpExecResult"/> carries only an exit status and standard error, and a probe that
/// had to be parsed from stdout would turn every locale, busybox variant and trailing-newline
/// difference into a silent misclassification. A status is a closed set.
/// </para>
/// </remarks>
public static class SftpMetadataPreflight
{
    /// <summary>Destination absent: nothing to preserve, the upload is a creation.</summary>
    public const int NoExistingTargetStatus = 10;

    /// <summary>Destination carries capabilities.</summary>
    public const int CapabilitiesStatus = 11;

    /// <summary>Destination carries security namespace extended attributes.</summary>
    public const int SecurityXattrStatus = 12;

    /// <summary>Destination carries an ACL beyond the base mode.</summary>
    public const int AclStatus = 13;

    /// <summary>A required tool is missing on the server.</summary>
    public const int ToolingStatus = 14;

    /// <summary>Metadata is present but could not be read.</summary>
    public const int UnreadableStatus = 15;

    /// <summary>
    /// Builds the probe for one destination path.
    /// </summary>
    /// <remarks>
    /// Ordering is the contract, and it is ordered by severity rather than by cost: capabilities
    /// first, because that is the class an unprivileged user can never restore, then the security
    /// xattr namespace, then ACLs. A destination carrying several classes is reported by its worst
    /// one, so the operator is told the reason that will not go away by changing tooling.
    /// <para>
    /// Absence of a tool is NOT read as absence of metadata. <c>getcap</c> missing means the
    /// question "does this file carry capabilities" is unanswered, and an unanswered question
    /// yields <see cref="ToolingStatus"/>, never a pass. That is the whole point of a fail-closed
    /// probe: it distinguishes "no" from "cannot tell".
    /// </para>
    /// </remarks>
    public static string Build(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        string path = PathEscaper.EscapeForShell(remotePath);

        return "LC_ALL=C sh -c "
            + PathEscaper.EscapeForShell(
                // A destination that is not there cannot lose anything.
                $"if [ ! -e {path} ]; then exit {NoExistingTargetStatus}; fi; "

                // Capabilities: refused first because CAP_SETFCAP is required to put them back,
                // so no amount of remote tooling makes this recoverable for an ordinary user.
                + "if command -v getcap >/dev/null 2>&1; then "
                + $"caps=$(getcap -- {path} 2>/dev/null) || exit {UnreadableStatus}; "
                + $"if [ -n \"$caps\" ]; then exit {CapabilitiesStatus}; fi; "
                + $"else exit {ToolingStatus}; fi; "

                // The security namespace is privileged; listing it is not.
                + "if command -v getfattr >/dev/null 2>&1; then "
                + $"sec=$(getfattr --absolute-names -m '^security\\.' --only-values -d -- {path} 2>/dev/null "
                + $"|| getfattr --absolute-names -m '^security\\.' -- {path} 2>/dev/null) || exit {UnreadableStatus}; "
                + $"if [ -n \"$sec\" ]; then exit {SecurityXattrStatus}; fi; "
                + $"else exit {ToolingStatus}; fi; "

                // An ACL beyond the base entries cannot be reproduced by mode bits alone.
                + "if command -v getfacl >/dev/null 2>&1; then "
                + $"acl=$(getfacl -cE -- {path} 2>/dev/null "
                + "| grep -Ev '^(user|group|other)::' | grep -v '^$') "
                + $"|| true; "
                + $"if [ -n \"$acl\" ]; then exit {AclStatus}; fi; "
                + $"else exit {ToolingStatus}; fi; "

                + "exit 0");
    }

    /// <summary>
    /// Maps a probe exit status to the decision the caller acts on.
    /// </summary>
    /// <remarks>
    /// Every status outside the documented set is <see cref="SftpMetadataPreflightVerdict.MetadataUnreadable"/>,
    /// never <see cref="SftpMetadataPreflightVerdict.Proceed"/>. A shell that died on a signal, a
    /// server that answered something unforeseen, or a future status added without updating this
    /// map all mean the same thing: the destination was not shown to be safe to replace.
    /// </remarks>
    public static SftpMetadataPreflightVerdict Classify(int exitStatus)
    {
        return exitStatus switch
        {
            0 => SftpMetadataPreflightVerdict.Proceed,
            NoExistingTargetStatus => SftpMetadataPreflightVerdict.NoExistingTarget,
            CapabilitiesStatus => SftpMetadataPreflightVerdict.CapabilitiesPresent,
            SecurityXattrStatus => SftpMetadataPreflightVerdict.SecurityXattrsPresent,
            AclStatus => SftpMetadataPreflightVerdict.AclPresent,
            ToolingStatus => SftpMetadataPreflightVerdict.ToolingUnavailable,
            UnreadableStatus => SftpMetadataPreflightVerdict.MetadataUnreadable,
            _ => SftpMetadataPreflightVerdict.MetadataUnreadable,
        };
    }

    /// <summary>
    /// Whether a verdict allows the replacement to continue.
    /// </summary>
    /// <remarks>
    /// Only two verdicts allow it: nothing to preserve, or nothing present that the unprivileged
    /// path cannot reproduce. Written as an allow-list so a verdict added later is refused until
    /// somebody decides otherwise, rather than admitted by omission.
    /// </remarks>
    public static bool AllowsReplacement(SftpMetadataPreflightVerdict verdict)
    {
        return verdict is SftpMetadataPreflightVerdict.Proceed
            or SftpMetadataPreflightVerdict.NoExistingTarget;
    }

    /// <summary>
    /// Returns the localization key describing a refusal.
    /// </summary>
    public static string GetRefusalLocaleKey(SftpMetadataPreflightVerdict verdict)
    {
        return verdict switch
        {
            SftpMetadataPreflightVerdict.CapabilitiesPresent => "ErrorSftpReplaceRefusedCapabilities",
            SftpMetadataPreflightVerdict.SecurityXattrsPresent => "ErrorSftpReplaceRefusedSecurityXattrs",
            SftpMetadataPreflightVerdict.AclPresent => "ErrorSftpReplaceRefusedAcl",
            SftpMetadataPreflightVerdict.ToolingUnavailable => "ErrorSftpReplaceRefusedTooling",
            SftpMetadataPreflightVerdict.MetadataUnreadable => "ErrorSftpReplaceRefusedUnreadable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict),
                verdict,
                "A verdict that allows the replacement has no refusal message."),
        };
    }
}
