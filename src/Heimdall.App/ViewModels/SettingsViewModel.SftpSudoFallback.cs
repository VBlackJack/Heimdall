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

using Heimdall.Core.Security;
using SftpSudoFallbackMode = Heimdall.Core.Models.SftpSudoFallbackMode;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The "run as root" choice of the SFTP browser and the organisation cap that limits it.
/// </summary>
/// <remarks>
/// The panel holds the user's own choice, so a cap never rewrites what is saved: removing the
/// cap later gives the user back the value they picked. The cap only decides which choices the
/// list offers and what the effective value is.
/// </remarks>
public partial class SettingsViewModel
{
    /// <summary>The cap the organisation set, or null when there is none.</summary>
    public SftpSudoFallbackMode? SftpSudoFallbackCap => _sftpSudoFallbackPolicy.Cap;

    /// <summary>True when an organisation cap exists.</summary>
    public bool IsSftpSudoFallbackCapped => SftpSudoFallbackCap is not null;

    /// <summary>Whether the "Ask" choice is within the cap.</summary>
    public bool IsSftpSudoFallbackAskAllowed => IsWithinSftpSudoFallbackCap(SftpSudoFallbackMode.Ask);

    /// <summary>Whether the "Automatic" choice is within the cap.</summary>
    public bool IsSftpSudoFallbackAutomaticAllowed => IsWithinSftpSudoFallbackCap(SftpSudoFallbackMode.Automatic);

    /// <summary>
    /// The line that says the value is set by the organisation, with what actually applies;
    /// empty when there is no cap.
    /// </summary>
    public string SftpSudoFallbackCapText => SftpSudoFallbackCap is { } cap
        ? _localizer.Format(
            "SettingsSftpSudoFallbackCapped",
            SftpSudoFallbackModeText(cap),
            SftpSudoFallbackModeText(_sftpSudoFallbackPolicy.Effective(ParseSftpSudoFallback(SftpSudoFallback))))
        : string.Empty;

    /// <summary>Reads the panel's name of a mode; anything unknown asks, like the settings file does.</summary>
    internal static SftpSudoFallbackMode ParseSftpSudoFallback(string? name)
        => Enum.TryParse(name, ignoreCase: false, out SftpSudoFallbackMode mode) && Enum.IsDefined(mode)
            ? mode
            : SftpSudoFallbackMode.Ask;

    private bool IsWithinSftpSudoFallbackCap(SftpSudoFallbackMode mode)
        => SftpSudoFallbackCap is not { } cap || mode <= cap;

    private string SftpSudoFallbackModeText(SftpSudoFallbackMode mode) => mode switch
    {
        SftpSudoFallbackMode.Never => _localizer["SettingsSftpSudoFallbackNever"],
        SftpSudoFallbackMode.Automatic => _localizer["SettingsSftpSudoFallbackAutomatic"],
        _ => _localizer["SettingsSftpSudoFallbackAsk"],
    };
}
