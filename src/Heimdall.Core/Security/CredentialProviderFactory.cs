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

using System.Runtime.Versioning;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Security;

/// <summary>
/// Default <see cref="ICredentialProviderFactory"/> that builds the configured
/// <see cref="ICredentialProvider"/> from settings. Owns the unlock-secret decryption
/// for the command provider so callers never touch <see cref="CredentialProtector"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialProviderFactory : ICredentialProviderFactory
{
    /// <inheritdoc />
    public ICredentialProvider Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.CredentialProviderType switch
        {
            CredentialProviderKind.WindowsCredentialManager => new WindowsCredentialManagerProvider(),
            _ => CreateCommandProvider(settings)
        };
    }

    private static ICredentialProvider CreateCommandProvider(AppSettings settings)
    {
        // Decrypt the unlock secret here so the credential value never lives in the
        // view-model. Tolerates null/blank (Unprotect returns null on empty input).
        string? unlockSecret = CredentialProtector.Unprotect(
            settings.CredentialProviderUnlockSecretEncrypted);

        return new CommandCredentialProvider(
            settings.CredentialProviderCommand,
            settings.CredentialProviderDatabase,
            settings.CredentialProviderTimeoutMs,
            unlockSecret,
            settings.CredentialProviderUsernameCommand,
            settings.CredentialProviderFirstLineOnly,
            settings.CredentialProviderKeyFile);
    }
}
