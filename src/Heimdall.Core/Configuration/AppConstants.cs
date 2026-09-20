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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Application-wide constants that are not protocol-specific.
/// </summary>
public static class AppConstants
{
    /// <summary>Application directory name under the current user's local application data.</summary>
    public const string ApplicationFolderName = "Heimdall";

    /// <summary>Install-directory subfolder containing bundled configuration templates.</summary>
    public const string BundledConfigDirectoryName = "config";

    /// <summary>Writable diagnostic and connection-history subfolder.</summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>Writable terminal-macro subfolder.</summary>
    public const string MacrosDirectoryName = "macros";

    /// <summary>Writable network-scan history subfolder.</summary>
    public const string NetworkScansDirectoryName = "network-scans";

    /// <summary>Restrictive staging root for verified application updates.</summary>
    public const string UpdatesDirectoryName = "updates";

    /// <summary>Writable notes subfolder within the legacy relative config path.</summary>
    public const string NotesDirectoryName = "notes";

    /// <summary>Maximum import file size for Command Library JSON imports.</summary>
    public const long MaxImportFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>Maximum size of a single Command Library seed JSON file read at first launch.</summary>
    public const long MaxSeedFileSizeBytes = 100 * 1024;

    /// <summary>Maximum accepted length of an imported action Title.</summary>
    /// <summary>
    /// How long a command-history entry is kept, in days.
    /// </summary>
    /// <remarks>
    /// History holds what the operator actually typed, sealed at rest but still a record
    /// of it, and nothing pruned it before: the table grew for the life of the install.
    /// A bounded window is part of recording real commands rather than a separate tidiness
    /// concern. Ninety days is the value the cleanup routine has always defaulted to, kept
    /// so that wiring it changes when rows disappear and not by how much.
    /// </remarks>
    public const int CommandHistoryRetentionDays = 90;

    public const int MaxImportActionTitleLength = 200;

    /// <summary>Maximum accepted length of an imported action Category.</summary>
    public const int MaxImportActionCategoryLength = 100;

    /// <summary>Legacy application folder name used for migration detection.</summary>
    public const string LegacyAppFolderName = "RDPManager";

    /// <summary>Subdirectory under BaseDirectory for embedded CLI tools.</summary>
    public const string EmbeddedToolsSubdir = "Assets/Tools";

    /// <summary>
    /// Maximum number of bytes a ConPTY session buffers before the first
    /// <c>DataReceived</c> subscriber attaches. This preserves the child shell's
    /// bootstrap output (e.g. the initial prompt/banner) that is otherwise emitted
    /// before the terminal view subscribes. Once the cap is hit the session stops
    /// buffering further bootstrap bytes.
    /// </summary>
    public const int MaxConPtyBootstrapBufferBytes = 256 * 1024;

    /// <summary>
    /// The shell a LOCAL profile runs when it names none.
    /// </summary>
    public const string DefaultLocalShellExecutable = "powershell.exe";

    /// <summary>
    /// The character that submits a line to an interactive terminal: carriage return, which is
    /// what the Enter key sends.
    /// </summary>
    /// <remarks>
    /// <para>Measured 2026-09-20 against a live ConPTY: a command terminated with LF leaves
    /// Windows PowerShell on its "&gt;&gt; " continuation prompt with the line typed and
    /// unexecuted, three runs out of three, while the same command terminated with CR runs and
    /// returns a fresh prompt.</para>
    /// <para>It lives here because more than one surface has to agree on it. Each of them
    /// spelled its own terminator, and they disagreed: the local file browser sent LF through
    /// the command formatter, and the Command Library sent LF through the view's own
    /// WriteCommand. Both were wrong on a ConPTY, and a fix to one would not have reached the
    /// other. The SSH keepalive already sends this same byte, as
    /// <c>EmbeddedSshView.KeepAliveCr</c>.</para>
    /// </remarks>
    public const string TerminalSubmitKey = "\r";

    /// <summary>
    /// Windows PowerShell's path relative to the system directory. The default shell is
    /// resolved through this rather than left as a bare name: CreateProcessW with no
    /// application name searches the application directory first, so a powershell.exe
    /// dropped beside Heimdall.exe would be preferred over the system one.
    /// </summary>
    public const string WindowsPowerShellSystemRelativePath =
        @"WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>
    /// Default maximum size in bytes of a single session log file before rollover (4 MiB).
    /// Chosen to bound per-file disk usage while keeping a whole interactive session readable
    /// in one file for typical workloads; longer sessions spill into ".N.log" continuations.
    /// </summary>
    public const long DefaultSessionLogMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Flush interval in milliseconds for batching session log writes to disk.
    /// 500 ms keeps transcripts near-live without forcing a disk write per terminal output
    /// chunk on the hot read-loop path (mirrors the batched-flush philosophy of FileLogger).
    /// </summary>
    public const int SessionLogFlushIntervalMs = 500;

    /// <summary>
    /// Default maximum size in bytes of the shared graphical-protocol session-event log before
    /// rollover (4 MiB). Mirrors <see cref="DefaultSessionLogMaxBytes"/>: events are low-volume
    /// (two lines per session), so this cap is reached only over very long-lived installs and the
    /// overflow spills into ".N.log" continuations.
    /// </summary>
    public const long DefaultSessionEventLogMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Default maximum size in bytes of the shared SFTP/FTP session-operations log before rollover
    /// (4 MiB). Mirrors <see cref="DefaultSessionEventLogMaxBytes"/>: one line per file operation, so
    /// the cap is reached only over very long-lived installs and the overflow spills into ".N.log"
    /// continuations.
    /// </summary>
    public const long DefaultSessionOperationLogMaxBytes = 4 * 1024 * 1024;
}
