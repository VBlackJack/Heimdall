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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Security;

/// <summary>
/// Builds an <see cref="ICredentialProvider"/> from application settings, centralizing
/// the settings-to-provider construction (command, database, timeout, unlock-secret
/// decryption, and username command) in one injectable, mockable place.
/// </summary>
public interface ICredentialProviderFactory
{
    /// <summary>
    /// Creates a credential provider configured from the supplied settings. The returned
    /// provider's <see cref="ICredentialProvider.IsAvailable"/> reflects whether a command
    /// is configured; callers should still short-circuit on it.
    /// </summary>
    ICredentialProvider Create(AppSettings settings);
}
