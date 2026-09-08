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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Ssh;

namespace Heimdall.App.UiTests.Views;

[Collection(DesktopUiCollection.Name)]
public sealed class GatewayDiagnosticDialogTests
{
    [StaFact]
    public void DiagnosticButton_UsesRealClickBindingAndLocalizedReport()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            GatewayDialogViewModel vm = new()
            {
                Localizer = WpfTestHost.Localizer,
                Name = "Application bastion",
                Host = "app-bastion.example",
                User = "audit",
                SelectedParentGatewayId = "parent",
                DiagnosticTargetHost = "database.example",
                DiagnosticTargetPort = "5432"
            };
            vm.ConfigureDiagnostics(
                [new SshGatewayDto { Id = "parent", Name = "VPN bastion", Host = "vpn-bastion.example", User = "audit" }],
                (_, _, _, _, _) => Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>(
                    [new(1, false, true, null, 24), new(2, false, true, null, 53),
                     new(3, true, false, SshFailureCode.ForwardingFailed, 31)]));
            GatewayDialog window = new() { DataContext = vm };
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(600, 760));
            content.Arrange(new Rect(0, 0, 600, 760));
            content.UpdateLayout();

            Button test = Assert.IsType<Button>(window.FindName("TestRouteButton"));
            Assert.True(test.IsEnabled);
            test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("Destination TCP access", vm.DiagnosticReport);
            Assert.DoesNotContain("database.example", vm.DiagnosticReport);
            Assert.Contains("VPN bastion", vm.FullDiagnosticRoute);
            vm.IsDiagnosticRunning = true;
            Assert.False(test.IsEnabled);
            Assert.False(Assert.IsType<Button>(window.FindName("SaveBtn")).IsEnabled);
            vm.IsDiagnosticRunning = false;

            string? renderPath = Environment.GetEnvironmentVariable("HEIMDALL_GATEWAY_DIAGNOSTIC_RENDER");
            if (!string.IsNullOrWhiteSpace(renderPath))
            {
                ResourceDictionary[] original = Application.Current.Resources.MergedDictionaries.ToArray();
                ThemeForge.Theme.ThemeService theme = new(Application.Current, ThemeForge.Theme.ThemeNames.All);
                theme.ApplyTheme(ThemeForge.Theme.ThemeNames.Drakul);
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Heimdall;component/Themes/HeimdallThemeBridge.xaml")
                });
                Border panel = Assert.IsType<Border>(window.FindName("DiagnosticPanel"));
                Assert.IsAssignableFrom<Panel>(panel.Parent).Children.Remove(panel);
                Border canvas = new()
                {
                    Child = panel,
                    DataContext = vm,
                    Padding = new Thickness(16),
                    Background = (Brush)Application.Current.FindResource("BackgroundBrush")
                };
                canvas.Measure(new Size(600, double.PositiveInfinity));
                canvas.Arrange(new Rect(new Point(0, 0), canvas.DesiredSize));
                canvas.UpdateLayout();
                RenderTargetBitmap bitmap = new((int)Math.Ceiling(canvas.ActualWidth),
                    (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream output = File.Create(renderPath);
                encoder.Save(output);
                Application.Current.Resources.MergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in original)
                    Application.Current.Resources.MergedDictionaries.Add(dictionary);
            }
            vm.CloseDiagnostics();
        });
    }
}
