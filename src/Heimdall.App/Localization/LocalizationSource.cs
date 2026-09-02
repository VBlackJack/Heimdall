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

using System.ComponentModel;
using Heimdall.Core.Localization;

namespace Heimdall.App.Localization;

/// <summary>
/// Singleton bridge between <see cref="LocalizationManager"/> and WPF bindings.
/// Exposes an indexer that resolves i18n keys, and raises <see cref="PropertyChanged"/>
/// for <c>Item[]</c> when the locale changes - causing all <c>{loc:Translate}</c>
/// bindings to re-evaluate automatically.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private static readonly LocalizationSource _instance = new();

    private LocalizationManager? _manager;

    /// <summary>
    /// Gets the singleton instance used by <see cref="TranslateExtension"/> bindings.
    /// </summary>
    public static LocalizationSource Instance => _instance;

    /// <summary>
    /// Resolves a locale key to its translated string.
    /// Falls back to the raw key if no translation is loaded.
    /// </summary>
    public string this[string key] => _manager?.GetString(key) ?? key;

    /// <summary>
    /// Connects this bridge to the application's <see cref="LocalizationManager"/> singleton.
    /// Must be called once during startup, after the initial locale has been loaded.
    /// </summary>
    public void Initialize(LocalizationManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (_manager is not null)
        {
            _manager.LocaleChanged -= OnLocaleChanged;
        }

        _manager = manager;
        _manager.LocaleChanged += OnLocaleChanged;
    }

    private void OnLocaleChanged(string _)
    {
        // "Item[]" is the WPF convention for signaling that all indexer
        // values have changed, causing every Binding path like [Key] to refresh.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
}
