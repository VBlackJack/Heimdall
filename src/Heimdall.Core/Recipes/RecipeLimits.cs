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

namespace Heimdall.Core.Recipes;

/// <summary>
/// Every cap and bound of the recipe format, in one place. A recipe is untrusted input: each count
/// and length is bounded so that a hostile file cannot flood a preview, a list or a command line.
/// </summary>
public static class RecipeLimits
{
    /// <summary>Largest recipe file accepted, in bytes. Checked before any parsing.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Deepest JSON nesting accepted.</summary>
    public const int MaxJsonDepth = 8;

    /// <summary>Most errors reported for one file; later ones are dropped.</summary>
    public const int MaxReportedErrors = 50;

    /// <summary>Longest fragment of offending text carried inside an error, after it was made safe.</summary>
    public const int MaxFragmentLength = 64;

    /// <summary>Longest half (author or name) of a recipe id.</summary>
    public const int MaxIdPartLength = 32;

    /// <summary>Longest semantic version text.</summary>
    public const int MaxVersionLength = 64;

    /// <summary>Longest author text.</summary>
    public const int MaxAuthorLength = 64;

    /// <summary>Longest recipe name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Longest recipe description.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>Longest label of an input.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Longest program text (a bare file name or an absolute path).</summary>
    public const int MaxProgramLength = 260;

    /// <summary>Most tokens in a recipe, counting bare tokens, group members and <c>whenTrue</c> tokens.</summary>
    public const int MaxTokens = 64;

    /// <summary>Longest token, default value or choice value.</summary>
    public const int MaxTokenLength = 512;

    /// <summary>Most inputs in a recipe.</summary>
    public const int MaxInputs = 16;

    /// <summary>Longest input name.</summary>
    public const int MaxInputNameLength = 32;

    /// <summary>Most values in a closed choice list.</summary>
    public const int MaxChoices = 64;

    /// <summary>Longest value of a closed choice list.</summary>
    public const int MaxChoiceLength = 128;

    /// <summary>Longest regular expression of a <c>string</c> input.</summary>
    public const int MaxPatternLength = 256;

    /// <summary>Time a pattern may spend on one value before the value is refused.</summary>
    public static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Largest <c>maxLength</c> a <c>string</c> input may declare.</summary>
    public const int MaxStringValueLength = 1024;

    /// <summary>Length cap applied to a <c>string</c> input that declares none.</summary>
    public const int DefaultStringMaxLength = 256;

    /// <summary>Most translations in the <c>labels</c> map.</summary>
    public const int MaxLabelLanguages = 16;

    /// <summary>Longest language tag.</summary>
    public const int MaxLanguageTagLength = 35;

    /// <summary>Shortest first subtag of a language tag.</summary>
    public const int MinLanguageSubtagLength = 2;

    /// <summary>Longest subtag of a language tag.</summary>
    public const int MaxLanguageSubtagLength = 8;

    /// <summary>Most entries in <c>successExitCodes</c>.</summary>
    public const int MaxSuccessExitCodes = 16;

    /// <summary>Shortest run timeout, in seconds.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Longest run timeout, in seconds.</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>Run timeout used when the file declares none, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>Longest value accepted for the <c>int</c> and <c>number</c> types, in characters.</summary>
    public const int MaxNumericValueLength = 64;

    /// <summary>Most bytes kept from the output of a run; the rest is read and discarded.</summary>
    public const int MaxCaptureBytes = 1024 * 1024;

    /// <summary>How long the readers may keep draining after the process ended, in milliseconds.</summary>
    public const int DrainGraceMilliseconds = 2000;

    /// <summary>How long to wait for a killed process to be gone, in milliseconds.</summary>
    public const int KillWaitMilliseconds = 5000;
}
