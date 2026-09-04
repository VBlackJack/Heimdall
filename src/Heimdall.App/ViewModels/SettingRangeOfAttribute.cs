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
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Validates a settings field against the range its <see cref="AppSettings"/> property declares.
/// </summary>
/// <remarks>
/// <para>Replaces the <c>[Range(min, max, ErrorMessage = "...")]</c> annotations the settings
/// screen used to carry, each of which spelled the bound a second time and half of which had
/// drifted from the loader's. The bound is read from <see cref="SettingRanges"/> at validation
/// time, so the screen refuses exactly what the loader would warn about, and accepts the declared
/// "off" value the old annotations could not express.</para>
/// <para>The error it reports is the settings property's name, not a sentence: the view model turns
/// that token into the localized message and formats the declared numbers into it, so the
/// translations carry no numbers of their own either.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SettingRangeOfAttribute(string settingsPropertyName) : ValidationAttribute
{
    /// <summary>The <see cref="AppSettings"/> property whose declared range applies.</summary>
    public string SettingsPropertyName { get; } = settingsPropertyName;

    /// <summary>The declared range, read from the settings type.</summary>
    public SettingRange Range => SettingRanges.Of(SettingsPropertyName);

    /// <inheritdoc />
    /// <remarks>
    /// A null value passes: the nullable per-profile overrides mean "inherit", and inheriting is
    /// not a number to bound.
    /// </remarks>
    protected override System.ComponentModel.DataAnnotations.ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value is not int number)
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        return Range.Accepts(number)
            ? System.ComponentModel.DataAnnotations.ValidationResult.Success
            : new System.ComponentModel.DataAnnotations.ValidationResult(SettingsPropertyName);
    }
}
