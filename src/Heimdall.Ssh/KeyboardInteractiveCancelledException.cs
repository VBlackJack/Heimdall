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

namespace Heimdall.Ssh;

/// <summary>
/// Raised when the user dismisses a keyboard-interactive question instead of answering it.
/// </summary>
/// <remarks>
/// <para>A type of its own because the connect token is NOT cancelled when this happens: the user
/// closed one dialog, not the connection attempt. SSH.NET rethrows whatever the prompt handler
/// raised from its authentication call, so without a distinct type the refusal reached the generic
/// classification as a bare <see cref="OperationCanceledException"/>, which reads as an
/// authentication timeout.</para>
/// <para>Derives from <see cref="OperationCanceledException"/> so every caller that already treats
/// cancellation as "stop, do not retry" keeps doing so.</para>
/// </remarks>
public sealed class KeyboardInteractiveCancelledException : OperationCanceledException
{
    private const string DefaultMessage = "SSH authentication input was cancelled.";

    /// <summary>Creates the exception with its default diagnostic message.</summary>
    public KeyboardInteractiveCancelledException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with a diagnostic message.</summary>
    public KeyboardInteractiveCancelledException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a diagnostic message and its cause.</summary>
    public KeyboardInteractiveCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
