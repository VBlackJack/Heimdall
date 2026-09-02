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
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Password policy checker that analyzes password strength against configurable policies
/// (NIST 800-63B, ANSSI, Custom). Provides real-time analysis with detailed criterion breakdown.
/// </summary>
public partial class PasswordAuditView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _passwordVisible;
    private bool _syncingPassword;
    private bool _disposed;

    // ── Policy definitions ──────────────────────────────────────────────

    private sealed record PasswordPolicy(
        int MinLength,
        bool RequireUpper,
        bool RequireLower,
        bool RequireDigit,
        bool RequireSymbol,
        int MinEntropy);

    private static readonly Dictionary<string, PasswordPolicy> Policies = new()
    {
        ["nist"] = new(8, false, false, false, false, 30),
        ["anssi"] = new(12, true, true, true, true, 50),
        ["custom"] = new(8, false, false, false, false, 0),
    };

    private static readonly string[] PolicyKeys = ["nist", "anssi", "custom"];

    // ── Analysis result model ───────────────────────────────────────────

    private sealed record CriterionResult(string LabelKey, bool Passed, string DetailKey, object[]? DetailArgs = null);

    private sealed record PasswordAnalysis(
        int Score,
        string ScoreLabelKey,
        double Entropy,
        bool IsCommon,
        List<string> PatternKeys,
        List<CriterionResult> Criteria);

    // ── Common passwords (top 100) ──────────────────────────────────────

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "123456", "12345678", "qwerty", "abc123", "monkey", "1234567",
        "letmein", "trustno1", "dragon", "baseball", "iloveyou", "master", "sunshine",
        "ashley", "bailey", "shadow", "123123", "654321", "superman", "qazwsx",
        "michael", "football", "password1", "password123", "admin", "welcome",
        "charlie", "donald", "login", "princess", "qwerty123", "solo", "passw0rd",
        "starwars", "121212", "flower", "hottie", "loveme", "zaq1zaq1", "hello",
        "monkey123", "dragon123", "master123", "qwerty1", "mustang", "access",
        "letmein1", "batman", "111111", "000000", "1234", "12345", "123456789",
        "1234567890", "password12", "iloveu", "sunshine1", "princess1", "football1",
        "charlie1", "shadow1", "michael1", "baseball1", "buster", "daniel",
        "jessica", "pepper", "harley", "robert", "thomas", "soccer", "hockey",
        "ranger", "killer", "george", "andrew", "andrea", "joshua", "matrix",
        "whatever", "cheese", "amanda", "summer", "ginger", "cookie", "hunter",
        "jennifer", "jordan", "sparky", "abcdef", "yankees", "dallas", "austin",
        "taylor", "corvette", "merlin", "compaq", "bigdog", "cowboy", "camaro",
        "jordan23", "london", "jasper", "apple", "brandy", "mercedes", "thunder",
        "tigers", "porsche",
    };

    // ── Keyboard pattern sequences ──────────────────────────────────────

    private static readonly string[] KeyboardPatterns =
    [
        "qwerty", "qwertz", "azerty", "qwert", "asdf", "zxcv",
        "1234", "2345", "3456", "4567", "5678", "6789", "7890",
        "abcd", "bcde", "cdef", "defg", "efgh", "fghi", "ghij",
        "hijk", "ijkl", "jklm", "klmn", "lmno", "mnop", "nopq",
        "opqr", "pqrs", "qrst", "rstu", "stuv", "tuvw", "uvwx",
        "vwxy", "wxyz",
    ];

    public PasswordAuditView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        ApplyLocalization();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            PwdInput.Focus();
        });
    }

    // ── Localization ────────────────────────────────────────────────────

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolPwdAuditTitle");
        LblPassword.Text = L("ToolPwdAuditInput");
        LblPolicy.Text = L("ToolPwdAuditPolicy");
        LblStrength.Text = L("ToolPwdAuditStrength");
        EmptyStateText.Text = L("ToolPwdAuditEmptyState");

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        System.Windows.Automation.AutomationProperties.SetName(PwdInput, L("ToolPwdAuditInput"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPasswordVisible, L("ToolPwdAuditInput"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPolicy, L("ToolPwdAuditPolicy"));

        TxtPasswordVisible.Tag = L("ToolWatermarkPassword");

        UpdateToggleButtonLabel();

        // Populate policy ComboBox
        CmbPolicy.Items.Clear();
        CmbPolicy.Items.Add(L("ToolPwdAuditPolicyNist"));
        CmbPolicy.Items.Add(L("ToolPwdAuditPolicyAnssi"));
        CmbPolicy.Items.Add(L("ToolPwdAuditPolicyCustom"));
        CmbPolicy.SelectedIndex = 0;
    }

    private void UpdateToggleButtonLabel()
    {
        var key = _passwordVisible ? "ToolPwdAuditBtnHide" : "ToolPwdAuditBtnShow";
        System.Windows.Automation.AutomationProperties.SetName(BtnToggleVisibility, L(key));
        BtnToggleVisibility.ToolTip = L(key);
    }

    // ── Event handlers ──────────────────────────────────────────────────

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpPWDAUDIT").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnTogglePasswordVisibility(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        _syncingPassword = true;

        if (_passwordVisible)
        {
            TxtPasswordVisible.Text = PwdInput.Password;
            TxtPasswordVisible.Visibility = Visibility.Visible;
            PwdInput.Visibility = Visibility.Collapsed;
            BtnToggleVisibility.Content = "\uED1A"; // Eye-off icon
        }
        else
        {
            PwdInput.Password = TxtPasswordVisible.Text;
            PwdInput.Visibility = Visibility.Visible;
            TxtPasswordVisible.Visibility = Visibility.Collapsed;
            BtnToggleVisibility.Content = "\uE7B3"; // Eye icon
        }

        UpdateToggleButtonLabel();
        _syncingPassword = false;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        RunAnalysis();
    }

    private void OnPasswordTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        RunAnalysis();
    }

    private void OnPolicySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RunAnalysis();
    }

    // ── Analysis engine ─────────────────────────────────────────────────

    private string GetCurrentPassword()
    {
        return _passwordVisible
            ? (TxtPasswordVisible?.Text ?? string.Empty)
            : (PwdInput?.Password ?? string.Empty);
    }

    private PasswordPolicy GetSelectedPolicy()
    {
        var index = CmbPolicy?.SelectedIndex ?? 0;
        if (index < 0 || index >= PolicyKeys.Length) index = 0;
        return Policies[PolicyKeys[index]];
    }

    private void RunAnalysis()
    {
        var password = GetCurrentPassword();

        if (string.IsNullOrEmpty(password))
        {
            PanelStrength.Visibility = Visibility.Collapsed;
            PanelCriteria.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;
        PanelStrength.Visibility = Visibility.Visible;
        PanelCriteria.Visibility = Visibility.Visible;

        var policy = GetSelectedPolicy();
        var analysis = AnalyzePassword(password, policy);
        UpdateStrengthBar(analysis);
        UpdateCriteriaDisplay(analysis);
    }

    private PasswordAnalysis AnalyzePassword(string password, PasswordPolicy policy)
    {
        var criteria = new List<CriterionResult>();
        var entropy = CalculateEntropy(password);
        var isCommon = CheckCommonPasswords(password);
        var patternKeys = DetectPatterns(password);

        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));

        // Length criterion
        bool lengthPass = password.Length >= policy.MinLength;
        criteria.Add(new CriterionResult(
            "ToolPwdAuditLength",
            lengthPass,
            "ToolPwdAuditLengthDetail",
            [password.Length, policy.MinLength]));

        // Uppercase criterion
        if (policy.RequireUpper)
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditUppercase",
                hasUpper,
                hasUpper ? "ToolPwdAuditPass" : "ToolPwdAuditFail"));
        }
        else
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditUppercase",
                hasUpper,
                hasUpper ? "ToolPwdAuditPass" : "ToolPwdAuditWarn"));
        }

        // Lowercase criterion
        if (policy.RequireLower)
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditLowercase",
                hasLower,
                hasLower ? "ToolPwdAuditPass" : "ToolPwdAuditFail"));
        }
        else
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditLowercase",
                hasLower,
                hasLower ? "ToolPwdAuditPass" : "ToolPwdAuditWarn"));
        }

        // Digits criterion
        if (policy.RequireDigit)
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditDigits",
                hasDigit,
                hasDigit ? "ToolPwdAuditPass" : "ToolPwdAuditFail"));
        }
        else
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditDigits",
                hasDigit,
                hasDigit ? "ToolPwdAuditPass" : "ToolPwdAuditWarn"));
        }

        // Symbols criterion
        if (policy.RequireSymbol)
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditSymbols",
                hasSymbol,
                hasSymbol ? "ToolPwdAuditPass" : "ToolPwdAuditFail"));
        }
        else
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditSymbols",
                hasSymbol,
                hasSymbol ? "ToolPwdAuditPass" : "ToolPwdAuditWarn"));
        }

        // Entropy criterion
        bool entropyPass = policy.MinEntropy <= 0 || entropy >= policy.MinEntropy;
        criteria.Add(new CriterionResult(
            "ToolPwdAuditEntropy",
            entropyPass,
            "ToolPwdAuditEntropyBits",
            [Math.Round(entropy, 1)]));

        // Common password check
        criteria.Add(new CriterionResult(
            "ToolPwdAuditCommon",
            !isCommon,
            isCommon ? "ToolPwdAuditInCommonList" : "ToolPwdAuditNotInCommonList"));

        // Pattern detection
        if (patternKeys.Count > 0)
        {
            var detail = string.Join(", ", patternKeys.Select(L));
            criteria.Add(new CriterionResult(
                "ToolPwdAuditPatterns",
                false,
                detail));
        }
        else
        {
            criteria.Add(new CriterionResult(
                "ToolPwdAuditPatterns",
                true,
                "ToolPwdAuditPass"));
        }

        int score = CalculateScore(password, policy, entropy, isCommon, patternKeys.Count,
            hasUpper, hasLower, hasDigit, hasSymbol);

        string scoreLabelKey = score switch
        {
            < 25 => "ToolPwdAuditScoreWeak",
            < 50 => "ToolPwdAuditScoreFair",
            < 75 => "ToolPwdAuditScoreGood",
            _ => "ToolPwdAuditScoreStrong",
        };

        return new PasswordAnalysis(score, scoreLabelKey, entropy, isCommon, patternKeys, criteria);
    }

    /// <summary>
    /// Calculates Shannon entropy of the password based on its character pool size.
    /// </summary>
    private static double CalculateEntropy(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        int poolSize = 0;
        bool hasLower = false, hasUpper = false, hasDigit = false, hasSymbol = false;

        foreach (var c in password)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }

        if (hasLower) poolSize += 26;
        if (hasUpper) poolSize += 26;
        if (hasDigit) poolSize += 10;
        if (hasSymbol) poolSize += 33;

        if (poolSize == 0) return 0;

        return password.Length * Math.Log2(poolSize);
    }

    /// <summary>
    /// Checks whether the password appears in the common passwords list.
    /// Also checks common substitutions (e.g. p@ssw0rd).
    /// </summary>
    private static bool CheckCommonPasswords(string password)
    {
        if (CommonPasswords.Contains(password))
            return true;

        // Normalize common leet-speak substitutions and re-check
        var normalized = password
            .Replace('@', 'a')
            .Replace('0', 'o')
            .Replace('1', 'l')
            .Replace('3', 'e')
            .Replace('$', 's')
            .Replace('!', 'i')
            .Replace('5', 's')
            .Replace('7', 't');

        return CommonPasswords.Contains(normalized);
    }

    /// <summary>
    /// Detects common patterns: keyboard walks, sequential characters, repeated characters.
    /// Returns i18n keys for each detected pattern type.
    /// </summary>
    private static List<string> DetectPatterns(string password)
    {
        var patterns = new List<string>();
        var lower = password.ToLowerInvariant();

        // Keyboard pattern detection
        foreach (var pattern in KeyboardPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
            {
                patterns.Add("ToolPwdAuditPatternKeyboard");
                break;
            }
        }

        // Sequential character detection (3+ ascending or descending)
        if (HasSequentialChars(lower))
        {
            patterns.Add("ToolPwdAuditPatternSequence");
        }

        // Repeated character detection (3+ of the same char)
        if (HasRepeatedChars(lower))
        {
            patterns.Add("ToolPwdAuditPatternRepeat");
        }

        return patterns;
    }

    private static bool HasSequentialChars(string s)
    {
        const int MinSequenceLength = 3;
        if (s.Length < MinSequenceLength) return false;

        int ascCount = 1;
        int descCount = 1;

        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == s[i - 1] + 1) { ascCount++; } else { ascCount = 1; }
            if (s[i] == s[i - 1] - 1) { descCount++; } else { descCount = 1; }

            if (ascCount >= MinSequenceLength || descCount >= MinSequenceLength)
                return true;
        }

        return false;
    }

    private static bool HasRepeatedChars(string s)
    {
        const int MinRepeatLength = 3;
        if (s.Length < MinRepeatLength) return false;

        int repeatCount = 1;

        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == s[i - 1]) { repeatCount++; } else { repeatCount = 1; }

            if (repeatCount >= MinRepeatLength)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates an overall strength score (0-100) from all analysis dimensions.
    /// </summary>
    private static int CalculateScore(
        string password,
        PasswordPolicy policy,
        double entropy,
        bool isCommon,
        int patternCount,
        bool hasUpper,
        bool hasLower,
        bool hasDigit,
        bool hasSymbol)
    {
        double score = 0;

        // Length contribution (up to 30 points)
        double lengthRatio = Math.Min((double)password.Length / Math.Max(policy.MinLength * 2, 16), 1.0);
        score += lengthRatio * 30;

        // Entropy contribution (up to 30 points)
        double entropyTarget = policy.MinEntropy > 0 ? policy.MinEntropy * 1.5 : 60;
        double entropyRatio = Math.Min(entropy / entropyTarget, 1.0);
        score += entropyRatio * 30;

        // Character diversity contribution (up to 20 points)
        int classCount = (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        score += (classCount / 4.0) * 20;

        // Penalty for common password (up to -30 points)
        if (isCommon) score -= 30;

        // Penalty for patterns (up to -20 points)
        score -= patternCount * 10;

        // Bonus for exceeding minimum length significantly (up to 10 points)
        if (password.Length > policy.MinLength)
        {
            double excessRatio = Math.Min((double)(password.Length - policy.MinLength) / policy.MinLength, 1.0);
            score += excessRatio * 10;
        }

        // Penalty for not meeting required criteria
        if (policy.RequireUpper && !hasUpper) score -= 10;
        if (policy.RequireLower && !hasLower) score -= 10;
        if (policy.RequireDigit && !hasDigit) score -= 10;
        if (policy.RequireSymbol && !hasSymbol) score -= 10;

        return Math.Clamp((int)Math.Round(score), 0, 100);
    }

    // ── UI update helpers ───────────────────────────────────────────────

    private void UpdateStrengthBar(PasswordAnalysis analysis)
    {
        var score = analysis.Score;

        StrengthBarFillColumn.Width = new GridLength(score, GridUnitType.Star);
        StrengthBarEmptyColumn.Width = new GridLength(100 - score, GridUnitType.Star);

        // Color gradient: red → orange → yellow → green
        Brush barBrush = score switch
        {
            < 25 => FindBrush("ErrorBrush"),
            < 50 => FindBrush("WarningBrush"),
            < 75 => FindBrush("WarningTextBrush"),
            _ => FindBrush("SuccessBrush"),
        };
        StrengthBar.Background = barBrush;

        StrengthScoreLabel.Text = $"{score}/100 - {L(analysis.ScoreLabelKey)}";
        StrengthScoreLabel.Foreground = barBrush;
    }

    private void UpdateCriteriaDisplay(PasswordAnalysis analysis)
    {
        PanelCriteria.Children.Clear();

        foreach (var criterion in analysis.Criteria)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            // Pass/fail icon
            var iconBlock = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = (double)FindResource("FontSizeBody"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 20,
                Text = criterion.Passed ? "\uE73E" : "\uE711", // Checkmark or X
                Foreground = criterion.Passed
                    ? FindBrush("SuccessBrush")
                    : FindBrush("ErrorBrush"),
            };
            row.Children.Add(iconBlock);

            // Detail text (right-aligned)
            var detailText = criterion.DetailArgs is not null
                ? string.Format(L(criterion.DetailKey), criterion.DetailArgs)
                : L(criterion.DetailKey);

            // If the detail key did not resolve to a localized string (pattern case),
            // use it as-is since DetectPatterns already built the combined string.
            if (criterion.LabelKey == "ToolPwdAuditPatterns" && !criterion.Passed && criterion.DetailArgs is null)
            {
                // The detail is already a pre-built comma-separated localized string
                detailText = criterion.DetailKey;
            }

            var detailBlock = new TextBlock
            {
                FontSize = (double)FindResource("FontSizeCaption"),
                Foreground = FindBrush("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            detailBlock.Text = detailText;
            DockPanel.SetDock(detailBlock, Dock.Right);
            row.Children.Add(detailBlock);

            // Label
            var labelBlock = new TextBlock
            {
                Text = L(criterion.LabelKey),
                FontSize = (double)FindResource("FontSizeBody"),
                Foreground = FindBrush("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0),
            };
            row.Children.Add(labelBlock);

            PanelCriteria.Children.Add(row);
        }
    }

    private Brush FindBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private string L(string key) => _localizer?[key] ?? key;

    // ── IDisposable ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PwdInput.Clear();
        TxtPasswordVisible.Clear();
        GC.SuppressFinalize(this);
    }
}
