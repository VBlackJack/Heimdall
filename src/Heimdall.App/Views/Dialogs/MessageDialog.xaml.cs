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

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Themed message dialog that replaces native <see cref="MessageBox"/>
/// to maintain visual consistency with the Dark/Light theme system.
/// </summary>
public partial class MessageDialog : Window
{
    public bool Result { get; private set; }

    /// <summary>
    /// Three-way result: true = primary (Save), false = secondary (Discard), null = tertiary (Cancel).
    /// </summary>
    public bool? ThreeWayResult { get; private set; }

    public MessageDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
    }

    /// <summary>
    /// Shows a themed information or error message with a single OK button.
    /// </summary>
    public static void ShowMessage(Window? owner, string title, string message, string severity = "info", string primaryLabel = "OK")
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.BtnPrimary.Content = primaryLabel;
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnPrimary, primaryLabel);

        // Single-button dialog: Escape must close like OK. The XAML IsCancel sits on
        // BtnTertiary, which is collapsed here and never receives the Escape invocation.
        dialog.BtnPrimary.IsCancel = true;
        dialog.BtnTertiary.IsCancel = false;

        ApplySeverityStyle(dialog, severity);

        dialog.ShowDialog();
    }

    /// <summary>
    /// Shows a themed confirmation dialog with Yes/No buttons.
    /// Returns true if the user clicked the primary (Yes) button.
    /// </summary>
    /// <summary>Labels the two confirm buttons and places the keyboard defaults.</summary>
    /// <param name="dialog">The dialog being prepared.</param>
    /// <param name="primaryLabel">Label of the accepting button.</param>
    /// <param name="secondaryLabel">Label of the declining button.</param>
    /// <param name="primaryIsDefault">Whether Enter accepts.</param>
    /// <remarks>
    /// Separated from <see cref="ShowConfirm"/> because that method ends in a blocking
    /// <c>ShowDialog</c>, so nothing downstream of it can be observed by a test. Which
    /// key does what is exactly the part worth pinning.
    /// </remarks>
    internal static void ConfigureConfirmButtons(
        MessageDialog dialog,
        string primaryLabel,
        string secondaryLabel,
        bool primaryIsDefault)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.BtnPrimary.Content = primaryLabel;
        dialog.BtnSecondary.Content = secondaryLabel;
        dialog.BtnSecondary.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnPrimary, primaryLabel);
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnSecondary, secondaryLabel);

        ConfirmKeyRoles roles = DescribeConfirmKeyRoles(primaryIsDefault);
        dialog.BtnSecondary.IsCancel = roles.SecondaryIsCancel;
        dialog.BtnTertiary.IsCancel = roles.TertiaryIsCancel;
        dialog.BtnPrimary.IsDefault = roles.PrimaryIsDefault;
        dialog.BtnSecondary.IsDefault = roles.SecondaryIsDefault;
    }

    /// <summary>Which keyboard role each confirm button carries.</summary>
    /// <param name="PrimaryIsDefault">Whether Enter accepts.</param>
    /// <param name="SecondaryIsDefault">Whether Enter declines.</param>
    /// <param name="SecondaryIsCancel">Whether Escape declines.</param>
    /// <param name="TertiaryIsCancel">Whether Escape reaches the collapsed button.</param>
    internal readonly record struct ConfirmKeyRoles(
        bool PrimaryIsDefault,
        bool SecondaryIsDefault,
        bool SecondaryIsCancel,
        bool TertiaryIsCancel);

    /// <summary>Decides which key does what on a two-button confirmation.</summary>
    /// <param name="primaryIsDefault">Whether the caller wants Enter to accept.</param>
    /// <remarks>
    /// Escape always maps to the non-destructive outcome: BtnSecondary's handler sets
    /// Result = false, and the XAML IsCancel on the collapsed BtnTertiary is cleared so
    /// Escape cannot land on a button nobody can see.
    /// <para>
    /// Enter follows the caller. That is right when accepting is the ordinary answer;
    /// where it destroys something the caller moves the default onto the declining
    /// button, so both keys agree and a user who has been pressing Enter at an
    /// unresponsive surface does not confirm by momentum. Exactly one button carries
    /// the default, because WPF allows one per scope.
    /// </para>
    /// <para>
    /// Separate from the assignment because a test cannot construct this dialog: a
    /// Window built on the shared test dispatcher seals application-level styles onto
    /// that thread and every later test that touches them fails on thread affinity.
    /// So the rule is pinned here and the four assignments above are not.
    /// </para>
    /// </remarks>
    internal static ConfirmKeyRoles DescribeConfirmKeyRoles(bool primaryIsDefault) =>
        new(
            PrimaryIsDefault: primaryIsDefault,
            SecondaryIsDefault: !primaryIsDefault,
            SecondaryIsCancel: true,
            TertiaryIsCancel: false);

    public static bool ShowConfirm(
        Window? owner,
        string title,
        string message,
        string severity = "info",
        string primaryLabel = "Yes",
        string secondaryLabel = "No",
        bool topmost = false,
        bool primaryIsDefault = true)
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.Topmost = topmost;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        ConfigureConfirmButtons(dialog, primaryLabel, secondaryLabel, primaryIsDefault);

        ApplySeverityStyle(dialog, severity);

        dialog.ShowDialog();
        return dialog.Result;
    }

    private static void ApplySeverityStyle(MessageDialog dialog, string severity)
    {
        // Icon glyph + color from Segoe MDL2 Assets
        var (icon, brushKey) = severity switch
        {
            "error" => ("\uEA39", "ErrorBrush"),       // ErrorBadge
            "warning" or "danger" => ("\uE7BA", "WarningBrush"), // Warning
            "success" => ("\uE73E", "SuccessBrush"),    // CheckMark
            _ => ("\uE946", "InfoBrush")                // Info
        };

        dialog.IconText.Text = icon;
        if (dialog.TryFindResource(brushKey) is System.Windows.Media.Brush brush)
        {
            dialog.IconText.Foreground = brush;
        }
    }

    /// <summary>
    /// Shows a three-choice dialog (e.g., Save / Discard / Cancel).
    /// Returns true (primary), false (secondary), or null (tertiary/cancel).
    /// </summary>
    public static bool? ShowThreeWay(
        Window? owner,
        string title,
        string message,
        string severity = "warning",
        string primaryLabel = "Save",
        string secondaryLabel = "Discard",
        string tertiaryLabel = "Cancel")
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.BtnPrimary.Content = primaryLabel;
        dialog.BtnSecondary.Content = secondaryLabel;
        dialog.BtnSecondary.Visibility = Visibility.Visible;
        dialog.BtnTertiary.Content = tertiaryLabel;
        dialog.BtnTertiary.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnPrimary, primaryLabel);
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnSecondary, secondaryLabel);
        System.Windows.Automation.AutomationProperties.SetName(dialog.BtnTertiary, tertiaryLabel);

        ApplySeverityStyle(dialog, severity);

        dialog.ShowDialog();
        return dialog.ThreeWayResult;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        ThreeWayResult = true;
        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        ThreeWayResult = false;
        DialogResult = true;
    }

    private void OnTertiaryClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        ThreeWayResult = null;
        DialogResult = false;
    }
}
