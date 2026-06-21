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

namespace Heimdall.App.Services;

/// <summary>
/// Wraps Windows Hello (biometric / PIN) consent verification so the connect-time
/// gate can be unit-tested without invoking the real WinRT API.
/// </summary>
public interface IWindowsHelloService
{
    /// <summary>
    /// Returns true when Windows Hello is configured and ready (a verifier is present
    /// and the user is enrolled).
    /// </summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Prompts the user for Windows Hello verification.
    /// </summary>
    /// <param name="message">Localized reason shown in the consent prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True only when the user was successfully verified.</returns>
    Task<bool> VerifyAsync(string message, CancellationToken ct);
}
