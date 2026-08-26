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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Onboarding;

/// <summary>
/// View-model for the first-launch onboarding overlay. Owns the 3-step
/// state machine, resolves all localized labels, and persists completion
/// to <see cref="AppSettings.OnboardingCompleted"/>.
/// </summary>
/// <remarks>
/// <para>
/// The view-model is intentionally pure: it does not know about the host
/// window's tab system or sidebar. Instead it raises
/// <see cref="StepCompleted"/> with the index of the step that was just
/// finished so the host view can perform whatever navigation it owns
/// (switching tabs, toggling sidebars, etc.). A single <see cref="Completed"/>
/// event is raised once persistence succeeds.
/// </para>
/// <para>
/// Tab navigation will be factored behind a service in a later refactor
/// phase; do not pull it into this class.
/// </para>
/// </remarks>
public sealed partial class OnboardingFlowViewModel : ObservableObject
{
    /// <summary>
    /// One step of the tour: what it says, and what it points at.
    /// </summary>
    /// <param name="TitleKey">Locale key for the heading.</param>
    /// <param name="BodyKey">Locale key for the body text.</param>
    /// <param name="TargetElementName">
    /// The <c>x:Name</c> of the control to spotlight in the shell, or null for a centred card
    /// with no target. Resolved by name at display time; a step whose target cannot be found
    /// degrades to a centred card rather than pointing at nothing.
    /// </param>
    /// <param name="ShellTab">
    /// The top-level tab to open BEFORE the step is shown. A spotlight on a control inside a
    /// closed tab would highlight empty space, so navigation has to happen first - the previous
    /// flow navigated AFTER a step was completed, which is why its text described tabs the user
    /// was not looking at while they read it.
    /// </param>
    public sealed record Step(
        string TitleKey,
        string BodyKey,
        string? TargetElementName,
        string? ShellTab);

    /// <summary>
    /// The tour, in order.
    /// </summary>
    /// <remarks>
    /// Built from where first-time users were measured to stumble rather than from a tour of the
    /// interface: the session list they could not find their first server in, the button that
    /// creates one, the search they did not know existed, the tools tab, the palette, and the
    /// settings page - which is also where the tour can be replayed, so the last step teaches the
    /// way back.
    ///
    /// A table rather than a switch, because a step now has to answer "what does this point at"
    /// as well as "what does it say", and because adding one should not mean editing three places.
    /// </remarks>
    public static readonly IReadOnlyList<Step> Steps =
    [
        new("OnboardingStep1Title", "OnboardingStep1Desc", "SessionTreeView", ShellTab.Sessions),
        new("OnboardingStepAddTitle", "OnboardingStepAddDesc", "AddButton", ShellTab.Sessions),
        new("OnboardingStepSearchTitle", "OnboardingStepSearchDesc", "Mw_FilterBox", ShellTab.Sessions),
        new("OnboardingStep2Title", "OnboardingStep2Desc", "TabTools", null),
        // Points at the magnifier rather than only naming the shortcut: a step that teaches a
        // keystroke and nothing else leaves anyone who misses it with no way in.
        new("OnboardingStep3Title", "OnboardingStep3Desc", "QuickConnectButton", null),
        new("OnboardingStepSettingsTitle", "OnboardingStepSettingsDesc", "TabSettings", null),
    ];

    /// <summary>Total number of steps in the onboarding flow.</summary>
    public static int StepCount => Steps.Count;

    private readonly LocalizationManager _localizer;
    private readonly IConfigManager _configManager;
    private AppSettings? _settings;
    private bool _completionInProgress;

    /// <summary>
    /// Creates a new onboarding view-model. Call <see cref="Attach"/> with
    /// the live <see cref="AppSettings"/> instance before invoking
    /// <see cref="Start"/>.
    /// </summary>
    public OnboardingFlowViewModel(LocalizationManager localizer, IConfigManager configManager)
    {
        _localizer = localizer;
        _configManager = configManager;
    }

    /// <summary>Zero-based index of the currently displayed step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepIndicatorStates))]
    [NotifyPropertyChangedFor(nameof(CurrentStepDefinition))]
    private int _currentStep;

    /// <summary>Localized title of the current step.</summary>
    [ObservableProperty]
    private string _titleText = string.Empty;

    /// <summary>Localized description shown beneath the title.</summary>
    [ObservableProperty]
    private string _subtitleText = string.Empty;

    /// <summary>Localized label of the Skip button.</summary>
    [ObservableProperty]
    private string _skipLabel = string.Empty;

    /// <summary>Localized label of the Next/Get-Started button.</summary>
    [ObservableProperty]
    private string _nextLabel = string.Empty;

    /// <summary>Whether the overlay should currently be displayed.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Localized recovery guidance shown when completion cannot be persisted.</summary>
    [ObservableProperty]
    private string _completionErrorText = string.Empty;

    /// <summary>
    /// Step indicator states bound by the view's dot ItemsControl. The
    /// active step is <c>true</c>, all other entries are <c>false</c>.
    /// Recomputed automatically whenever <see cref="CurrentStep"/> changes.
    /// </summary>
    public IReadOnlyList<bool> StepIndicatorStates
    {
        get
        {
            var dots = new bool[StepCount];
            if (CurrentStep >= 0 && CurrentStep < StepCount)
            {
                dots[CurrentStep] = true;
            }

            return dots;
        }
    }

    /// <summary>
    /// Raised after a step is completed via Next, with the zero-based index
    /// of the step that was just finished. The view uses this to perform
    /// any navigation associated with the completed step.
    /// </summary>
    public event EventHandler<int>? StepCompleted;

    /// <summary>
    /// Raised once after the flow finishes (Next on the final step, Skip,
    /// or Escape) and persistence has succeeded.
    /// </summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Binds the view-model to the live application settings so completion
    /// can be persisted on the same in-memory instance the rest of the app
    /// observes.
    /// </summary>
    public void Attach(AppSettings? settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Resets the flow to step 0, refreshes localized labels, and shows the
    /// overlay. Call this once after attaching the live settings instance.
    /// </summary>
    public void Start()
    {
        CurrentStep = 0;
        CompletionErrorText = string.Empty;
        IsVisible = true;
        RefreshLabels();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        var completedStep = CurrentStep;
        StepCompleted?.Invoke(this, completedStep);

        if (completedStep < StepCount - 1)
        {
            CurrentStep = completedStep + 1;
        }
        else
        {
            await CompleteAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task SkipAsync() => CompleteAsync();

    [RelayCommand]
    private Task EscapeAsync() => CompleteAsync();

    private async Task CompleteAsync()
    {
        if (_completionInProgress || !IsVisible)
        {
            return;
        }

        _completionInProgress = true;
        try
        {
            if (_settings is null)
            {
                FileLogger.Warn("Onboarding completion skipped because settings are not attached.");
                CompletionErrorText = _localizer["OnboardingCompletionSaveFailed"];
                return;
            }

            CompletionErrorText = string.Empty;
            bool persisted = await TryPersistCompletionAsync().ConfigureAwait(true);
            if (!persisted)
            {
                CompletionErrorText = _localizer["OnboardingCompletionSaveFailed"];
                return;
            }

            _settings.OnboardingCompleted = true;
            IsVisible = false;
            Completed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _completionInProgress = false;
        }
    }

    private async Task<bool> TryPersistCompletionAsync()
    {
        try
        {
            await _configManager.MergeSettingAsync(
                settings => settings.OnboardingCompleted = true).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Onboarding completion persistence failed", ex);
        }

        try
        {
            AppSettings persistedSettings = await _configManager.LoadSettingsAsync().ConfigureAwait(true);
            return persistedSettings.OnboardingCompleted;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Onboarding completion state reload failed", ex);
            return false;
        }
    }

    partial void OnCurrentStepChanged(int value)
    {
        RefreshLabels();
    }

    /// <summary>The step currently displayed, or null while the index is out of range.</summary>
    public Step? CurrentStepDefinition =>
        CurrentStep >= 0 && CurrentStep < Steps.Count ? Steps[CurrentStep] : null;

    private void RefreshLabels()
    {
        if (CurrentStepDefinition is { } step)
        {
            TitleText = _localizer[step.TitleKey];
            SubtitleText = _localizer[step.BodyKey];
        }

        SkipLabel = _localizer["OnboardingBtnSkip"];
        NextLabel = CurrentStep < StepCount - 1
            ? _localizer["OnboardingBtnNext"]
            : _localizer["OnboardingBtnGetStarted"];
    }
}
