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

namespace Heimdall.App.Services;

/// <summary>
/// Aggregates all <see cref="IExternalToolProvider"/> implementations,
/// scans for installed third-party tools, and registers them as
/// <see cref="ToolDescriptor"/> entries available to the <see cref="ToolRegistry"/>.
/// </summary>
public sealed class ExternalToolProviderService
{
    private readonly IExternalToolProvider[] _providers;
    private List<ExternalToolInfo> _detectedTools = [];

    /// <summary>All tools detected during the last scan.</summary>
    public IReadOnlyList<ExternalToolInfo> DetectedTools => _detectedTools;

    public ExternalToolProviderService()
    {
        _providers =
        [
            new SysinternalsToolProvider(),
            new NirSoftToolProvider(),
            new NanaRunToolProvider(),
        ];
    }

    /// <summary>
    /// Scans all registered providers for available tools.
    /// Call from a background thread - performs disk I/O.
    /// </summary>
    /// <param name="settings">Current app settings for user-configured search paths.</param>
    public void ScanAll(AppSettings? settings)
    {
        var allTools = new List<ExternalToolInfo>();

        foreach (var provider in _providers)
        {
            try
            {
                var customPaths = BuildCustomPaths(settings, provider);
                var tools = provider.Scan(customPaths);
                allTools.AddRange(tools);
                Core.Logging.FileLogger.Info(
                    $"[ExternalToolProvider] {provider.Name}: {tools.Count} tool(s) detected");
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"[ExternalToolProvider] {provider.Name} scan failed: {ex.Message}");
            }
        }

        _detectedTools = allTools;
    }

    private static IEnumerable<string>? BuildCustomPaths(
        AppSettings? settings,
        IExternalToolProvider provider)
    {
        if (settings is null) return null;

        var paths = new List<string>();

        switch (provider)
        {
            case SysinternalsToolProvider:
                if (!string.IsNullOrWhiteSpace(settings.SysinternalsPath))
                    paths.Add(settings.SysinternalsPath);
                break;
            case NirSoftToolProvider:
                if (!string.IsNullOrWhiteSpace(settings.NirSoftPath))
                    paths.Add(settings.NirSoftPath);
                break;
            case NanaRunToolProvider:
                if (!string.IsNullOrWhiteSpace(settings.NanaRunPath))
                    paths.Add(settings.NanaRunPath);
                break;
        }

        return paths.Count > 0 ? paths : null;
    }
}
