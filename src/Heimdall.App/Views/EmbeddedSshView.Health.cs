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

using System.Windows;
using Heimdall.Ssh;

namespace Heimdall.App.Views;

public partial class EmbeddedSshView
{
    private void OnHealthToggleClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _healthPanelVisible = !_healthPanelVisible;

        if (_healthPanelVisible)
        {
            ShowHealthPanel();
        }
        else
        {
            HideHealthPanel();
        }
    }

    private void ShowHealthPanel()
    {
        HealthPanel.Visibility = Visibility.Visible;
        HealthColumnDef.Width = new GridLength(HealthPanelWidth);
        LocalizeHealthLabels();
        StartHealthMonitor();
    }

    private void StartHealthMonitor()
    {
        var client = _session?.Client;
        if (client is null || !client.IsConnected)
        {
            Core.Logging.FileLogger.Warn(
                "ServerHealthMonitor: no connected SSH client available for health monitoring");
            return;
        }

        StopHealthMonitor();

        var pollSeconds = TerminalSettings?.SshHealthMonitorIntervalSeconds
            ?? Core.Configuration.AppSettings.DefaultSshHealthMonitorIntervalSeconds;
        _healthMonitor = new ServerHealthMonitor(TimeSpan.FromSeconds(pollSeconds));
        _healthMonitor.HealthUpdated += OnHealthDataReceived;
        _ = _healthMonitor.StartAsync(client);

        Core.Logging.FileLogger.Info("ServerHealthMonitor started");
    }

    private void HideHealthPanel()
    {
        HealthPanel.Visibility = Visibility.Collapsed;
        HealthColumnDef.Width = new GridLength(0);
        StopHealthMonitor();
    }

    private void ShowHealthButton(bool visible)
    {
        HealthToggleButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _healthPanelVisible)
        {
            _healthPanelVisible = false;
            HideHealthPanel();
        }
    }

    private void StopHealthMonitor()
    {
        if (_healthMonitor is not null)
        {
            _healthMonitor.HealthUpdated -= OnHealthDataReceived;
            _healthMonitor.Dispose();
            _healthMonitor = null;
            Core.Logging.FileLogger.Info("ServerHealthMonitor stopped");
        }
    }

    private void OnHealthDataReceived(ServerHealthData data)
    {
        BeginInvokeIfAvailable(() =>
        {
            if (_disposed || !_healthPanelVisible)
            {
                return;
            }

            if (!data.IsSupported)
            {
                SetHealthProgressEnabled(false);
                string unsupported = L("HealthPanelUnsupported");
                CpuProgressBar.Value = 0;
                CpuPercentText.Text = unsupported;
                RamProgressBar.Value = 0;
                RamDetailText.Text = unsupported;
                DiskProgressBar.Value = 0;
                DiskDetailText.Text = unsupported;
                HealthUptimeSection.Visibility = Visibility.Collapsed;
                HealthLoadSection.Visibility = Visibility.Collapsed;
                HealthNetSection.Visibility = Visibility.Collapsed;
                StopHealthMonitor();
                return;
            }

            SetHealthProgressEnabled(true);
            CpuProgressBar.Value = data.CpuPercent;
            CpuPercentText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("HealthPanelCpuFormat"),
                data.CpuPercent);

            double ramPercent = data.MemTotalMb > 0
                ? (double)data.MemUsedMb / data.MemTotalMb * 100.0
                : 0;
            RamProgressBar.Value = ramPercent;
            RamDetailText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("HealthPanelRamFormat"),
                data.MemUsedMb,
                data.MemTotalMb);

            DiskProgressBar.Value = data.DiskPercent;
            DiskDetailText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("HealthPanelDiskFormat"),
                data.DiskUsed,
                data.DiskTotal);

            if (!string.IsNullOrWhiteSpace(data.Uptime))
            {
                HealthUptimeText.Text = data.Uptime;
                HealthUptimeSection.Visibility = Visibility.Visible;
            }
            else
            {
                HealthUptimeSection.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(data.LoadAverage))
            {
                HealthLoadText.Text = data.LoadAverage;
                HealthLoadSection.Visibility = Visibility.Visible;
            }
            else
            {
                HealthLoadSection.Visibility = Visibility.Collapsed;
            }

            if (data.NetRxBytesPerSec.HasValue && data.NetTxBytesPerSec.HasValue)
            {
                HealthNetText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L("HealthPanelNetFormat"),
                    ServerHealthMonitor.FormatNetworkRate(data.NetRxBytesPerSec.Value),
                    ServerHealthMonitor.FormatNetworkRate(data.NetTxBytesPerSec.Value));
                HealthNetSection.Visibility = Visibility.Visible;
            }
            else
            {
                HealthNetSection.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void SetHealthProgressEnabled(bool isEnabled)
    {
        CpuProgressBar.IsEnabled = isEnabled;
        RamProgressBar.IsEnabled = isEnabled;
        DiskProgressBar.IsEnabled = isEnabled;
    }

    private void LocalizeHealthLabels()
    {
        HealthCpuLabel.Text = L("HealthPanelCpu");
        HealthRamLabel.Text = L("HealthPanelRam");
        HealthDiskLabel.Text = L("HealthPanelDisk");
        HealthUptimeLabel.Text = L("HealthPanelUptime");
        HealthLoadLabel.Text = L("HealthPanelLoad");
        HealthNetLabel.Text = L("HealthPanelNet");
    }
}
