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
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Scheduled task add/edit dialog. Code-behind is limited to localization,
/// validation triggering, and DialogResult assignment.
/// </summary>
public partial class ScheduledTaskDialog : Window
{
    public ScheduledTaskDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) =>
        {
            if (DataContext is ScheduledTaskDialogViewModel { Localizer: not null } vm)
            {
                // Button labels
                CancelBtn.Content = vm.Localizer["BtnCancel"];
                SaveBtn.Content = vm.Localizer["BtnSave"];
                System.Windows.Automation.AutomationProperties.SetName(CancelBtn, vm.Localizer["BtnCancel"]);
                System.Windows.Automation.AutomationProperties.SetName(SaveBtn, vm.Localizer["BtnSave"]);

                // Form labels
                LblServer.Text = vm.Localizer["ScheduledTaskFieldServerLabel"];
                LblScheduleType.Text = vm.Localizer["ScheduledTaskFieldScheduleType"];
                LblTimeOfDay.Text = vm.Localizer["ScheduledTaskFieldTimeOfDay"];
                LblInterval.Text = vm.Localizer["ScheduledTaskFieldInterval"];
                ChkEnabled.Content = vm.Localizer["ScheduledTaskFieldEnabled"];
                HintTimeOfDay.Text = vm.Localizer["ScheduledTaskFieldTimeOfDayHint"];
                IntervalSuffix.Text = vm.Localizer["ScheduledTaskFieldIntervalSuffix"];

                // Schedule type combo items
                CmbItemDaily.Content = vm.Localizer["ScheduleTypeDaily"];
                CmbItemInterval.Content = vm.Localizer["ScheduleTypeInterval"];

                // Input field accessibility
                System.Windows.Automation.AutomationProperties.SetName(CmbServer, vm.Localizer["ScheduledTaskFieldServerLabel"]);
                System.Windows.Automation.AutomationProperties.SetName(CmbScheduleType, vm.Localizer["ScheduledTaskFieldScheduleType"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtTimeOfDay, vm.Localizer["ScheduledTaskFieldTimeOfDay"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtInterval, vm.Localizer["ScheduledTaskFieldInterval"]);
                System.Windows.Automation.AutomationProperties.SetName(ChkEnabled, vm.Localizer["ScheduledTaskFieldEnabled"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtNextRunPreview, vm.Localizer["ScheduledColNextRun"]);

                // Refresh the next-run preview now that Localizer is available
                vm.RefreshNextRunPreview();
            }

            CmbServer.Focus();
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScheduledTaskDialogViewModel vm)
        {
            return;
        }

        vm.ValidateCommand.Execute(null);

        if (vm.ValidationError is null)
        {
            DialogResult = true;
            return;
        }

        // Same as the session dialog: the refusal and the field to fix are not left to be found.
        UIElement? target = vm.FirstInvalidField switch
        {
            ScheduledTaskField.Server => CmbServer,
            ScheduledTaskField.TimeOfDay => TxtTimeOfDay,
            ScheduledTaskField.Interval => TxtInterval,
            _ => null
        };
        target?.Focus();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Skip dirty check when the user clicked Save (DialogResult == true)
        if (DialogResult == true) return;
        if (DataContext is not ScheduledTaskDialogViewModel { IsDirty: true } vm) return;

        var title = vm.Localizer?["DialogUnsavedWarningTitle"] ?? "Unsaved Changes";
        var message = vm.Localizer?["DialogUnsavedWarning"]
            ?? "You have unsaved changes. Discard them and close?";
        var yes = vm.Localizer?["BtnYes"] ?? "Yes";
        var no = vm.Localizer?["BtnNo"] ?? "No";

        var discard = MessageDialog.ShowDestructiveConfirm(this, title, message, yes, no);
        if (!discard)
        {
            e.Cancel = true;
        }
    }
}
