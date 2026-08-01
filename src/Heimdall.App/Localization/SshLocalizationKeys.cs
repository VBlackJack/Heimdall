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

namespace Heimdall.App.Localization;

/// <summary>
/// Compile-time constants for the SSH and tunnel-related localization keys
/// resolved through the i18n service. Centralizing these prevents typo-driven
/// regressions where a missing key silently surfaces as the literal key name
/// in the UI, and allows tooling to "find references" across the app.
/// </summary>
internal static class SshLocalizationKeys
{
    public const string ErrorConnectionFailed = "ErrorConnectionFailed";
    public const string ErrorHostKeyMismatch = "ErrorHostKeyMismatch";
    public const string ErrorHostKeyMismatchDetail = "ErrorHostKeyMismatchDetail";
    public const string ErrorInvalidSshUsername = "ErrorInvalidSshUsername";
    public const string ErrorInvalidTargetHost = "ErrorInvalidTargetHost";
    public const string ErrorInvalidTargetPort = "ErrorInvalidTargetPort";
    public const string ErrorPlinkNotConfigured = "ErrorPlinkNotConfigured";
    public const string ErrorPlinkNotConfiguredWithReason = "ErrorPlinkNotConfiguredWithReason";
    public const string ErrorPlinkOpenSshAgentUnsupported = "ErrorPlinkOpenSshAgentUnsupported";
    public const string ErrorPlinkPassphraseUnsupported = "ErrorPlinkPassphraseUnsupported";
    public const string ErrorPreflightFailed = "ErrorPreflightFailed";
    public const string ErrorPuttyNotConfigured = "ErrorPuttyNotConfigured";
    public const string ErrorSshCancelled = "ErrorSshCancelled";
    public const string ErrorSshHostKeyUnavailable = "ErrorSshHostKeyUnavailable";
    public const string ErrorSshKeyFileNotFound = "ErrorSshKeyFileNotFound";
    public const string ErrorSshKeyPathInvalid = "ErrorSshKeyPathInvalid";
    public const string ErrorSshKeyPathNotAbsolute = "ErrorSshKeyPathNotAbsolute";
    public const string ErrorTunnelFailed = "ErrorTunnelFailed";
    public const string ErrorTunnelNoLoopbackAlias = "ErrorTunnelNoLoopbackAlias";
    public const string ErrorTunnelPortConcurrent = "ErrorTunnelPortConcurrent";
    public const string StatusSshDirectCompressionUnavailable = "StatusSshDirectCompressionUnavailable";
    public const string StatusSshDirectX11AndCompressionUnavailable = "StatusSshDirectX11AndCompressionUnavailable";
    public const string StatusSshDirectX11Unavailable = "StatusSshDirectX11Unavailable";
    public const string StatusSshRetryingViaPlink = "StatusSshRetryingViaPlink";
}
