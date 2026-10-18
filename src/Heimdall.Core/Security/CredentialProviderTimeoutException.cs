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

namespace Heimdall.Core.Security;

/// <summary>
/// The external credential command ran past its configured timeout and was killed.
/// </summary>
/// <remarks>
/// Distinct from <see cref="OperationCanceledException"/> on purpose. A timeout is a failure of
/// the vault, which the caller reports or skips; a cancellation is the caller's own decision. As
/// one exception type, a hung vault on a single connect ended the attempt without a word, ended a
/// whole bulk connect at the first slow entry, and escaped the folder menu's "Connect all" as an
/// unhandled exception.
/// </remarks>
public sealed class CredentialProviderTimeoutException : TimeoutException
{
    /// <summary>Initializes the exception with the timeout that elapsed.</summary>
    /// <param name="timeout">The configured command timeout.</param>
    public CredentialProviderTimeoutException(TimeSpan timeout)
        : base($"The credential command did not complete within {(int)timeout.TotalMilliseconds} ms.")
    {
        Timeout = timeout;
    }

    /// <summary>The configured command timeout that elapsed.</summary>
    public TimeSpan Timeout { get; }
}
