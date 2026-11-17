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

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace Heimdall.Sftp;

/// <summary>
/// Builds privileged file commands that never stage content in a shared directory.
/// </summary>
public static class PrivilegedFileCommands
{
    /// <summary>A creation-only privileged upload, root create-folder or rename found the destination name occupied.</summary>
    public const int DestinationExistsExitStatus = 78;
    /// <summary>Exit status used when a privileged edit exceeds its configured size limit.</summary>
    public const int FileTooLargeExitStatus = 75;

    /// <summary>
    /// Reported when the replaced file's metadata could not be carried over to the payload.
    /// </summary>
    /// <remarks>
    /// The rename never runs in that case, so the old target survives untouched. A file that loses
    /// its ACLs, capabilities or timestamps on replacement is a security regression, not a detail
    /// to swallow - so this refuses rather than publishing a file with weaker metadata.
    /// <para>
    /// 76, not 74: the no-follow read path already exits 74 for a non-regular source, and reusing
    /// it would make two unrelated refusals indistinguishable to the caller. Kept in step with the
    /// literal the script exits with; a test pins the two together.
    /// </para>
    /// </remarks>
    public const int MetadataPreservationFailedExitStatus = 76;

    /// <summary>
    /// Exit status of a privileged script that found one of the tools it needs missing, or a cp
    /// that is not GNU coreutils. Distinct from 73 (non-regular target), 74 (non-regular source)
    /// 75 (file too large) and 76 (metadata not preserved): the caller names the missing tool rather than reporting
    /// a failed transfer. Kept in step with the literal the probe exits with; a test pins the two.
    /// </summary>
    public const int ToolingUnavailableExitStatus = 77;

    /// <summary>
    /// Exit status of the user-side stage of a privileged write when the content could not be
    /// staged in the server's temporary directory (no space, no directory, no <c>mktemp</c>).
    /// Nothing privileged ran and the target is untouched. Kept in step with the literal the
    /// outer script exits with; a test pins the two.
    /// </summary>
    public const int StagingFailedExitStatus = 74;

    /// <summary>
    /// The first line the root-side installer writes to standard error. Its presence means sudo
    /// accepted the call and the installer started, so every status then belongs to the installer;
    /// its absence means the failure came from the user-side stage or from sudo itself.
    /// </summary>
    public const string InstallStartedMarker = "HEIMDALL-INSTALL-STARTED";

    /// <summary>The exit status a shell gives a command it could not find.</summary>
    public const int CommandNotFoundExitStatus = 127;

    /// <summary>
    /// Exit status of a privileged script that refused a target that is not a regular file the
    /// script could verify (a symbolic link, a special file, an entry that changed under it).
    /// Kept in step with the literal the scripts exit with; a test pins the two.
    /// </summary>
    public const int NonRegularTargetExitStatus = 73;

    /// <summary>
    /// Exit status of a privileged read that refused its source. Kept in step with the literal the
    /// read script exits with; a test pins the two.
    /// </summary>
    public const int NonRegularSourceExitStatus = 74;

    private static readonly Regex TransferRefusalLine = new(
        "^" + TransferRefusedMarker + " (" + RefusalReasonPathLink + "|" + RefusalReasonHardLink + ")\\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The first line the root-side chmod writes to standard error when it refuses an entry. With
    /// <see cref="NonRegularTargetExitStatus"/> it tells a refusal of the entry from every other
    /// failure that happens to end with the same status: both must be present.
    /// </summary>
    public const string EntryRefusedMarker = "HEIMDALL-ENTRY-REFUSED";

    /// <summary>
    /// The first line the root-side write or read prints on standard error when it refuses a path
    /// (<see cref="RefusalReasonPathLink"/>) or a file (<see cref="RefusalReasonHardLink"/>). It is
    /// read together with the exit status (73 for a write, 74 for a read), and only when it starts
    /// a line.
    /// </summary>
    public const string TransferRefusedMarker = "HEIMDALL-TRANSFER-REFUSED";

    // The same status as text: a constant string cannot be built from the integer. A test pins the two.
    private const string NonRegularTargetExitStatusText = "73";

    // The status of a refused read, as text for the same reason. A test pins it to the script.
    private const string NonRegularSourceExitStatusText = "74";

    // The status of a missing tool, as text for the same reason. A test pins it to the script.
    private const string ToolingUnavailableExitStatusText = "77";

    // The status of a destination that is taken, as text for the same reason. A test pins it to the scripts.
    private const string DestinationExistsExitStatusText = "78";

    private const string PasswordMode = "password";
    private const string NoPasswordMode = "none";
    private const string BangPlaceholder = "^";

    /// <summary>
    /// The scripts below are written for GNU coreutils (<c>stat -c</c>, <c>cp --attributes-only</c>,
    /// <c>sync -f</c>, <c>mv -T</c>). On a BSD or busybox server they used to fail on the first
    /// such flag with a message about that flag; the probe names the missing tool instead, and
    /// refuses before anything is staged.
    /// </summary>
    private const string ToolingProbe =
        "for t in stat cp sync mv ln mktemp chmod chown rm rmdir base64 cat; do "
        + "command -v \"$t\" >/dev/null 2>&1 || { printf '%s\\n' \"Refusing: $t is not available on the server.\" >&2; exit 77; }; done; "
        + "case \"$(stat --version 2>/dev/null)\" in *GNU*) ;; *) "
        + "printf '%s\\n' 'Refusing: GNU coreutils are required for a privileged transfer.' >&2; exit 77;; esac; ";

    // The refusal helper of the two scripts below. It writes the reason on standard error and ends
    // the script, and the shell's exit trap then runs the cleanup. The reason token (its first
    // argument) is only read by the permission change script, so it is ignored here. A write
    // refuses with 73 and a read with 74: the statuses each path already reported for a refused
    // entry. Neither prints the marker that the permission change uses, which would be read as a
    // refusal of that kind.
    private const string WriteRefuseFunction =
        "refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularTargetExitStatusText + "; }; ";

    private const string ReadRefuseFunction =
        "refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularSourceExitStatusText + "; }; ";

    // The refusal of a path that goes through a link that is not trusted, and of a file that has
    // other hard links in a folder others can write to. It prints the reason on a line of its own,
    // so the application can name it, then ends the script: the exit trap runs the cleanup.
    private const string WriteDenyFunction =
        "deny() { printf '%s %s\\n' '" + TransferRefusedMarker + "' \"$1\" >&2; "
        + "printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularTargetExitStatusText + "; }; ";

    private const string ReadDenyFunction =
        "deny() { printf '%s %s\\n' '" + TransferRefusedMarker + "' \"$1\" >&2; "
        + "printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularSourceExitStatusText + "; }; ";

    // The permission change and the delete report their refusals with the entry marker.
    private const string EntryDenyFunction =
        "deny() { printf '%s %s\\n' '" + EntryRefusedMarker + "' \"$1\" >&2; "
        + "printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularTargetExitStatusText + "; }; ";

    // The command search path of the two scripts, reduced to its absolute entries before any
    // command is looked up. An empty or relative entry would resolve a command name in the
    // current directory, which the scripts change. No directory is added: a server keeps its
    // tools where it keeps them. Only shell built-ins run here, and the pattern expansion is
    // switched off while the entries are split.
    private const string AbsolutePathOnly =
        "set -f; IFS=:; clean_path=; for entry in ${PATH-}; do "
        + "case \"$entry\" in /*) clean_path=${clean_path:+$clean_path:}$entry ;; esac; done; unset IFS; set +f; "
        + "[ -n \"$clean_path\" ] || { printf '%s\\n' "
        + "'Refusing: no absolute directory is listed in the command search path.' >&2; exit "
        + ToolingUnavailableExitStatusText + "; }; PATH=$clean_path; ";

    // The name of the marker directory a run creates inside its work directory once the work
    // directory is verified. Creating it is an all-or-nothing step, so two runs that ended up in
    // the same work directory cannot both go on.
    private const string ClaimName = "claim";

    // The last part of both cleanups, which also closes the function: remove the work directory
    // by its own name, relative to the directory that holds it, if that directory is the held
    // parent and the work directory is empty. It is gated on the
    // work variable, which holds the name only once the directory has been verified, and it clears
    // the variable so a second run does nothing.
    private const string RemoveWorkDirectory =
        "if [ -n \"$work\" ]; then w=$work; work=; cd -P .. 2>/dev/null "
        + "&& [ \"$(stat -c %d:%i .)\" = \"$parent\" ] && rmdir -- \"./$w\" 2>/dev/null || :; fi; cd /; }; ";

    // What the cleanup removes from the work directory, in addition to the files of the script.
    private const string RemoveClaim = "rmdir -- " + ClaimName + " 2>/dev/null || :; ";

    // Whether a folder is open to a change by an account the script does not trust: the folder is
    // owned by neither root, the account that called sudo, nor the owner given as the third
    // argument, or its mode (read from stat, three or four octal digits) grants write to group or
    // others. A mode printed with fewer digits is read as open. Arguments: owner, mode, file owner.
    // The function returns 0 when the folder is open.
    private const string OpenFolderFunction =
        "open_folder() { case \"$1\" in 0|\"$caller\"|\"$3\") ;; *) return 0;; esac; "
        + "case \"$2\" in [0-7][0-7][0-7]|[0-7][0-7][0-7][0-7]) ;; *) return 0;; esac; "
        + "return $(( (0$2 & 022) == 0 )); }; ";

    // The texts the walk below prints.
    private const string PathLinkMessage = "a symbolic link in the path is not trusted.";

    private const string WalkChangedMessage = "the parent directory changed during the check.";

    /// <summary>
    /// The component walk of the parent folder shared by the privileged write, read, permission
    /// change, delete, create-folder and rename. It needs the <c>refuse</c> and <c>deny</c> functions, the <c>dir</c>
    /// variable (an absolute path) and the <c>me</c> variable (the running account), and leaves the
    /// shell inside the folder, held, as the plain <c>cd -P</c> of the whole path used to.
    /// </summary>
    /// <remarks>
    /// The path is walked from the root one component at a time, each step by <c>cd -P</c> into a
    /// folder named relative to the folder held (<c>./name</c> or <c>..</c>), so the command search
    /// path and the working directory of the caller play no part. A component that is a symbolic
    /// link is followed only when the link is owned by root or by the account that called sudo
    /// (<c>SUDO_UID</c>, else the running account) and the folder that holds the link is owned by
    /// one of them and grants no write to group or others (<see cref="OpenFolderFunction"/>); the
    /// link target is walked the same way, with a limit of forty links. Any other link is denied
    /// with the reason <see cref="RefusalReasonPathLink"/>. After each step the folder entered is
    /// checked against the one named: for a link-free name by the identity read before the entry,
    /// and, only when the device differs (a mount appeared on entry), by its parent; for <c>..</c>
    /// by its identity. A <c>stat</c> or <c>readlink</c>
    /// message never reaches standard error.
    /// </remarks>
    internal const string HeldParentWalk =
        OpenFolderFunction
        + "command -v readlink >/dev/null 2>&1 || { printf '%s\\n' "
        + "'Refusing: readlink is not available on the server.' >&2; exit " + ToolingUnavailableExitStatusText + "; }; "
        + "caller=${SUDO_UID:-$me}; hops=0; "
        + "case \"$dir\" in /*) ;; *) refuse " + RefusalReasonInvalid + " 'the path is not absolute.';; esac; "
        + "rest=${dir#/}; cd /; set -f; IFS=/; set -- $rest; unset IFS; set +f; "
        + "while [ $# -gt 0 ]; do c=$1; shift; "
        + "case \"$c\" in ''|.) continue;; ..) dir=..;; *) dir=./$c;; esac; "
        + "before=$(stat -c %d:%i . 2>/dev/null) || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "want=$(stat -c %d:%i:%u -- \"$dir\" 2>/dev/null) || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "if [ -L \"$dir\" ]; then : $((hops += 1)); "
        + "if [ \"$hops\" -gt 40 ]; then deny " + RefusalReasonPathLink + " '" + PathLinkMessage + "'; fi; "
        + "hold=$(stat -c %u:%a . 2>/dev/null) || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "if open_folder \"${hold%%:*}\" \"${hold#*:}\" \"\"; then deny " + RefusalReasonPathLink + " '" + PathLinkMessage + "'; fi; "
        + "case \"${want##*:}\" in 0|\"$caller\") ;; *) deny " + RefusalReasonPathLink + " '" + PathLinkMessage + "';; esac; "
        + "t=$(readlink -- \"$dir\" 2>/dev/null) || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "case \"$t\" in /*) cd /;; esac; "
        + "set -f; IFS=/; set -- $t \"$@\"; unset IFS; set +f; continue; fi; "
        + "[ -d \"$dir\" ] || refuse " + RefusalReasonSpecial + " 'a folder of the path is not a directory.'; "
        + "cd -P -- \"$dir\" || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "if [ \"$c\" = .. ]; then [ \"$(stat -c %d:%i:%u . 2>/dev/null)\" = \"$want\" ] "
        + "|| refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "else now=$(stat -c %d:%i . 2>/dev/null) || refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "if [ \"$now\" = \"${want%:*}\" ]; then :; "
        + "elif [ \"${now%%:*}\" = \"${want%%:*}\" ]; then refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; "
        + "else [ \"$(stat -c %d:%i .. 2>/dev/null)\" = \"$before\" ] "
        + "|| refuse " + RefusalReasonChanged + " '" + WalkChangedMessage + "'; fi; fi; "
        + "done; ";

    // The check of a pinned file: it is refused when another name reaches it (the link count, which
    // includes the pin) and the folder holding it is open to an account the script does not trust.
    // The file is named between the two halves. It needs the pin to be in the working directory,
    // whose parent is the held folder.
    private const string HardLinkCheckStart = "links=$(stat -c %h:%u -- ";

    private const string HardLinkCheckEnd =
        " 2>/dev/null) || refuse " + RefusalReasonUnverifiable + " 'the file could not be checked.'; "
        + "pinfo=$(stat -c %u:%a .. 2>/dev/null) || refuse " + RefusalReasonUnverifiable + " 'the file could not be checked.'; "
        + "if [ \"${links%%:*}\" -gt 2 ] && open_folder \"${pinfo%%:*}\" \"${pinfo#*:}\" \"${links#*:}\"; then "
        + "deny " + RefusalReasonHardLink + " 'the file has other hard links in a folder that others can write to.'; fi; ";

    /// <summary>
    /// The first half of the entry procedure shared by the privileged write and read: it finds the
    /// entry name, holds the parent directory and arms the exit and signal handlers. It needs the
    /// <c>refuse</c> and <c>cleanup</c> functions, the <c>target</c> and <c>dir</c> variables, and
    /// is followed by the creation of the work directory and by <see cref="VerifiedEntryClosing"/>.
    /// </summary>
    internal const string VerifiedEntryOpening =
        "name=${target##*/}; case \"$name\" in ''|.|..) refuse " + RefusalReasonInvalid + " 'no entry name.';; esac; "
        + "me=$(id -u) || refuse " + RefusalReasonUnverifiable + " 'the account could not be identified.'; "
        + HeldParentWalk
        + "parent=$(stat -c %d:%i .) || refuse " + RefusalReasonChanged + " 'the parent directory changed during the check.'; "
        + "pdev=$(stat -c %d .) || refuse " + RefusalReasonChanged + " 'the parent directory changed during the check.'; "
        + "trap cleanup EXIT; trap 'exit 129' HUP INT TERM; ";

    /// <summary>
    /// The second half of the shared entry procedure. The work directory is trusted only after it
    /// is entered and checked: owned by the running account, not writable by group or others,
    /// inside the held parent on the same device, and empty. Only then are <c>entered</c> and
    /// <c>work</c> set, and from then on the cleanup may remove the files this script made and the
    /// directory itself: before that, the cleanup removes nothing. A last step creates the marker
    /// directory named by <c>ClaimName</c>, which fails if another run already did. The entry is
    /// afterwards reached as <c>../$name</c>, relative to the directory the script holds.
    /// </summary>
    internal const string VerifiedEntryClosing =
        "cd -P -- \"$made\" || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be entered.'; "
        + "info=$(stat -c %u:%a .) || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be verified.'; "
        + "owner=${info%%:*}; perm=${info#*:}; "
        + "case \"$perm\" in [0-7][0-7][0-7]|[0-7][0-7][0-7][0-7]) ;; "
        + "*) refuse " + RefusalReasonUnverifiable + " 'the private directory could not be verified.';; esac; "
        + "[ \"$owner\" = \"$me\" ] && [ $((0$perm & 022)) -eq 0 ] "
        + "|| refuse " + RefusalReasonUnverifiable + " 'the private directory is not private to the account.'; "
        + "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] && [ \"$(stat -c %d .)\" = \"$pdev\" ] "
        + "|| refuse " + RefusalReasonUnverifiable + " 'the private directory could not be verified.'; "
        + "listing=$(ls -A) || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be listed.'; "
        + "[ -z \"$listing\" ] || refuse " + RefusalReasonUnverifiable + " 'the private directory is not empty.'; "
        + "mkdir -- " + ClaimName + " || refuse " + RefusalReasonUnverifiable + " 'the private directory is in use.'; "
        + "work=${made#./}; entered=1; target=../$name; ";

    private const string WriteWorkDirectoryCreation =
        "made=$(mktemp -d -- \"./.heimdall-write.XXXXXXXXXX\") || refuse "
        + RefusalReasonUnverifiable + " 'the private directory could not be created.'; ";

    private const string ReadWorkDirectoryCreation =
        "made=$(mktemp -d -- \"./.heimdall-read.XXXXXXXXXX\") || refuse "
        + RefusalReasonUnverifiable + " 'the private directory could not be created.'; ";

    private const string AtomicWriteScript =
        "set -eu; umask 077; target=$1; work=; entered=0; pdev=; "
        + AbsolutePathOnly
        + ToolingProbe
        + WriteRefuseFunction
        + WriteDenyFunction
        + "cleanup() { if [ \"$entered\" = 1 ]; then rm -f -- original payload || :; " + RemoveClaim + "entered=0; fi; "
        + RemoveWorkDirectory
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + VerifiedEntryOpening
        + WriteWorkDirectoryCreation
        + VerifiedEntryClosing
        + "preserve_metadata=0; "
        + "if [ -e \"$target\" ] || [ -L \"$target\" ]; then "
        + "if [ \"$replace\" -eq 0 ]; then printf '%s\\n' 'Refusing existing destination.' >&2; exit 78; fi; "
        + "ln -P -- \"$target\" original "
        + "|| refuse " + RefusalReasonUnverifiable + " 'the file could not be pinned.'; "
        + "if [ -L original ] || [ ! -f original ]; then "
        + "printf '%s\\n' 'Refusing non-regular or symbolic-link target.' >&2; exit 73; fi; "
        + HardLinkCheckStart + "original" + HardLinkCheckEnd
        + "original_owner=$(stat -c %u:%g -- original); "
        + "original_mode=$(stat -c %a -- original); preserve_metadata=1; fi; "
        + "cat > payload; "
        + "if [ \"$preserve_metadata\" -eq 1 ]; then "
        + "payload_size=$(stat -c %s -- payload); "
        // Owner and mode first, then the extended attributes - never the other way round. On Linux
        // a chown clears the file capabilities stored in security.capability, so running it after
        // the attribute copy would silently strip exactly what that copy just restored.
        + "chown -- \"$original_owner\" payload; chmod -- \"$original_mode\" payload; "
        // The preserved set is listed explicitly rather than using --preserve=all, because GNU cp
        // does NOT fail when "all" cannot carry the extended attributes or the SELinux context: it
        // warns and exits zero. Naming xattr makes a failure a failure. "original" is a hard link
        // to the target, so it IS the target inode - these are the attributes the replaced file
        // really carried, and ACLs, capabilities and the SELinux context all travel as xattrs.
        + "if cp --attributes-only --preserve=mode,ownership,timestamps,xattr -- original payload; "
        + "then :; else "
        + "printf '%s\\n' 'Refusing: could not preserve the replaced file metadata.' >&2; "
        + "exit 76; fi; "
        // --attributes-only is documented not to touch the data, but this script cannot verify
        // which coreutils build is on the far side, and a truncating one would publish an empty
        // file. Measured across the copy, and refused if it moved.
        + "if [ \"$(stat -c %s -- payload)\" != \"$payload_size\" ]; then "
        + "printf '%s\\n' 'Refusing: the attribute copy altered the payload.' >&2; "
        + "exit 76; fi; else "
        // A new file: umask 077 kept it private while it was written in the private work directory,
        // but it is published like "sudo cp" would, readable by others. The chmod runs only when all
        // of these hold: the work directory carries no ACL (GNU ls marks a directory with any default
        // ACL, base-only included, with a plus, and the work directory inherits the folder's), the
        // payload carries none, and its mode is still the 600 that "cat >" gives under umask 077. In
        // every other case the folder's default ACL decided and must not be widened. An ls that fails
        // counts as an ACL (leave alone). Best effort: some filesystems (vfat, CIFS) refuse the mode
        // change, and that must not fail the write.
        + "work_acl=$(ls -ld -- . 2>/dev/null) || work_acl=----------+; "
        + "payload_acl=$(ls -ld -- payload 2>/dev/null) || payload_acl=----------+; "
        + "case \"$work_acl\" in ??????????+*) ;; *) case \"$payload_acl\" in ??????????+*) ;; *) "
        + "if [ \"$(stat -c %a -- payload)\" = 600 ]; then chmod -- 0644 payload 2>/dev/null || :; fi "
        + ";; esac ;; esac; fi; "
        // After the metadata, so what reaches the disk is the file as it will be published.
        + "sync -f payload; "
        + "rm -f -- original; "
        // The directory held since the entry is checked again right before the rename, so the
        // payload is only ever published into the directory it was prepared for.
        + "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] "
        + "|| refuse " + RefusalReasonChanged + " 'the parent directory changed during the write.'; "
        + "if [ -L \"$target\" ]; then "
        + "printf '%s\\n' 'Refusing symbolic-link target.' >&2; exit 73; fi; "
        + "if [ \"$replace\" -eq 1 ]; then mv -fT -- payload \"$target\"; "
        + "else if ln -T -- payload \"$target\"; then :; "
        + "else if [ -e \"$target\" ] || [ -L \"$target\" ]; then exit 78; else exit 1; fi; fi; "
        + "rm -f -- payload; fi; "
        + "sync -f ..; cleanup; trap - EXIT HUP INT TERM";

    private const string NoFollowReadPrefix =
        "set -eu; umask 077; target=$1; work=; entered=0; pdev=; "
        + AbsolutePathOnly
        + ToolingProbe
        + ReadRefuseFunction
        + ReadDenyFunction
        + "cleanup() { if [ \"$entered\" = 1 ]; then rm -f -- source || :; " + RemoveClaim + "entered=0; fi; "
        + RemoveWorkDirectory
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + VerifiedEntryOpening
        + ReadWorkDirectoryCreation
        + VerifiedEntryClosing
        + "if ln -P -- \"$target\" source 2>/dev/null; then :; else "
        + "ln -- \"$target\" source || refuse " + RefusalReasonUnverifiable + " 'the file could not be pinned.'; fi; "
        + "if [ -L source ] || [ ! -f source ]; then "
        + "printf '%s\\n' 'Refusing non-regular or symbolic-link source.' >&2; exit " + NonRegularSourceExitStatusText + "; fi; "
        + HardLinkCheckStart + "source" + HardLinkCheckEnd
        + "exec 3< source; rm -f -- source; ";

    private const string NoFollowReadSuffix =
        "base64 <&3; exec 3<&-; cleanup; trap - EXIT HUP INT TERM";

    /// <summary>The refusal reason tokens the script prints after the marker. A test pins them to the script.</summary>
    public const string RefusalReasonLink = "link";

    /// <summary>The target is a file that is neither regular nor a directory.</summary>
    public const string RefusalReasonSpecial = "special";

    /// <summary>The entry is not the one that was listed.</summary>
    public const string RefusalReasonChanged = "changed";

    /// <summary>The server cannot do the safe method here (no hard link, no private directory).</summary>
    public const string RefusalReasonUnverifiable = "unverifiable";

    /// <summary>The request itself is not valid (mode, entry name).</summary>
    public const string RefusalReasonInvalid = "invalid";

    /// <summary>The path goes through a symbolic link that is not trusted.</summary>
    public const string RefusalReasonPathLink = "pathlink";

    /// <summary>The file has other hard links and sits in a folder that others can write to.</summary>
    public const string RefusalReasonHardLink = "hardlink";

    // The shared start of the scripts that act on one entry by its name in a parent folder they
    // hold: the command search path, the tooling probe, the refusal functions, the split of the
    // target into a folder and a name (an empty name, "." and ".." are refused as invalid) and the
    // walk of the parent. It expects the target in the variable target.
    private const string HeldEntryPrologue =
        AbsolutePathOnly
        + ToolingProbe
        + "refuse() { printf '%s %s\\n' '" + EntryRefusedMarker + "' \"$1\" >&2; "
        + "printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularTargetExitStatusText + "; }; "
        + EntryDenyFunction
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + "name=${target##*/}; case \"$name\" in ''|.|..|*/*) refuse " + RefusalReasonInvalid + " 'no entry name.';; esac; "
        + "me=$(id -u) || refuse " + RefusalReasonUnverifiable + " 'the account could not be identified.'; "
        + HeldParentWalk;

    /// <summary>
    /// The script root runs to delete one entry (a file, a link or a folder with its content) by its
    /// name in a parent folder it holds. Positional parameter: the absolute path (<c>$1</c>).
    /// </summary>
    /// <remarks>
    /// The parent is reached by <see cref="HeldParentWalk"/>, so a link in the path is followed only
    /// when it is trusted; the entry is then removed as <c>./name</c> from the held parent, and a
    /// link in that last position is removed itself, never followed. A refusal exits with 73 and
    /// prints <see cref="EntryRefusedMarker"/> and one of <c>pathlink</c>, <c>invalid</c>,
    /// <c>changed</c>, <c>special</c> or <c>unverifiable</c>.
    /// </remarks>
    private const string HeldDeleteScript =
        "set -eu; umask 077; target=$1; "
        + HeldEntryPrologue;

    // The tail of the delete of a file or a link: a folder at that name is refused, untouched.
    private const string HeldDeleteFileTail =
        "if [ -L \"./$name\" ]; then :; elif [ -d \"./$name\" ]; then refuse "
        + RefusalReasonChanged + " 'the entry is a folder now.'; fi; rm -f -- \"./$name\"";

    private const string HeldDeleteFolderTail = "rm -rf -- \"./$name\"";

    /// <summary>
    /// The script root runs to change the permissions of one entry without following a link.
    /// Positional parameters: the octal mode (<c>$1</c>) and the target (<c>$2</c>).
    /// </summary>
    /// <remarks>
    /// A directory is changed through the working directory the script holds after checking that it
    /// is the entry it listed. A regular file is changed through a hard link made inside a private
    /// root-only directory next to it: <c>ln -P -T</c> links a symbolic link itself rather than what
    /// it points to, so a link found there is seen as one and refused, and the
    /// only operand of the file chmod is that private link.
    /// <para>
    /// The private directory is trusted only after it is entered and verified: root-owned, mode 700
    /// (2700 where the parent is setgid), the held parent as its parent, the same device, and empty.
    /// Only then are <c>entered</c> and <c>work</c> set, and only then may the cleanup remove
    /// <c>source</c> or the directory: before that the shell may still be in the user's own
    /// directory, or in a directory other than the one it created, and what is there is not its own.
    /// A failed verification therefore leaves our own directory behind, empty, rather than risk
    /// removing a foreign one. The cleanup clears both flags, so a second run does nothing. The
    /// signal traps exit, so the cleanup never lets the script carry on after a signal. The script
    /// holds no <c>!</c> and no newline, and takes every path as a positional parameter.
    /// </para>
    /// </remarks>
    private const string NoFollowChmodScript =
        "set -eu; umask 077; mode=$1; target=$2; work=; entered=0; pdev=; "
        + AbsolutePathOnly
        + ToolingProbe
        + "refuse() { printf '%s %s\\n' '" + EntryRefusedMarker + "' \"$1\" >&2; "
        + "printf '%s\\n' \"Refusing: $2\" >&2; exit " + NonRegularTargetExitStatusText + "; }; "
        + EntryDenyFunction
        + "cleanup() { if [ \"$entered\" = 1 ]; then rm -f -- source || :; entered=0; fi; cd /; "
        + "if [ -n \"$work\" ]; then rmdir -- \"$work\" 2>/dev/null || :; work=; fi; }; "
        + "case \"$mode\" in [0-7]|[0-7][0-7]|[0-7][0-7][0-7]|[0-7][0-7][0-7][0-7]) ;; "
        + "*) refuse " + RefusalReasonInvalid + " 'invalid mode.';; esac; "
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + "name=${target##*/}; case \"$name\" in ''|.|..) refuse " + RefusalReasonInvalid + " 'no entry name.';; esac; "
        + "me=$(id -u) || refuse " + RefusalReasonUnverifiable + " 'the account could not be identified.'; "
        + HeldParentWalk
        + "parent=$(stat -c %d:%i .) || refuse " + RefusalReasonChanged + " 'the parent directory changed during the check.'; "
        + "pdev=$(stat -c %d .) || refuse " + RefusalReasonChanged + " 'the parent directory changed during the check.'; "
        + "if [ -L \"./$name\" ]; then refuse " + RefusalReasonLink + " 'symbolic link.'; fi; "
        + "if [ -d \"./$name\" ]; then "
        + "held=$(stat -c %d:%i -- \"./$name\") || refuse " + RefusalReasonChanged + " 'the directory changed during the check.'; "
        + "cd -P -- \"./$name\" || refuse " + RefusalReasonChanged + " 'the directory changed during the check.'; "
        + "[ \"$(stat -c %d:%i .)\" = \"$held\" ] || refuse " + RefusalReasonChanged + " 'the directory changed during the check.'; "
        + "chmod -- \"$mode\" .; exit 0; fi; "
        + "[ -f \"./$name\" ] || refuse " + RefusalReasonSpecial + " 'not a regular file or directory.'; "
        + "trap cleanup EXIT; trap 'exit 129' HUP INT TERM; "
        + "made=$(mktemp -d -- \"./.heimdall-chmod.XXXXXXXXXX\") || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be created.'; "
        + "candidate=$PWD/${made#./}; "
        + "cd -P -- \"$made\" || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be entered.'; "
        + "case \"$(stat -c %u:%a .)\" in 0:700|0:2700) ;; "
        + "*) refuse " + RefusalReasonUnverifiable + " 'the private directory is not private to root.';; esac; "
        + "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] && [ \"$(stat -c %d .)\" = \"$pdev\" ] "
        + "|| refuse " + RefusalReasonUnverifiable + " 'the private directory could not be verified.'; "
        + "listing=$(ls -A) || refuse " + RefusalReasonUnverifiable + " 'the private directory could not be listed.'; "
        + "[ -z \"$listing\" ] || refuse " + RefusalReasonUnverifiable + " 'the private directory is not empty.'; "
        + "if [ -e source ] || [ -L source ]; then refuse " + RefusalReasonUnverifiable + " 'the private directory is not empty.'; fi; "
        + "work=$candidate; entered=1; "
        + "ln -P -T -- \"../$name\" source || refuse " + RefusalReasonUnverifiable + " 'the file could not be pinned.'; "
        + "if [ -L source ]; then refuse " + RefusalReasonLink + " 'symbolic link.'; fi; "
        + "[ -f source ] || refuse " + RefusalReasonSpecial + " 'not a regular file.'; "
        + HardLinkCheckStart + "source" + HardLinkCheckEnd
        + "chmod -- \"$mode\" source; cleanup; trap - EXIT HUP INT TERM";

    /// <summary>
    /// Builds a root-side streamed write that commits with a same-directory atomic rename.
    /// </summary>
    /// <remarks>
    /// A replaced file takes the old file's owner, mode, timestamps and extended attributes. A new
    /// file is published with mode 0644 where the filesystem allows it, or keeps the folder's
    /// default ACL when it has one (named or base-only); its group is the folder's when the folder is setgid, else
    /// root's. The chmod runs only when neither the work directory nor the payload carries an ACL and the
    /// payload is still mode 600; a failing ls counts as an ACL. The umask 077 at the start is unchanged and
    /// the work directory stays private.
    /// </remarks>
    public static string BuildAtomicWriteBody(string targetRemotePath, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);

        return BuildShellBody($"replace={(overwrite ? 1 : 0)}; " + AtomicWriteScript, targetRemotePath);
    }

    /// <summary>
    /// Builds a root-side read that hard-links without following the source and then reads
    /// through a held descriptor. A size limit, when supplied, is checked on that descriptor.
    /// </summary>
    public static string BuildNoFollowBase64ReadBody(
        string sourceRemotePath,
        long? maximumBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRemotePath);
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        string sizeCheck = maximumBytes.HasValue
            ? "size=$(stat -Lc %s /proc/self/fd/3); "
                + $"if [ \"$size\" -gt {maximumBytes.Value} ]; then "
                + $"printf '%s\\n' \"$size\"; exit {FileTooLargeExitStatus}; fi; "
            : string.Empty;

        return BuildShellBody(
            NoFollowReadPrefix + sizeCheck + NoFollowReadSuffix,
            sourceRemotePath);
    }

    /// <summary>
    /// Builds a root-side permission change that never follows a symbolic link: the entry is
    /// refused when it is a link or a special file, or when it is not the entry that was listed.
    /// </summary>
    /// <param name="targetRemotePath">The entry to change.</param>
    /// <param name="octalMode">One to four octal digits.</param>
    /// <exception cref="ArgumentException">The path is blank or the mode is not octal.</exception>
    public static string BuildNoFollowChmodBody(string targetRemotePath, string octalMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        if (!SftpPermissionMode.TryParseOctal(octalMode, out _))
        {
            throw new ArgumentException(
                "The mode must be one to four octal digits.",
                nameof(octalMode));
        }

        // A "!" in a file name is escaped the way every history-expanding login shell reads it:
        // outside the quotes. The script itself holds none.
        string target = PathEscaper.EscapeForShell(targetRemotePath)
            .Replace("!", "'\\!'", StringComparison.Ordinal);

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(NoFollowChmodScript),
            "sh",
            PathEscaper.EscapeForShell(octalMode),
            target);
    }

    /// <summary>
    /// Builds a root-side delete that reaches the parent folder through <see cref="HeldParentWalk"/>
    /// and removes the entry by its name from that held folder: <c>rm -rf</c> of <c>./name</c>.
    /// </summary>
    /// <param name="remotePath">The absolute path of the entry to delete.</param>
    /// <param name="recursive">
    /// <see langword="true"/> for a folder (<c>rm -rf</c>); <see langword="false"/> for a file or a
    /// link, which is removed with <c>rm -f</c> and refuses, untouched, a folder found at that name.
    /// </param>
    /// <remarks>
    /// The entry name is the last component of the path; a name that is empty, <c>.</c> or
    /// <c>..</c> is refused. The path is data, passed as a quoted positional parameter, so spaces,
    /// a leading dash or a quote in a name change nothing. A refusal exits with
    /// <see cref="NonRegularTargetExitStatus"/> and writes <see cref="EntryRefusedMarker"/> and a
    /// reason (read it with <see cref="ParseEntryRefusalReason"/>); any other failure keeps the
    /// status of the tool that failed.
    /// </remarks>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    public static string BuildHeldDeleteBody(string remotePath, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        // A "!" in a file name is escaped the way every history-expanding login shell reads it:
        // outside the quotes. The script itself holds none.
        string target = PathEscaper.EscapeForShell(remotePath)
            .Replace("!", "'\\!'", StringComparison.Ordinal);

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(HeldDeleteScript + (recursive ? HeldDeleteFolderTail : HeldDeleteFileTail)),
            "sh",
            target);
    }

    // The start of the script that creates one folder: no umask change, so the folder gets the
    // mode the server's own default gives it. An existing entry at that name, a link included,
    // ends the script with the "exists" status before mkdir runs.
    private const string HeldMkdirScript =
        "set -eu; target=$1; "
        + HeldEntryPrologue
        + "if [ -e \"./$name\" ] || [ -L \"./$name\" ]; then printf '%s\\n' "
        + "'Refusing: an entry with that name already exists.' >&2; exit " + DestinationExistsExitStatusText + "; fi; "
        + "mkdir -- \"./$name\" || { if [ -e \"./$name\" ] || [ -L \"./$name\" ]; then printf '%s\\n' "
        + "'Refusing: an entry with that name already exists.' >&2; exit " + DestinationExistsExitStatusText + "; fi; exit 1; }";

    // The start of the script that renames one entry inside the parent folder it holds: the new
    // name is a plain name, never a path, and differs from the current one (one case: the last
    // pattern is the current name, quoted so it matches literally).
    private const string HeldRenameScript =
        "set -eu; umask 077; target=$1; to=$2; "
        + HeldEntryPrologue
        + "case \"$to\" in ''|.|..|*/*|\"$name\") refuse " + RefusalReasonInvalid + " 'no new name.';; esac; ";

    // Replace: the destination name is taken over by the entry.
    private const string HeldRenameReplaceTail = "mv -T -- \"./$name\" \"./$to\"";

    // No replace: nothing is overwritten. When both names are still present afterwards the move
    // did not happen because the destination was taken, which the exit status 78 tells the caller.
    private const string HeldRenameKeepTail =
        "rc=0; mv -nT -- \"./$name\" \"./$to\" || rc=$?; "
        + "if { [ -e \"./$name\" ] || [ -L \"./$name\" ]; } && { [ -e \"./$to\" ] || [ -L \"./$to\" ]; }; then "
        + "printf '%s\\n' 'Refusing: an entry with the new name already exists.' >&2; exit "
        + DestinationExistsExitStatusText + "; fi; exit \"$rc\"";

    /// <summary>
    /// Builds a root-side folder creation that reaches the parent folder through
    /// <see cref="HeldParentWalk"/> and creates the folder by its name from that held folder.
    /// </summary>
    /// <param name="remotePath">The absolute path of the folder to create.</param>
    /// <remarks>
    /// The name is the last component of the path; a name that is empty, <c>.</c> or <c>..</c> is
    /// refused. An entry already at that name (a link included) ends the script with
    /// <see cref="DestinationExistsExitStatus"/>. A missing parent folder is refused with
    /// <see cref="NonRegularTargetExitStatus"/> like any other refusal of the walk.
    /// </remarks>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    public static string BuildHeldMkdirBody(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(HeldMkdirScript),
            "sh",
            EscapeHeldArgument(remotePath));
    }

    /// <summary>
    /// Builds a root-side rename of one entry inside its own folder, reached through
    /// <see cref="HeldParentWalk"/>: <c>mv</c> of <c>./name</c> to <c>./newname</c> from the held folder.
    /// </summary>
    /// <param name="remotePath">The absolute path of the entry to rename.</param>
    /// <param name="newName">The new name: one component, not a path.</param>
    /// <param name="replace">
    /// <see langword="true"/> to take over an existing destination; <see langword="false"/> to leave
    /// both entries untouched and exit with <see cref="DestinationExistsExitStatus"/> when the
    /// destination exists.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The path is blank, or the new name is empty, <c>.</c>, <c>..</c>, holds a <c>/</c>, or equals
    /// the current name.
    /// </exception>
    public static string BuildHeldRenameBody(string remotePath, string newName, bool replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (newName is "." or ".." || newName.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("The new name must be a single path component.", nameof(newName));
        }

        string currentName = remotePath[(remotePath.LastIndexOf('/') + 1)..];
        if (string.Equals(currentName, newName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The new name must differ from the current name.", nameof(newName));
        }

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(HeldRenameScript + (replace ? HeldRenameReplaceTail : HeldRenameKeepTail)),
            "sh",
            EscapeHeldArgument(remotePath),
            EscapeHeldArgument(newName));
    }

    // A "!" in a file name is escaped the way every history-expanding login shell reads it:
    // outside the quotes. The scripts themselves hold none.
    private static string EscapeHeldArgument(string value) =>
        PathEscaper.EscapeForShell(value).Replace("!", "'\\!'", StringComparison.Ordinal);

    /// <summary>
    /// Reads why a write or a read was refused for its path or its file, when the script said so.
    /// </summary>
    /// <param name="stderr">What the script wrote on standard error.</param>
    /// <param name="exitStatus">The script's exit status: 73 for a write, 74 for a read.</param>
    /// <returns>
    /// <see cref="EntryRefusalReason.PathLink"/> or <see cref="EntryRefusalReason.HardLink"/> when
    /// the status is 73 or 74 and a line of <paramref name="stderr"/> is the marker followed by one
    /// of the two reasons; otherwise <see langword="null"/>.
    /// </returns>
    public static EntryRefusalReason? ParseTransferRefusal(string? stderr, int exitStatus)
    {
        if (string.IsNullOrEmpty(stderr)
            || (exitStatus != NonRegularTargetExitStatus && exitStatus != NonRegularSourceExitStatus))
        {
            return null;
        }

        Match match = TransferRefusalLine.Match(stderr);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value switch
        {
            RefusalReasonPathLink => EntryRefusalReason.PathLink,
            RefusalReasonHardLink => EntryRefusalReason.HardLink,
            _ => null,
        };
    }

    /// <summary>
    /// Reads why the permission change script refused an entry, from the token after the marker.
    /// A missing or unknown token is read as the cautious answer: the safe method was not possible.
    /// </summary>
    public static EntryRefusalReason ParseEntryRefusalReason(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return EntryRefusalReason.Unverifiable;
        }

        int marker = IndexOfLineStart(stderr, EntryRefusedMarker);
        if (marker < 0)
        {
            return EntryRefusalReason.Unverifiable;
        }

        string rest = stderr[(marker + EntryRefusedMarker.Length)..].TrimStart(' ');
        int end = rest.IndexOfAny([' ', '\r', '\n']);
        string token = end < 0 ? rest : rest[..end];
        return token switch
        {
            RefusalReasonLink => EntryRefusalReason.Link,
            RefusalReasonSpecial => EntryRefusalReason.Special,
            RefusalReasonChanged => EntryRefusalReason.Changed,
            RefusalReasonInvalid => EntryRefusalReason.Invalid,
            RefusalReasonPathLink => EntryRefusalReason.PathLink,
            RefusalReasonHardLink => EntryRefusalReason.HardLink,
            _ => EntryRefusalReason.Unverifiable,
        };
    }

    /// <summary>
    /// Whether <paramref name="stderr"/> holds <see cref="EntryRefusedMarker"/> at the start of a
    /// line; the same text in the middle of a line (an echoed file name) does not count.
    /// </summary>
    public static bool HasEntryRefusalMarker(string? stderr)
        => !string.IsNullOrEmpty(stderr) && IndexOfLineStart(stderr, EntryRefusedMarker) >= 0;

    // The marker counts only at the start of a line: a file name that holds the same text, echoed
    // in the middle of a line of a message, is not a refusal.
    private static int IndexOfLineStart(string text, string value)
    {
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            if (index == 0 || text[index - 1] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The program every sudo started by the SFTP browser begins with, so sudo's own messages
    /// ("a password is required", "sorry, try again", "not in the sudoers") are English whatever
    /// the login locale of the server, and the classifiers that read them keep working.
    /// </summary>
    /// <remarks>
    /// <c>env</c> and not <c>LC_ALL=C sudo</c>: a csh or tcsh login shell does not parse a
    /// leading assignment. Before <c>sudo</c> and not after: <c>sudo LC_ALL=C</c> needs the
    /// SETENV tag. <c>LC_ALL=C</c> also makes gettext ignore <c>LANGUAGE</c>. The staged outer
    /// script already exports <c>LC_ALL=C</c> before its own sudo calls and does not use this.
    /// A missing sudo then reports <c>env: 'sudo': No such file or directory</c> with status 127,
    /// which <see cref="PrivilegedFileTransfer.IsSudoMissing"/> still recognizes.
    /// </remarks>
    public const string SudoProgram = "env LC_ALL=C sudo";

    internal static string BuildPasswordSudoInvocation(string privilegedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        return $"{SudoProgram} -S -p '' {privilegedBody}";
    }

    internal static string BuildNonInteractiveSudoInvocation(string privilegedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        return $"{SudoProgram} -n {privilegedBody}";
    }

    /// <summary>
    /// The script the connecting user runs, as a constant. Its positional parameters are the
    /// installer script (<c>$1</c>), the mode (<c>$2</c>), the replace flag (<c>$3</c>), the
    /// target (<c>$4</c>) and the number of bytes the content must have (<c>$5</c>).
    /// </summary>
    /// <remarks>
    /// The content is first staged in a private directory of the connecting user, so no sudo ever
    /// shares standard input with the payload: sudo's input is the single password line and then
    /// its end, a wrong password meets the end after one attempt, and the password can neither be
    /// written to the file nor taken from it. The install is first tried without any password and
    /// with no input: when sudo needs none, the password never reaches sudo's input. Only when the
    /// installer did not start is the password offered, once. A staged file that is not exactly the
    /// expected size is refused before any sudo, so a truncated read is never installed. The script
    /// holds no <c>!</c> (history expansion in some login shells) and no newline. The staged
    /// directory is removed by this script's own trap, never by the root-side installer.
    /// </remarks>
    internal const string StagedWriteOuterScript =
        "umask 077; LC_ALL=C; export LC_ALL; "
        + "inner=$(printf '%s' \"$1\" | tr '^' '\\041') || exit 74; "
        + "mode=$2; replace=$3; target=$4; size=$5; p=; "
        + "if [ \"$mode\" = password ]; then IFS= read -r p; fi; "
        + "d=$(mktemp -d \"${TMPDIR:-/tmp}/.heimdall-stage.XXXXXXXXXX\") || exit 74; "
        + "trap 'rm -rf -- \"$d\"' EXIT; trap 'exit 129' HUP INT TERM; "
        + "cat > \"$d/f\" || exit 74; "
        + "[ $(wc -c < \"$d/f\") -eq \"$size\" ] || exit 74; "
        + "uid=$(id -u) || exit 74; "
        + "sudo -n sh -c \"$inner\" sh \"$replace\" \"$target\" \"$d/f\" \"$uid\" "
        + "</dev/null 2> \"$d/e\"; rc=$?; "
        + "case \"$(cat \"$d/e\")\" in *" + InstallStartedMarker + "*) "
        + "cat \"$d/e\" >&2; p=; unset p; exit $rc ;; esac; "
        + "if [ \"$mode\" = password ]; then "
        + "printf '%s\\n' \"$p\" | sudo -S -p '' sh -c \"$inner\" sh \"$replace\" \"$target\" \"$d/f\" \"$uid\"; rc=$?; "
        + "else cat \"$d/e\" >&2; fi; "
        + "p=; unset p; exit $rc";

    /// <summary>
    /// The script root runs: it announces itself, checks the staged file on the descriptor it
    /// opened, then runs the atomic writer unchanged with that descriptor as standard input.
    /// Positional parameters: replace flag, target, staged file, the caller's uid.
    /// </summary>
    /// <remarks>
    /// The checks use GNU <c>stat</c> and <c>/proc/self/fd</c>, the same platform the writer and the
    /// no-follow read already assume. A check that cannot be performed refuses with 73 rather than
    /// proceed.
    /// </remarks>
    internal const string StagedInstallScript =
        "printf '%s\\n' '" + InstallStartedMarker + "' >&2; "
        + AbsolutePathOnly
        + "replace=$1; target=$2; staged=$3; uid=$4; "
        + ToolingProbe
        + "case \"$staged\" in */*) ;; *) "
        + "printf '%s\\n' 'Refusing: the staged file has no directory.' >&2; exit 73;; esac; "
        + "sd=${staged%/*}; "
        + "if [ -L \"$staged\" ] || [ -L \"$sd\" ]; then "
        + "printf '%s\\n' 'Refusing symbolic-link staged file.' >&2; exit 73; fi; "
        + "dinfo=$(stat -c '%u %a' -- \"$sd\" 2>/dev/null) || dinfo=; "
        + "set -- $dinfo; du=$1; dm=$2; "
        + "if [ -n \"$dm\" ] && [ \"$du\" = \"$uid\" ] && [ $((0$dm & 022)) -eq 0 ]; then :; else "
        + "printf '%s\\n' 'Refusing: the staging directory is not private to the caller.' >&2; exit 73; fi; "
        + "exec 3< \"$staged\"; "
        + "info=$(stat -Lc %u:%h /proc/self/fd/3 2>/dev/null) || info=; "
        + "if [ -f /proc/self/fd/3 ] && [ \"$info\" = \"$uid:1\" ]; then :; else "
        + "printf '%s\\n' 'Refusing: the staged file is not a regular single-link file of the caller, or could not be checked.' >&2; "
        + "exit 73; fi; "
        + "exec 0<&3 3<&-; set -- \"$target\"; "
        + AtomicWriteScript;

    /// <summary>
    /// Builds the one simple command that stages the content as the connecting user and installs it
    /// with sudo. Every variable value is a quoted positional parameter.
    /// </summary>
    internal static string BuildStagedWriteCommand(
        string targetRemotePath,
        bool overwrite,
        bool passwordMode,
        long expectedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBytes);

        // The installer holds "!" (tests and comparisons), which a history-expanding login shell
        // would act on even inside quotes. It travels as a placeholder the outer script restores.
        string installer = StagedInstallScript.Replace("!", BangPlaceholder, StringComparison.Ordinal);

        // A "!" in a file name is escaped the way every such shell reads it: outside the quotes.
        string target = PathEscaper.EscapeForShell(targetRemotePath)
            .Replace("!", "'\\!'", StringComparison.Ordinal);

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(StagedWriteOuterScript),
            "sh",
            PathEscaper.EscapeForShell(installer),
            PathEscaper.EscapeForShell(passwordMode ? PasswordMode : NoPasswordMode),
            PathEscaper.EscapeForShell(overwrite ? "1" : "0"),
            target,
            PathEscaper.EscapeForShell(expectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string BuildShellBody(string script, string remotePath)
    {
        string escapedScript = PathEscaper.EscapeForShell(script);
        string escapedPath = PathEscaper.EscapeForShell(remotePath);
        return $"sh -c {escapedScript} sh {escapedPath}";
    }
}

/// <summary>
/// Immutable result of a completed streamed privileged command.
/// </summary>
public readonly record struct PrivilegedCommandResult(
    int ExitStatus,
    string Result,
    string Error);

/// <summary>
/// Streams command input over an SSH exec channel without an unprivileged remote file.
/// </summary>
public static class PrivilegedFileTransfer
{
    /// <summary>
    /// The bound of a privileged exec command whose duration is not set by a payload.
    /// </summary>
    /// <remarks>
    /// SSH.NET leaves <c>SshCommand.CommandTimeout</c> infinite, so a server that accepts the exec
    /// channel and then never answers holds the operation open with nothing left to end it. Ten
    /// minutes, matching the server-side copy bound: long enough for a recursive privileged
    /// operation over a large tree, short enough that an unproductive channel gives up by itself.
    /// The SFTP pane's sudo control commands use this same value, so the two cannot drift apart.
    /// </remarks>
    public static readonly TimeSpan ControlCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The slowest sustained rate a privileged write is still expected to complete at.
    /// </summary>
    /// <remarks>
    /// SSH.NET's command bound is wall-clock over the whole command, not an inactivity timer, so a
    /// fixed bound on a write would abort a large legitimate one. The write's bound grows by the
    /// time its payload takes at this rate: 32 KiB/s, well below any usable link, so it never
    /// aborts a transfer that is making progress, while a wedged server is still given up on.
    /// </remarks>
    internal const long MinimumTransferBytesPerSecond = 32 * 1024;

    /// <summary>
    /// Returns the bound for a privileged command carrying <paramref name="payloadBytes"/>, or the
    /// control bound when the payload is unknown.
    /// </summary>
    internal static TimeSpan TransferCommandTimeout(long? payloadBytes)
    {
        if (payloadBytes is not { } bytes)
        {
            return ControlCommandTimeout;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bytes, nameof(payloadBytes));
        return ControlCommandTimeout + TimeSpan.FromSeconds((double)bytes / MinimumTransferBytesPerSecond);
    }

    /// <summary>
    /// Connects a client for a privileged command with a cancellation that reaches the handshake.
    /// </summary>
    /// <remarks>
    /// The application's privileged SFTP connect used <c>Task.Run(() =&gt; client.Connect())</c>,
    /// whose token is checked once before the call and never again. The SSH assembly grants its
    /// cancellable connect to this one, not to the application, so the application reaches it here.
    /// </remarks>
    public static Task ConnectAsync(SshClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Heimdall.Ssh.SshConnectionFactory.ConnectWithCancellationAsync(client, ct);
    }

    /// <summary>
    /// Executes a privileged command whose body does not consume standard input.
    /// </summary>
    public static async Task<PrivilegedCommandResult> ExecuteCommandAsync(
        SshClient client,
        string privilegedBody,
        string? sudoPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);

        if (string.IsNullOrEmpty(sudoPassword))
        {
            using var emptyInput = new MemoryStream();
            return await ExecuteWithInputAsync(
                    client,
                    PrivilegedFileCommands.BuildNonInteractiveSudoInvocation(privilegedBody),
                    emptyInput,
                    ReadOnlyMemory<byte>.Empty,
                    ControlCommandTimeout,
                    ct)
                .ConfigureAwait(false);
        }

        return await ExecuteWithPasswordAsync(
                client,
                PrivilegedFileCommands.BuildPasswordSudoInvocation(privilegedBody),
                sudoPassword,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="targetRemotePath"/> as root. The content
    /// is first staged in a private directory of the connecting user, then installed by one sudo
    /// call that shares no input with the payload, so a password can never reach the file and a
    /// payload line can never be read as a password.
    /// </summary>
    public static async Task<PrivilegedCommandResult> ExecuteAtomicWriteAsync(
        SshClient client,
        string targetRemotePath,
        bool overwrite,
        Stream content,
        string? sudoPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        // The remote side refuses a staged file of any other size, so a read that stops early can
        // never be installed. That needs the length up front: a stream that cannot report it is
        // refused rather than buffered. Both callers pass a file stream.
        if (!content.CanSeek)
        {
            throw new ArgumentException("The content stream must report its length.", nameof(content));
        }

        long expectedBytes = Math.Max(0, content.Length - content.Position);

        // The payload sets how long a legitimate write takes, so it sets the bound too.
        TimeSpan commandTimeout = TransferCommandTimeout(expectedBytes);

        bool passwordMode = !string.IsNullOrEmpty(sudoPassword);
        string commandText = PrivilegedFileCommands.BuildStagedWriteCommand(
            targetRemotePath,
            overwrite,
            passwordMode,
            expectedBytes);
        if (!passwordMode)
        {
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    content,
                    ReadOnlyMemory<byte>.Empty,
                    commandTimeout,
                    ct,
                    remoteMayExplainWriteFailure: true)
                .ConfigureAwait(false);
        }

        byte[] passwordBytes = EncodePasswordLine(sudoPassword!);
        try
        {
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    content,
                    passwordBytes,
                    commandTimeout,
                    ct,
                    remoteMayExplainWriteFailure: true)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Names what a failed privileged write failed at, from its exit status and standard error.
    /// </summary>
    public static PrivilegedWriteFailureKind ClassifyWrite(PrivilegedCommandResult result)
    {
        return ClassifyWrite(result.ExitStatus, result.Error);
    }

    internal static PrivilegedWriteFailureKind ClassifyWrite(int exitStatus, string? stderr)
    {
        if (exitStatus == 0)
        {
            return PrivilegedWriteFailureKind.None;
        }

        // The installer's own start line decides first: whatever it exits with is its own status.
        string error = stderr ?? string.Empty;
        if (error.Contains(PrivilegedFileCommands.InstallStartedMarker, StringComparison.Ordinal))
        {
            return PrivilegedWriteFailureKind.InstallFailed;
        }

        if (exitStatus == PrivilegedFileCommands.StagingFailedExitStatus)
        {
            return PrivilegedWriteFailureKind.StagingFailed;
        }

        if (exitStatus == PrivilegedFileCommands.CommandNotFoundExitStatus)
        {
            return PrivilegedWriteFailureKind.SudoMissing;
        }

        if (ContainsAny(error, NotPermittedMessages))
        {
            return PrivilegedWriteFailureKind.NotPermitted;
        }

        if (ContainsAny(error, PasswordRejectedMessages))
        {
            return PrivilegedWriteFailureKind.PasswordRejected;
        }

        if (ContainsAny(error, PasswordUnavailableMessages))
        {
            return PrivilegedWriteFailureKind.PasswordUnavailable;
        }

        return PrivilegedWriteFailureKind.SudoFailed;
    }

    /// <summary>
    /// Standard error without the installer's start line, fit to show or log.
    /// </summary>
    public static string CleanWriteError(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        return stderr
            .Replace(PrivilegedFileCommands.InstallStartedMarker, string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Throws for a failure of the stages before the installer ran: staging, sudo's own refusal,
    /// a missing sudo. A failure of the installer itself, or success, returns so the caller applies
    /// the installer's own statuses.
    /// </summary>
    /// <exception cref="PrivilegedWriteFailedException">A named stage failure.</exception>
    /// <exception cref="InvalidOperationException">An unnamed sudo stage failure.</exception>
    public static void EnsureStageSucceeded(PrivilegedCommandResult result)
    {
        PrivilegedWriteFailureKind kind = ClassifyWrite(result);
        string error = CleanWriteError(result.Error);
        switch (kind)
        {
            case PrivilegedWriteFailureKind.None:
            case PrivilegedWriteFailureKind.InstallFailed:
                return;
            case PrivilegedWriteFailureKind.SudoFailed:
                throw new InvalidOperationException(
                    $"sudo atomic write failed (exit {result.ExitStatus}): {error}");
            default:
                throw new PrivilegedWriteFailedException(kind, error);
        }
    }

    /// <summary>Whether sudo's standard error says the account may not use sudo.</summary>
    public static bool IsSudoNotPermittedMessage(string? stderr)
    {
        return !string.IsNullOrEmpty(stderr) && ContainsAny(stderr, NotPermittedMessages);
    }

    /// <summary>Whether sudo's standard error says the password was refused or never supplied.</summary>
    public static bool IsPasswordRejectedMessage(string? stderr)
    {
        return !string.IsNullOrEmpty(stderr) && ContainsAny(stderr, PasswordRejectedMessages);
    }

    /// <summary>Whether sudo's standard error says no password prompt could be answered.</summary>
    public static bool IsPasswordUnavailableMessage(string? stderr)
    {
        return !string.IsNullOrEmpty(stderr) && ContainsAny(stderr, PasswordUnavailableMessages);
    }

    /// <summary>
    /// Whether a command ended with "command not found" for sudo itself: the status alone would
    /// also name any other missing command.
    /// </summary>
    public static bool IsSudoMissing(int exitStatus, string? stderr)
    {
        return exitStatus == PrivilegedFileCommands.CommandNotFoundExitStatus
            && !string.IsNullOrEmpty(stderr)
            && stderr.Contains(SudoCommandName, StringComparison.OrdinalIgnoreCase);
    }

    private const string SudoCommandName = "sudo";

    private static readonly string[] PasswordRejectedMessages =
    [
        "incorrect password attempt",
        "sorry, try again",
        "no password was provided",
        // sudo-rs
        "authentication failed, try again",
        "incorrect authentication attempt",
        "authentication required but not attempted",
    ];

    private static readonly string[] PasswordUnavailableMessages =
    [
        "a terminal is required",
        "no tty present",
        "no askpass",
        "a password is required",
        "you must have a tty",
        // sudo-rs
        "interactive authentication is required",
        "interaction is required",
    ];

    private static readonly string[] NotPermittedMessages =
    [
        "not in the sudoers",
        "may not run sudo",
        "is not allowed to execute",
        "is not allowed to run sudo",
        // sudo-rs
        "i'm afraid i can't do that",
        "is not allowed to authenticate as",
    ];

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithPasswordAsync(
        SshClient client,
        string commandText,
        string password,
        CancellationToken ct)
    {
        byte[] passwordBytes = EncodePasswordLine(password);
        try
        {
            using var emptyInput = new MemoryStream();
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    emptyInput,
                    passwordBytes,
                    ControlCommandTimeout,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Encodes the single line the streaming sudo line reads before anything else: the password
    /// and a line feed. A password holding a line feed would end the line early and leave the rest
    /// at the head of the payload, so it is refused here rather than sent.
    /// </summary>
    internal static byte[] EncodePasswordLine(string sudoPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(sudoPassword);
        if (sudoPassword.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The sudo password must be a single line.",
                nameof(sudoPassword));
        }

        return Encoding.UTF8.GetBytes(sudoPassword + "\n");
    }

    /// <summary>
    /// Writes the prefix, then exactly the payload, to the remote command's standard input.
    /// </summary>
    internal static async Task WriteInputAsync(
        Stream commandInput,
        Stream payload,
        ReadOnlyMemory<byte> prefix,
        CancellationToken ct)
    {
        if (!prefix.IsEmpty)
        {
            await commandInput.WriteAsync(prefix, ct).ConfigureAwait(false);
        }

        byte[] buffer = new byte[CopyBufferBytes];
        while (true)
        {
            int read;
            try
            {
                read = await payload.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Told apart from a failed write: the remote side then refuses the short file, and
                // its status must not be allowed to hide that the local read is what failed.
                throw new LocalPayloadReadException(ex);
            }

            if (read == 0)
            {
                break;
            }

            await commandInput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await commandInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private const int CopyBufferBytes = 81920;

    private sealed class LocalPayloadReadException(Exception inner) : IOException(inner.Message, inner);

    private static void Rethrow(Exception failure)
    {
        Exception original = failure is LocalPayloadReadException { InnerException: { } inner } ? inner : failure;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
    }

    /// <summary>
    /// Writes the input and returns the failure instead of throwing it. Cancellation still throws.
    /// </summary>
    internal static async Task<Exception?> TryWriteInputAsync(
        Stream commandInput,
        Stream payload,
        ReadOnlyMemory<byte> prefix,
        CancellationToken ct)
    {
        try
        {
            await WriteInputAsync(commandInput, payload, prefix, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex;
        }
    }

    /// <summary>
    /// Raises a deferred local write failure unless the remote command ended in one of the stages
    /// before the installer (staging, sudo), whose status and message explain it better than a
    /// closed channel does. After the installer started, or on success, the write failure stands.
    /// </summary>
    internal static void RethrowUnlessStageFailed(Exception? writeFailure, int exitStatus, string? stderr)
    {
        if (writeFailure is null)
        {
            return;
        }

        // A failed local read is the cause whatever the remote side then says about the short
        // file, and no exit status read means nothing explains the write failure.
        if (writeFailure is LocalPayloadReadException || exitStatus < 0)
        {
            Rethrow(writeFailure);
        }

        PrivilegedWriteFailureKind kind = ClassifyWrite(exitStatus, stderr);
        if (kind is not (PrivilegedWriteFailureKind.None or PrivilegedWriteFailureKind.InstallFailed))
        {
            return;
        }

        Rethrow(writeFailure);
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithInputAsync(
        SshClient client,
        string commandText,
        Stream input,
        ReadOnlyMemory<byte> prefix,
        TimeSpan commandTimeout,
        CancellationToken ct,
        bool remoteMayExplainWriteFailure = false)
    {
        using SshCommand command = client.CreateCommand(commandText);
        command.CommandTimeout = commandTimeout;
        Task? executeTask = null;
        try
        {
            executeTask = command.ExecuteAsync(ct);
            // A refused password makes the remote side exit before it reads the payload, so a large
            // write can fail on a closed channel first. The failure waits for the exit status.
            Exception? writeFailure = null;
            try
            {
                using Stream commandInput = command.CreateInputStream();
                writeFailure = await TryWriteInputAsync(commandInput, input, prefix, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writeFailure ??= ex;
            }

            await executeTask.ConfigureAwait(false);
            if (remoteMayExplainWriteFailure)
            {
                RethrowUnlessStageFailed(writeFailure, command.ExitStatus ?? -1, command.Error);
            }
            else if (writeFailure is not null)
            {
                Rethrow(writeFailure);
            }

            return new PrivilegedCommandResult(
                command.ExitStatus ?? -1,
                command.Result ?? string.Empty,
                command.Error ?? string.Empty);
        }
        catch
        {
            if (executeTask is not null)
            {
                try
                {
                    await executeTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original stream or cancellation exception.
                }
            }

            throw;
        }
    }
}

/// <summary>
/// Why the root-side permission change refused an entry.
/// </summary>
public enum EntryRefusalReason
{
    /// <summary>The server cannot do the safe method here, or the check could not be completed.</summary>
    Unverifiable,

    /// <summary>The entry is a symbolic link.</summary>
    Link,

    /// <summary>The entry is neither a regular file nor a directory.</summary>
    Special,

    /// <summary>The entry is no longer the one that was listed.</summary>
    Changed,

    /// <summary>The request was not valid: the mode, or no entry name.</summary>
    Invalid,

    /// <summary>The path goes through a symbolic link that is not trusted.</summary>
    PathLink,

    /// <summary>The file has other hard links and sits in a folder that others can write to.</summary>
    HardLink,
}

/// <summary>
/// What a failed privileged write failed at.
/// </summary>
public enum PrivilegedWriteFailureKind
{
    /// <summary>The write succeeded.</summary>
    None,

    /// <summary>The content could not be staged in the server's temporary directory.</summary>
    StagingFailed,

    /// <summary>sudo refused the password.</summary>
    PasswordRejected,

    /// <summary>sudo needs a password or a terminal it was not given.</summary>
    PasswordUnavailable,

    /// <summary>The account is not allowed to use sudo.</summary>
    NotPermitted,

    /// <summary>The server has no sudo to run.</summary>
    SudoMissing,

    /// <summary>The stage failed in a way none of the named kinds recognise.</summary>
    SudoFailed,

    /// <summary>The installer started; its own exit statuses apply.</summary>
    InstallFailed,
}

/// <summary>
/// A privileged write failed before the installer ran, at a stage the caller can name.
/// </summary>
public sealed class PrivilegedWriteFailedException : InvalidOperationException
{
    public PrivilegedWriteFailedException(PrivilegedWriteFailureKind kind, string remoteError)
        : base(string.IsNullOrWhiteSpace(remoteError)
            ? $"The privileged write failed: {kind}."
            : remoteError)
    {
        Kind = kind;
        RemoteError = remoteError ?? string.Empty;
    }

    /// <summary>The stage that failed.</summary>
    public PrivilegedWriteFailureKind Kind { get; }

    /// <summary>What the remote side wrote on standard error, without the installer's start line.</summary>
    public string RemoteError { get; }
}
