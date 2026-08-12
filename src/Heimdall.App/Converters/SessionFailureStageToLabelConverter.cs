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
using System.Windows.Data;
using Heimdall.App.Localization;
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.Converters;

/// <summary>
/// Maps a <see cref="SessionFailureStage"/> to a localized display label.
/// </summary>
public sealed class SessionFailureStageToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionFailureStage stage)
        {
            return string.Empty;
        }

        return LocalizationSource.Instance[GetKey(stage)];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string GetKey(SessionFailureStage stage)
    {
        return stage switch
        {
            SessionFailureStage.RdpTunnel => "SessionFailureStageRdpTunnel",
            SessionFailureStage.RdpCredentialWrite => "SessionFailureStageRdpCredentialWrite",
            SessionFailureStage.RdpFileWrite => "SessionFailureStageRdpFileWrite",
            SessionFailureStage.RdpLaunch => "SessionFailureStageRdpLaunch",
            SessionFailureStage.RdpActiveXDisconnect => "SessionFailureStageRdpActiveXDisconnect",
            SessionFailureStage.SshGateway => "SessionFailureStageSshGateway",
            SessionFailureStage.SshPreflight => "SessionFailureStageSshPreflight",
            SessionFailureStage.SshAuth => "SessionFailureStageSshAuth",
            SessionFailureStage.SshHostKey => "SessionFailureStageSshHostKey",
            SessionFailureStage.SshPlinkFallback => "SessionFailureStageSshPlinkFallback",
            SessionFailureStage.SshPipeMode => "SessionFailureStageSshPipeMode",
            _ => "SessionFailureStageGeneric",
        };
    }
}
