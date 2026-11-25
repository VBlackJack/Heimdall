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
using Heimdall.App.Localization;
using Heimdall.App.Theming;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Themed message dialog that replaces native <see cref="MessageBox"/>
/// to maintain visual consistency with the Dark/Light theme system.
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>Resource key of the accent-filled button style.</summary>
    internal const string AccentButtonStyleKey = "PrimaryButtonStyle";

    /// <summary>Resource key of the plain button style.</summary>
    internal const string PlainButtonStyleKey = "SecondaryButtonStyle";

    /// <summary>Width of the large layout, in device-independent pixels, before it is clamped to the work area.</summary>
    internal const double LargeWidth = 720;

    /// <summary>Share of the work area height the large layout may take at most.</summary>
    internal const double LargeMaxHeightShare = 0.8;

    private const string AppNameLocaleKey = "AppName";

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

    /// <summary>The width of the large layout inside a work area: wide, but never wider than the area.</summary>
    /// <param name="workAreaWidth">The width of the work area, in device-independent pixels.</param>
    internal static double LargeWidthWithin(double workAreaWidth) => Math.Min(LargeWidth, workAreaWidth);

    /// <summary>The maximum height of the large layout inside a work area: a share of it, so never above it.</summary>
    /// <param name="workAreaHeight">The height of the work area, in device-independent pixels.</param>
    internal static double LargeMaxHeightWithin(double workAreaHeight) => workAreaHeight * LargeMaxHeightShare;

    /// <summary>
    /// Opts this dialog into the large layout, for a long message such as a preview: wider and taller
    /// than the default, within the given work area. The message stays scrollable and the buttons visible.
    /// </summary>
    /// <param name="workArea">The work area of the screen the dialog opens on, in device-independent pixels.</param>
    internal void UseLargeLayout(Rect workArea)
    {
        Width = LargeWidthWithin(workArea.Width);
        MaxHeight = LargeMaxHeightWithin(workArea.Height);
    }

    /// <summary>The work area of the screen the owner is on, or of the primary screen without an owner.</summary>
    /// <param name="owner">The owner window, or null.</param>
    internal static Rect WorkAreaOf(Window? owner)
    {
        System.IntPtr handle = owner is null ? System.IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        if (owner is null || handle == System.IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        System.Windows.DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
        return new Rect(area.X / dpi.DpiScaleX, area.Y / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    /// <summary>
    /// Sets the window title and the in-window header from <paramref name="title"/>, falling back
    /// to the localized application name for the window title when the caller passes a blank one.
    /// </summary>
    /// <param name="title">The caller-supplied title.</param>
    internal void ApplyTitle(string? title)
    {
        TitleText.Text = title ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title)
            ? LocalizationSource.Instance[AppNameLocaleKey]
            : title.Trim();
    }

    /// <summary>
    /// Shows a themed information or error message with a single OK button.
    /// </summary>
    public static void ShowMessage(Window? owner, string title, string message, string severity = "info", string primaryLabel = "OK")
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.ApplyTitle(title);
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

        ConfirmButtonStyles styles = DescribeConfirmButtonStyles(primaryIsDefault);
        dialog.BtnPrimary.SetResourceReference(StyleProperty, styles.PrimaryStyleKey);
        dialog.BtnSecondary.SetResourceReference(StyleProperty, styles.SecondaryStyleKey);
    }

    /// <summary>Style resource key each confirm button wears.</summary>
    /// <param name="PrimaryStyleKey">Key of the accepting button's style.</param>
    /// <param name="SecondaryStyleKey">Key of the declining button's style.</param>
    internal readonly record struct ConfirmButtonStyles(string PrimaryStyleKey, string SecondaryStyleKey);

    /// <summary>Decides which confirm button carries the accent: the one Enter presses.</summary>
    /// <param name="primaryIsDefault">Whether Enter accepts.</param>
    /// <remarks>
    /// The accent follows the default button, as on the multi-answer question, so the filled
    /// button is never the one Enter does not press.
    /// </remarks>
    internal static ConfirmButtonStyles DescribeConfirmButtonStyles(bool primaryIsDefault)
        => primaryIsDefault
            ? new ConfirmButtonStyles(AccentButtonStyleKey, PlainButtonStyleKey)
            : new ConfirmButtonStyles(PlainButtonStyleKey, AccentButtonStyleKey);

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
        bool primaryIsDefault = true,
        bool large = false)
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.Topmost = topmost;
        if (large)
        {
            dialog.UseLargeLayout(WorkAreaOf(owner));
        }

        dialog.ApplyTitle(title);
        dialog.MessageText.Text = message;
        ConfigureConfirmButtons(dialog, primaryLabel, secondaryLabel, primaryIsDefault);

        ApplySeverityStyle(dialog, severity);

        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>
    /// Whether Enter accepts a question whose yes throws work away: no. Kept beside the
    /// overload that applies it, so the rule is pinned where it is used.
    /// </summary>
    internal const bool DestructivePrimaryIsDefault = false;

    /// <summary>
    /// Asks before throwing work away (unsaved edits, a stored item). Enter and Escape both
    /// keep it, so only a deliberate click or the access key destroys anything.
    /// </summary>
    /// <param name="owner">The window the question belongs to.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">What would be lost.</param>
    /// <param name="destroyLabel">The label of the button that throws the work away.</param>
    /// <param name="keepLabel">The label of the button that keeps it.</param>
    /// <returns><see langword="true"/> when the user chose to throw the work away.</returns>
    public static bool ShowDestructiveConfirm(
        Window? owner,
        string title,
        string message,
        string destroyLabel,
        string keepLabel) =>
        ShowConfirm(
            owner,
            title,
            message,
            "warning",
            destroyLabel,
            keepLabel,
            primaryIsDefault: DestructivePrimaryIsDefault);

    private static void ApplySeverityStyle(MessageDialog dialog, string severity)
    {
        // Icon glyph + color from Segoe MDL2 Assets
        var (icon, brushKey) = severity switch
        {
            "error" => ("\uEA39", "ErrorTextBrush"),       // ErrorBadge
            "warning" or "danger" => ("\uE7BA", "WarningTextBrush"), // Warning
            "success" => ("\uE73E", "SuccessTextBrush"),    // CheckMark
            _ => ("\uE946", "InfoTextBrush")                // Info
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
        dialog.ApplyTitle(title);
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

    /// <summary>
    /// The answer picked in a <see cref="ShowChoice"/> dialog, or null when it was cancelled.
    /// </summary>
    public int? ChoiceResult { get; private set; }

    /// <summary>
    /// Shows a themed question with one button per answer and a cancel button.
    /// </summary>
    /// <param name="owner">The window the question belongs to.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question.</param>
    /// <param name="choices">One label per answer, laid out left to right.</param>
    /// <param name="defaultIndex">The answer Enter picks; drawn as the primary button.</param>
    /// <param name="cancelLabel">The label of the cancel button, which Escape presses.</param>
    /// <returns>The index of the answer picked, or null when cancelled or closed.</returns>
    public static int? ShowChoice(
        Window? owner,
        string title,
        string message,
        IReadOnlyList<string> choices,
        int defaultIndex,
        string cancelLabel)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var dialog = new MessageDialog { Owner = owner };
        dialog.ApplyTitle(title);
        dialog.MessageText.Text = message;
        dialog.ConfigureChoiceButtons(choices, defaultIndex, cancelLabel);
        ApplySeverityStyle(dialog, "info");

        dialog.ShowDialog();
        return dialog.ChoiceResult;
    }

    /// <summary>
    /// Replaces the fixed buttons with the cancel button followed by one button per answer.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="ShowChoice"/>, which ends in a blocking <c>ShowDialog</c>, so
    /// the layout and the keyboard defaults can be checked without showing anything.
    /// </remarks>
    internal void ConfigureChoiceButtons(IReadOnlyList<string> choices, int defaultIndex, string cancelLabel)
    {
        BtnPrimary.Visibility = Visibility.Collapsed;
        BtnPrimary.IsDefault = false;
        BtnSecondary.Visibility = Visibility.Collapsed;
        BtnTertiary.Content = cancelLabel;
        BtnTertiary.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(BtnTertiary, cancelLabel);

        for (var index = 0; index < choices.Count; index++)
        {
            var answer = index;
            var label = choices[index];
            var isDefault = index == defaultIndex;
            var button = new System.Windows.Controls.Button
            {
                Content = label,
                IsDefault = isDefault,
                MinWidth = BtnSecondary.MinWidth,
                Padding = BtnSecondary.Padding,
                FontSize = BtnSecondary.FontSize,
                Margin = BtnSecondary.Margin
            };
            button.SetResourceReference(
                StyleProperty,
                isDefault ? AccentButtonStyleKey : PlainButtonStyleKey);
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                ChoiceResult = answer;
                DialogResult = true;
            };
            ButtonPanel.Children.Insert(ButtonPanel.Children.IndexOf(BtnPrimary), button);
        }
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
