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

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Services;

/// <summary>
/// Computes transient Settings UI state for the external tools section.
/// </summary>
public sealed class ExternalToolSettingsService
{
    private readonly ExternalToolProviderService _externalToolProvider;
    private readonly LocalizationManager _localizer;

    public ExternalToolSettingsService(
        ExternalToolProviderService externalToolProvider,
        LocalizationManager localizer)
    {
        _externalToolProvider = externalToolProvider;
        _localizer = localizer;
    }

    /// <summary>
    /// Builds the localized status label describing detected external tools.
    /// </summary>
    public string BuildDetectedToolsStatus()
    {
        var count = _externalToolProvider.DetectedTools.Count;
        return count > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                _localizer["ExtToolStatusDetected"],
                count)
            : _localizer["ExtToolStatusNone"];
    }

    /// <summary>
    /// Builds the placeholder legend displayed below the external tool editor.
    /// </summary>
    public IReadOnlyList<FrameworkElement> BuildPlaceholderItems(double captionFontSize)
    {
        var items = new List<FrameworkElement>(ExternalToolDefinition.SupportedPlaceholders.Length);

        foreach (var (variable, descKey) in ExternalToolDefinition.SupportedPlaceholders)
        {
            var panel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 2)
            };

            var variableLabel = new TextBlock
            {
                Text = variable,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = captionFontSize,
                VerticalAlignment = VerticalAlignment.Center
            };
            variableLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            panel.Children.Add(variableLabel);

            var descLabel = new TextBlock
            {
                Text = $" - {_localizer[descKey]}",
                FontSize = captionFontSize,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            };
            descLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(descLabel);

            items.Add(panel);
        }

        return items;
    }

    /// <summary>
    /// Builds the command preview shown for the currently selected external tool.
    /// </summary>
    public string BuildPreview(
        ExternalToolItemViewModel? tool,
        ServerItemViewModel? selectedServer)
    {
        if (tool is null)
        {
            return string.Empty;
        }

        if (selectedServer is null)
        {
            return $"{tool.ExecutablePath} {tool.Arguments}".Trim();
        }

        var definition = new ExternalToolDefinition
        {
            ExecutablePath = tool.ExecutablePath,
            Arguments = tool.Arguments
        };

        var resolved = definition.ResolveArguments(
            selectedServer.RemoteServer,
            selectedServer.EffectivePort,
            selectedServer.Username,
            serverName: selectedServer.DisplayName,
            protocol: selectedServer.ConnectionType,
            keyFile: selectedServer.SshKeyPath,
            project: selectedServer.ProjectName,
            gateway: selectedServer.GatewayName);

        return $"{tool.ExecutablePath} {resolved}".Trim();
    }
}
