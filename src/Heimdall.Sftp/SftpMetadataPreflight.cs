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

    /// <summary>Any tool the probe needs to read the metadata is missing on the server.</summary>
    ToolingUnavailable,

    /// <summary>The metadata could not be read, so it cannot be shown to be reproducible.</summary>
    MetadataUnreadable,

    /// <summary>No trusted exec channel was available to characterise the destination.</summary>
    ExecUnavailable,

    /// <summary>The destination carries extended attributes outside the security namespace.</summary>
    ExtendedAttributesPresent,

    /// <summary>The destination belongs to another user, and chown to them needs CAP_CHOWN.</summary>
    OwnershipNotReproducible,
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

    /// <summary>Destination carries extended attributes outside the security namespace.</summary>
    public const int ExtendedAttributeStatus = 16;

    /// <summary>Destination is owned by another user.</summary>
    public const int OwnershipStatus = 17;

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

                // Line-splitting without a literal newline: PathEscaper refuses control characters,
                // so the script cannot contain one and a here-document is unavailable. Command
                // substitution strips trailing newlines, hence the sacrificial 'x'. Globbing is
                // disabled while splitting so an entry containing '*' cannot expand to filenames.
                + "oldifs=$IFS; IFS=$(printf '\\nx'); IFS=${IFS%x}; set -f; "

                // The security namespace is privileged; listing it is not. NAMES are inspected,
                // never values: an attribute that exists with an empty value is still an attribute
                // this session cannot write back, and reading it by value would report it absent.
                + "if command -v getfattr >/dev/null 2>&1; then "
                + $"xattr_raw=$(getfattr --absolute-names -m - -- {path} 2>/dev/null) "
                + $"|| {{ IFS=$oldifs; set +f; exit {UnreadableStatus}; }}; "
                + "sec_found=0; xattr_found=0; "
                + "for line in $xattr_raw; do case \"$line\" in "
                + "''|'#'*) ;; "
                + "security.*) sec_found=1 ;; "
                + "*) xattr_found=1 ;; "
                + "esac; done; "
                + $"if [ \"$sec_found\" -eq 1 ]; then IFS=$oldifs; set +f; exit {SecurityXattrStatus}; fi; "
                + $"if [ \"$xattr_found\" -eq 1 ]; then IFS=$oldifs; set +f; exit {ExtendedAttributeStatus}; fi; "
                + $"else IFS=$oldifs; set +f; exit {ToolingStatus}; fi; "

                // Ownership. A replacement creates a new inode owned by the connecting user, and
                // handing it back to another owner needs CAP_CHOWN, so a destination belonging to
                // somebody else cannot be reproduced. Timestamps are deliberately NOT refused
                // here: the owner can restore mtime and atime, so they are an implementation gap
                // rather than something this session is unable to reproduce.
                + "if command -v stat >/dev/null 2>&1 && command -v id >/dev/null 2>&1; then "
                // GNU stat first, BSD stat (macOS, FreeBSD) second: the probe used to exit as
                // "tooling unavailable" on every non-GNU server, and a replacement there was refused
                // for good. The extended-attribute and ACL tools stay required: without them the
                // destination cannot be characterised, and an uncharacterised destination is not
                // replaced.
                + $"owner_uid=$(stat -c %u -- {path} 2>/dev/null || stat -f %u -- {path} 2>/dev/null) "
                + $"|| {{ IFS=$oldifs; set +f; exit {UnreadableStatus}; }}; "
                + $"self_uid=$(id -u 2>/dev/null) || {{ IFS=$oldifs; set +f; exit {UnreadableStatus}; }}; "
                + "if [ -z \"$owner_uid\" ] || [ -z \"$self_uid\" ]; then "
                + $"IFS=$oldifs; set +f; exit {UnreadableStatus}; fi; "
                + $"if [ \"$owner_uid\" != \"$self_uid\" ]; then IFS=$oldifs; set +f; exit {OwnershipStatus}; fi; "
                + $"else IFS=$oldifs; set +f; exit {ToolingStatus}; fi; "

                // An ACL beyond the base entries cannot be reproduced by mode bits alone. The
                // output is captured on its own so a getfacl failure is seen; the previous shape
                // piped it straight into a filter, and a POSIX pipeline reports only the LAST
                // command's status, so a failed read looked exactly like a clean file.
                + "if command -v getfacl >/dev/null 2>&1; then "
                + $"acl_raw=$(getfacl -c -- {path} 2>/dev/null) "
                + $"|| {{ IFS=$oldifs; set +f; exit {UnreadableStatus}; }}; "
                + "acl_found=0; "

                // Filtering in the shell's own case builtin rather than through grep: an absent
                // grep would fail the pipeline and, absorbed, read as "no extended entries".
                // A mask entry counts, because it only exists when an extended ACL does.
                + "for line in $acl_raw; do case \"$line\" in "
                + "''|'#'*|user::*|group::*|other::*) ;; "
                + "*) acl_found=1 ;; "
                + "esac; done; "
                + $"if [ \"$acl_found\" -eq 1 ]; then IFS=$oldifs; set +f; exit {AclStatus}; fi; "
                + $"else IFS=$oldifs; set +f; exit {ToolingStatus}; fi; "

                + "IFS=$oldifs; set +f; exit 0");
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
            ExtendedAttributeStatus => SftpMetadataPreflightVerdict.ExtendedAttributesPresent,
            OwnershipStatus => SftpMetadataPreflightVerdict.OwnershipNotReproducible,
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
            SftpMetadataPreflightVerdict.ExecUnavailable => "ErrorSftpReplaceRefusedExecUnavailable",
            SftpMetadataPreflightVerdict.ExtendedAttributesPresent => "ErrorSftpReplaceRefusedExtendedAttributes",
            SftpMetadataPreflightVerdict.OwnershipNotReproducible => "ErrorSftpReplaceRefusedOwnership",
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict),
                verdict,
                "A verdict that allows the replacement has no refusal message."),
        };
    }
}
