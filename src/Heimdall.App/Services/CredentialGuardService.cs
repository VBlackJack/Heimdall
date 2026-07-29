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

using System.Collections;
using System.Globalization;
using System.Management;

namespace Heimdall.App.Services;

public enum CredentialGuardState
{
    Active,
    Inactive,
    Indeterminate
}

public sealed record CredentialGuardStatus(
    CredentialGuardState State,
    string? FailureReason = null);

public interface ICredentialGuardService
{
    Task<CredentialGuardStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects the local Credential Guard runtime state through Device Guard WMI.
/// Only definitive results are cached for the lifetime of the process.
/// </summary>
internal sealed class CredentialGuardService : ICredentialGuardService
{
    private readonly Func<object?> _querySecurityServicesRunning;
    private readonly SemaphoreSlim _detectionGate = new(1, 1);
    private CredentialGuardStatus? _cachedDefinitiveStatus;

    public CredentialGuardService()
        : this(QuerySecurityServicesRunning)
    {
    }

    internal CredentialGuardService(Func<object?> querySecurityServicesRunning)
    {
        ArgumentNullException.ThrowIfNull(querySecurityServicesRunning);
        _querySecurityServicesRunning = querySecurityServicesRunning;
    }

    public async Task<CredentialGuardStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        CredentialGuardStatus? cached = Volatile.Read(ref _cachedDefinitiveStatus);
        if (cached is not null)
        {
            return cached;
        }

        await _detectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedDefinitiveStatus;
            if (cached is not null)
            {
                return cached;
            }

            CredentialGuardStatus detected = await Task.Run(Detect, cancellationToken)
                .ConfigureAwait(false);
            if (detected.State is not CredentialGuardState.Indeterminate)
            {
                Volatile.Write(ref _cachedDefinitiveStatus, detected);
            }

            return detected;
        }
        finally
        {
            _detectionGate.Release();
        }
    }

    private CredentialGuardStatus Detect()
    {
        try
        {
            return MapSecurityServicesRunning(_querySecurityServicesRunning());
        }
        catch (Exception ex)
        {
            return new CredentialGuardStatus(
                CredentialGuardState.Indeterminate,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static CredentialGuardStatus MapSecurityServicesRunning(object? rawValue)
    {
        if (rawValue is null)
        {
            return Indeterminate("SecurityServicesRunning returned no value.");
        }

        if (rawValue is IEnumerable values and not string)
        {
            var foundValue = false;
            foreach (object? value in values)
            {
                if (!TryConvertServiceId(value, out uint serviceId))
                {
                    return Indeterminate("SecurityServicesRunning contained an invalid value.");
                }

                foundValue = true;
                if (serviceId == 1)
                {
                    return new CredentialGuardStatus(CredentialGuardState.Active);
                }
            }

            return foundValue
                ? new CredentialGuardStatus(CredentialGuardState.Inactive)
                : Indeterminate("SecurityServicesRunning returned an empty collection.");
        }

        if (!TryConvertServiceId(rawValue, out uint scalarServiceId))
        {
            return Indeterminate("SecurityServicesRunning returned an invalid value.");
        }

        return new CredentialGuardStatus(
            scalarServiceId == 1
                ? CredentialGuardState.Active
                : CredentialGuardState.Inactive);
    }

    private static CredentialGuardStatus Indeterminate(string reason) =>
        new(CredentialGuardState.Indeterminate, reason);

    private static bool TryConvertServiceId(object? value, out uint serviceId)
    {
        try
        {
            serviceId = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            serviceId = 0;
            return false;
        }
    }

    private static object? QuerySecurityServicesRunning()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\DeviceGuard",
            "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject result in results)
        {
            using (result)
            {
                return result["SecurityServicesRunning"];
            }
        }

        return null;
    }
}
