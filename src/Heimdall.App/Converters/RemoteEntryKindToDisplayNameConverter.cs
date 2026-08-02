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
using Heimdall.App.ViewModels;
using Heimdall.Sftp;

namespace Heimdall.App.Converters;

/// <summary>
/// Maps a remote entry kind to its localized display name.
/// </summary>
public sealed class RemoteEntryKindToDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RemoteEntryKind kind)
        {
            return string.Empty;
        }

        string key = EmbeddedSftpViewModel.GetRemoteEntryKindDisplayKey(kind);
        return LocalizationSource.Instance[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
