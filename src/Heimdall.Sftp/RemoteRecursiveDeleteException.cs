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

/// <summary>Describes why a confined recursive remote deletion was refused or failed.</summary>
public enum RemoteRecursiveDeleteFailureReason
{
    /// <summary>No trusted SSH exec channel could run the deletion.</summary>
    ExecUnavailable,

    /// <summary>The remote shell or its recursive removal command is unavailable.</summary>
    ShellOrRmUnavailable,

    /// <summary>The remote command reported insufficient permissions.</summary>
    PermissionDenied,

    /// <summary>The remote command failed for another reason.</summary>
    CommandFailed,
}

/// <summary>
/// Raised when recursive remote deletion cannot complete through the confined shell path.
/// </summary>
/// <remarks>
/// Messages are deliberately safe for callers and never include remote stderr. Detailed command
/// diagnostics remain confined to the file log.
/// </remarks>
public sealed class RemoteRecursiveDeleteException : Exception
{
    /// <summary>Initializes a typed recursive deletion failure with a safe message.</summary>
    /// <param name="reason">Structured failure reason used by the App layer.</param>
    /// <param name="innerException">Underlying transport failure, when available.</param>
    public RemoteRecursiveDeleteException(
        RemoteRecursiveDeleteFailureReason reason,
        Exception? innerException = null)
        : base(GetSafeMessage(reason), innerException)
    {
        Reason = reason;
    }

    /// <summary>Gets the structured reason for the failure.</summary>
    public RemoteRecursiveDeleteFailureReason Reason { get; }

    private static string GetSafeMessage(RemoteRecursiveDeleteFailureReason reason)
    {
        return reason switch
        {
            RemoteRecursiveDeleteFailureReason.ExecUnavailable =>
                "Recursive remote deletion is unavailable for this session.",
            RemoteRecursiveDeleteFailureReason.ShellOrRmUnavailable =>
                "The remote shell cannot perform recursive deletion.",
            RemoteRecursiveDeleteFailureReason.PermissionDenied =>
                "Permission was denied while deleting the remote directory.",
            RemoteRecursiveDeleteFailureReason.CommandFailed =>
                "The remote recursive deletion command failed.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown failure reason."),
        };
    }
}
