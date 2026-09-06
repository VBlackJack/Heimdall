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

using FluentFTP.Exceptions;
using Renci.SshNet.Common;

namespace Heimdall.Sftp;

/// <summary>
/// Tells a listing that failed because the path does not exist from a listing that failed
/// for any other reason.
/// </summary>
/// <remarks>
/// The upload conflict inventory used to swallow every IOException per planned parent, so a
/// transient failure - a dropped connection, a timeout, a server error - was indistinguishable
/// from "the directory does not exist yet": the inventory came back empty, no conflict was
/// found, no dialog was shown, and every file was sent as a replacement. Only a typed absence
/// is silence; everything else refuses the batch before its first byte.
/// </remarks>
public static class RemotePathAbsence
{
    /// <summary>The FTP reply code for a file or directory that is not available.</summary>
    private const string FtpFileUnavailableCode = "550";

    public static bool IsPathNotFound(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is SftpPathNotFoundException
            || exception is FtpCommandException { CompletionCode: FtpFileUnavailableCode };
    }
}
