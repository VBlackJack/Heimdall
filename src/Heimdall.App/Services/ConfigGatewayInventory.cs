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
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// The gateway inventory as the configuration manager holds it, announced on every settings save.
/// </summary>
/// <remarks>
/// <para>One instance serves every tool tab. <see cref="Current"/> reads the published settings on
/// each call rather than keeping a copy, so a load that raised no change event still shows
/// through, and a tool opened from a path that passes no settings of its own, which a split pane
/// does, still lists the gateways.</para>
/// <para>Nothing handed out here aliases the manager's state: <see cref="IConfigManager.SettingsChanged"/>
/// gives each subscriber its own clone, and the published snapshot is cloned on read.</para>
/// </remarks>
public sealed class ConfigGatewayInventory : IGatewayInventory, IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly Func<AppSettings?> _publishedSettings;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="ConfigGatewayInventory"/>.
    /// </summary>
    /// <param name="configManager">Raises the change notifications.</param>
    /// <param name="publishedSettings">
    /// Reads the settings as most recently loaded or saved, or null before the first load. Taken
    /// as a delegate because the abstraction exposes the event and not the snapshot.
    /// </param>
    public ConfigGatewayInventory(IConfigManager configManager, Func<AppSettings?> publishedSettings)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _publishedSettings = publishedSettings ?? throw new ArgumentNullException(nameof(publishedSettings));
        _configManager.SettingsChanged += OnSettingsChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<SshGatewayDto> Current => Snapshot(_publishedSettings());

    /// <inheritdoc />
    public event Action<IReadOnlyList<SshGatewayDto>>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configManager.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        Changed?.Invoke(Snapshot(settings));
    }

    private static IReadOnlyList<SshGatewayDto> Snapshot(AppSettings? settings)
        => settings?.SshGateways is { Count: > 0 } gateways ? gateways.ToList() : [];
}
