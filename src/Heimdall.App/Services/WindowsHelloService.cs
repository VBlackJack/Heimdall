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

using Heimdall.Core.Logging;
using Windows.Security.Credentials.UI;

namespace Heimdall.App.Services;

/// <summary>
/// Default <see cref="IWindowsHelloService"/> backed by the WinRT
/// <see cref="UserConsentVerifier"/> API. Logs only coarse, value-free outcomes.
/// </summary>
public sealed class WindowsHelloService : IWindowsHelloService
{
    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            UserConsentVerifierAvailability availability =
                await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WindowsHelloService: availability check failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            UserConsentVerificationResult result =
                await UserConsentVerifier.RequestVerificationAsync(message).AsTask(ct);
            bool verified = result == UserConsentVerificationResult.Verified;
            FileLogger.Info(
                $"WindowsHelloService: verification {(verified ? "succeeded" : "was not granted")}");
            return verified;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WindowsHelloService: verification failed: {ex.Message}");
            return false;
        }
    }
}
