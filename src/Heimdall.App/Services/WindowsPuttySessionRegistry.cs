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

using Heimdall.Ssh.Plink;
using Microsoft.Win32;

namespace Heimdall.App.Services;

/// <summary>
/// <see cref="IPuttySessionRegistry"/> over <c>HKCU\Software\SimonTatham\PuTTY\Sessions</c>.
/// </summary>
public sealed class WindowsPuttySessionRegistry : IPuttySessionRegistry
{
    /// <inheritdoc />
    public IReadOnlyList<string> GetSessionNames()
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
            PlinkSizeSessionNaming.SessionsRegistryPath,
            writable: false);
        return root is null ? [] : root.GetSubKeyNames();
    }

    /// <inheritdoc />
    public IReadOnlyList<PuttyRegistryValue>? ReadSession(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            SessionPath(sessionName),
            writable: false);
        if (key is null)
        {
            return null;
        }

        var values = new List<PuttyRegistryValue>();
        foreach (string valueName in key.GetValueNames())
        {
            object? value = key.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                continue;
            }

            values.Add(new PuttyRegistryValue(valueName, value, key.GetValueKind(valueName)));
        }

        return values;
    }

    /// <inheritdoc />
    public void WriteSession(string sessionName, IReadOnlyList<PuttyRegistryValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(values);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SessionPath(sessionName), writable: true);
        foreach (PuttyRegistryValue value in values)
        {
            key.SetValue(value.Name, value.Value, value.Kind);
        }
    }

    /// <inheritdoc />
    public void DeleteSession(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        // The only deletion Heimdall ever performs here is of its own temporary sessions. Refusing
        // any other name keeps a wrong caller from reaching a session the user saved in PuTTY.
        if (!PlinkSizeSessionNaming.IsHeimdallSession(sessionName))
        {
            throw new ArgumentException(null, nameof(sessionName));
        }

        Registry.CurrentUser.DeleteSubKeyTree(SessionPath(sessionName), throwOnMissingSubKey: false);
    }

    private static string SessionPath(string sessionName) =>
        $@"{PlinkSizeSessionNaming.SessionsRegistryPath}\{sessionName}";
}
