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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Pure decision helper for the startup unlock gate. The blocking unlock gate is
/// shown if, and only if, a master-password vault is configured.
/// </summary>
public static class VaultUnlockGate
{
    /// <summary>
    /// Whether the blocking unlock gate must run before the main window.
    /// </summary>
    /// <param name="settings">The loaded application settings.</param>
    /// <returns><c>true</c> when a vault is configured and the master password is required.</returns>
    public static bool ShouldShowUnlockGate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.VaultEnabled;
    }
}
