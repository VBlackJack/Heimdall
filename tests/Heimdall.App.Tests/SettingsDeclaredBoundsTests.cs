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

using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace Heimdall.App.Tests;

/// <summary>
/// The settings screen, its messages and its translations derive every bound from the setting's
/// one declaration.
/// </summary>
/// <remarks>
/// <para>Before this, a bound was spelled four times: in the validator, in the screen's own range
/// attribute, in the map from that attribute's English sentence to a locale key, and inside the
/// English and French translations. Two of the four disagreed on master. What is measured here is
/// that no copy is left: the screen's attribute reads the declaration, the message is a template,
/// the translations carry no number, and the one policy class that still holds its own constants
/// agrees with the declaration.</para>
/// </remarks>
public sealed class SettingsDeclaredBoundsTests
{
    private const string ValidatorRelativePath = "src/Heimdall.Core/Configuration/SchemaValidator.cs";
    private const string SettingsViewModelRelativePath = "src/Heimdall.App/ViewModels/SettingsViewModel.cs";
    private const string HealthMonitorRelativePath = "src/Heimdall.App/Services/SessionHealthMonitor.cs";

    [Theory]
    [InlineData(nameof(AppSettings.TunnelEstablishmentDelayMs))]
    [InlineData(nameof(AppSettings.AntiIdleIntervalSeconds))]
    [InlineData(nameof(AppSettings.RdpConnectWatchdogTimeoutMs))]
    [InlineData(nameof(AppSettings.SessionHealthMaxConcurrent))]
    public void Attribute_RefusesJustOutsideTheDeclaredEnds_AndAcceptsTheEndsAndOff(string propertyName)
    {
        SettingRange range = SettingRanges.Of(propertyName);
        SettingRangeOfAttribute attribute = new(propertyName);
        ValidationContext context = new(new object());

        Assert.NotEqual(ValidationResult.Success, attribute.GetValidationResult(range.Min - 1, context));
        Assert.NotEqual(ValidationResult.Success, attribute.GetValidationResult(range.Max + 1, context));
        Assert.Equal(ValidationResult.Success, attribute.GetValidationResult(range.Min, context));
        Assert.Equal(ValidationResult.Success, attribute.GetValidationResult(range.Max, context));
        if (range.DisabledValue is int off)
        {
            Assert.Equal(ValidationResult.Success, attribute.GetValidationResult(off, context));
        }

        // The error names the setting, which is what the view model turns into a message.
        Assert.Equal(propertyName, attribute.GetValidationResult(range.Max + 1, context)!.ErrorMessage);
    }

    [Fact]
    public void Attribute_LetsANullOverrideThrough_BecauseNullMeansInherit()
    {
        SettingRangeOfAttribute attribute = new(nameof(AppSettings.RdpResizeEnableDelayMs));
        Assert.Equal(ValidationResult.Success, attribute.GetValidationResult(null, new ValidationContext(new object())));
    }

    // Every numeric settings field the screen validates is bound by a declaration, and each of
    // those declarations exists. A field left on the old [Range] would spell a bound again.
    [Fact]
    public void EveryRangedFieldOfTheSettingsScreen_IsBoundByADeclaration_AndNoneByARangeAttribute()
    {
        FieldInfo[] fields = typeof(SettingsViewModel).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        List<string> declared = [];
        foreach (FieldInfo field in fields)
        {
            Assert.Null(field.GetCustomAttribute<RangeAttribute>());
            SettingRangeOfAttribute? bound = field.GetCustomAttribute<SettingRangeOfAttribute>();
            if (bound is null)
            {
                continue;
            }

            // Resolves, or throws KeyNotFoundException: the declaration must exist.
            _ = bound.Range;
            declared.Add(bound.SettingsPropertyName);
        }

        // The floor: the nineteen [Range] sites and the two zero-or-range custom validations the
        // screen used to carry. A scan that found fewer would be measuring the wrong type.
        Assert.True(declared.Count >= 21, $"only {declared.Count} settings fields are bound by a declaration");
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());
    }

    // The translations are templates: the numbers come from the declaration at display time.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ValidationMessages_CarryNoNumberOfTheirOwn_InEitherLanguage(string locale)
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "locales", locale + ".json");
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        List<string> offenders = [];
        int templates = 0;
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            if (!entry.Name.StartsWith("ValidationSettings", StringComparison.Ordinal)
                && entry.Name != "ValidationRdpResizeEnableDelayRange")
            {
                continue;
            }

            string text = entry.Value.GetString() ?? string.Empty;
            // Placeholders are the bounds arriving from the declaration; a lone 0 is the "off"
            // value a sentence may name ("enter 0 to disable"), which is not a bound.
            string withoutPlaceholders = Regex.Replace(text, @"\{\d\}|\b0\b", string.Empty);
            if (Regex.IsMatch(withoutPlaceholders, @"\d"))
            {
                offenders.Add(entry.Name);
            }

            if (text.Contains("{0}", StringComparison.Ordinal))
            {
                templates++;
            }
        }

        Assert.True(offenders.Count == 0, $"{locale}: numbers written into {string.Join(", ", offenders)}");
        Assert.True(templates >= 22, $"{locale}: only {templates} templated messages found");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ScreenMessage_FormatsTheDeclaredBoundsIntoTheTemplate(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        SettingRange range = SettingRanges.Of(nameof(AppSettings.TunnelEstablishmentDelayMs));

        string message = localizer.Format("ValidationSettingsTunnelDelay", range.Min, range.Max);

        Assert.Contains(range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.Contains(range.Max.ToString(System.Globalization.CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
    }

    // The one policy class that still holds constants of its own agrees with the declaration.
    // Shared decision, not shared text: if the policy moves, this is red, and the declaration is
    // where the answer is.
    [Fact]
    public void ConnectWatchdogPolicy_AgreesWithTheDeclaration()
    {
        SettingRange range = SettingRanges.Of(nameof(AppSettings.RdpConnectWatchdogTimeoutMs));

        Assert.Equal(RdpConnectWatchdogPolicy.MinTimeoutMs, range.Min);
        Assert.Equal(RdpConnectWatchdogPolicy.MaxTimeoutMs, range.Max);
        Assert.Equal(RdpConnectWatchdogPolicy.DisabledTimeoutMs, range.DisabledValue);
    }

    // Absence guards, in the shape the source-reading meta-guard counts: the validator spells no
    // settings bound by hand, and the health monitor's floors are read from the registry.
    [Fact]
    public void TheValidator_SpellsNoSettingsBoundByHand()
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), ValidatorRelativePath)));

        Assert.False(
            logic.Contains("ValidateRange(errors, settings.", StringComparison.Ordinal),
            "SchemaValidator spells an AppSettings bound by hand again; declare it with [SettingRange] instead.");
        Assert.False(
            logic.Contains("ValidateRange(errors, server.RdpColorDepth", StringComparison.Ordinal),
            "SchemaValidator spells the colour depth bound by hand again.");
    }

    [Fact]
    public void TheHealthMonitor_ReadsItsFloorsFromTheRegistry()
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), HealthMonitorRelativePath)));

        Assert.False(logic.Contains("Math.Max(15,", StringComparison.Ordinal), "the interval floor is spelled by hand again");
        Assert.False(logic.Contains("Math.Max(1,", StringComparison.Ordinal), "the concurrency floor is spelled by hand again");
    }

    [Fact]
    public void TheSettingsScreen_KeepsNoSentenceKeyedMapForRanges()
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), SettingsViewModelRelativePath)));

        // A literal English sentence as a dictionary key is blanked with the literal; what must be
        // absent is the attribute that produced those sentences.
        Assert.False(logic.Contains("[Range(", StringComparison.Ordinal), "a [Range] attribute is back on the settings screen");
    }
}
