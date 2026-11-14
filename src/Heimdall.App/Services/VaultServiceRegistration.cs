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

using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Security.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>
/// Registers the vault lifecycle together with the App stores it seals and unseals. Kept in one
/// place so a test can prove the lifecycle service receives them.
/// </summary>
internal static class VaultServiceRegistration
{
    /// <summary>
    /// Registers the shared-session and password-preset stores and the lifecycle service. The
    /// shared-session secret store and the configuration manager are registered by the caller; the
    /// command-history store is registered by <see cref="TwinShellBootstrapper"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="presetsDirectory">The directory the password preset file lives in.</param>
    public static void Register(IServiceCollection services, string presetsDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetsDirectory);

        // Stores outside the configuration files whose secrets follow the vault in both directions:
        // sealed when it is enabled or unlocked, returned when it is disabled, which is refused while
        // one still holds a secret sealed with its key.
        services.AddSingleton<IVaultSecretStore>(
            sp => new SharedSessionVaultSecretStore(sp.GetRequiredService<SharedSessionSecretStore>()));
        services.AddSingleton<IVaultSecretStore>(_ => new PasswordPresetVaultSecretStore(presetsDirectory));
        services.AddSingleton<VaultLifecycleService>();
    }
}
