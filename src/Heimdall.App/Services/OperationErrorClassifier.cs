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
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.Services;

/// <summary>
/// Pure, standalone classifier that maps a transfer-operation exception to a stable, language-neutral
/// error category string for the operations log. Lives in the sink layer and references the exception
/// types directly so it never depends on the view-model's permission-denied predicate.
/// </summary>
/// <remarks>
/// Cancellation (<see cref="OperationCanceledException"/>) is intentionally NOT an error category: a
/// cancelled operation is recorded with <see cref="SessionOperationResult.Cancelled"/> by the caller,
/// not classified here.
/// </remarks>
public static class OperationErrorClassifier
{
    /// <summary>"permission" — a permission-denied failure (remote SFTP or local filesystem).</summary>
    public const string Permission = "permission";

    /// <summary>"security" — a host-key rejection / MITM signal.</summary>
    public const string Security = "security";

    /// <summary>"io" — a general input/output failure.</summary>
    public const string Io = "io";

    /// <summary>"other" — any failure not matched by a more specific category.</summary>
    public const string Other = "other";

    /// <summary>
    /// Classifies <paramref name="exception"/> into one of the stable category constants.
    /// </summary>
    /// <param name="exception">The exception thrown by a failed transfer operation.</param>
    /// <returns>A lowercase, language-neutral error category.</returns>
    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SftpPermissionDeniedException => Permission,
            UnauthorizedAccessException => Permission,
            HostKeyRejectedException => Security,
            IOException => Io,
            _ => Other,
        };
    }
}
