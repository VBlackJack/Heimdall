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

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// The names of the samples of the version 1 freeze, folder by folder. A sample can be added to the lock and this
/// list stays as it is; a sample of the list can be neither removed nor renamed, because the test that reads it
/// would fail. Each name stands for two files, the recipe and its expected file.
/// </summary>
internal static class FormatLockFrozen
{
    /// <summary>The samples of the accepted folder at the freeze.</summary>
    public static readonly string[] Accepted =
    [
        "001-minimal",
        "002-every-property",
        "003-defaults-as-json-numbers",
        "004-defaults-as-strings",
        "005-bool-defaults-as-json-booleans",
        "006-bool-with-when-true-only",
        "007-bool-with-when-false-only",
        "008-bool-with-both-lists",
        "009-groups-dropped-when-an-optional-field-is-empty",
        "010-host-and-port-in-tokens-and-switch-arguments",
        "011-escaped-braces",
        "012-output-text",
        "013-output-csv",
        "014-output-json",
        "015-encoding-utf8",
        "016-encoding-oem",
        "017-encoding-ansi",
        "018-encoding-utf16le",
        "019-stderr-merge",
        "020-stderr-separate",
        "021-csv-separator-comma",
        "022-csv-separator-semicolon",
        "023-csv-separator-tab",
        "024-icon-known-to-this-version",
        "025-icon-unknown-to-this-version",
        "026-bounds-on-an-int",
        "027-bounds-on-a-number",
        "028-translations-with-hints-and-regional-variants",
        "029-every-limit-at-its-cap",
        "030-the-lowest-limits",
        "031-utf8-byte-order-mark",
        "032-non-ascii-display-text-written-as-utf8",
        "033-example-of-the-documentation-with-non-ascii-text",
        "034-non-ascii-display-text-written-as-escapes",
        "035-inputs-inside-switch-arguments",
        "036-minus-zero-as-a-default-and-as-bounds",
        "037-int32-edges-as-bounds-and-defaults",
        "038-number-bounds-with-a-long-fraction",
    ];

    /// <summary>The samples of the refused folder at the freeze.</summary>
    public static readonly string[] Refused =
    [
        "001-required-on-a-bool-true",
        "002-required-on-a-bool-false",
        "003-an-input-no-argument-uses",
        "004-a-bool-placed-inside-a-group",
        "005-required-together-with-a-default",
        "006-optional-input-in-when-true",
        "007-optional-input-in-when-false",
        "008-stderr-merge-with-csv",
        "009-stderr-merge-with-json",
        "010-min-above-max-on-an-int",
        "011-min-above-max-on-a-number",
        "012-default-below-min",
        "013-default-above-max",
        "014-bound-that-is-not-a-whole-number",
        "015-bound-written-with-an-exponent",
        "016-bounds-on-a-string",
        "017-switch-without-arguments",
        "018-empty-when-false",
        "019-when-false-on-a-string",
        "020-icon-key-in-capitals",
        "021-icon-key-with-two-hyphens",
        "022-hint-with-a-line-break",
        "023-hint-over-its-cap",
        "024-csv-separator-with-a-text-output",
        "025-csv-separator-comma-with-a-json-output",
        "026-the-old-labels-block",
        "027-the-old-fields-name",
        "028-hint-of-a-field-that-does-not-exist",
        "029-label-of-a-field-that-does-not-exist",
        "030-language-tags-that-differ-only-by-case",
        "031-exit-code-listed-twice",
        "032-int-default-with-a-leading-plus",
        "033-number-default-with-an-exponent",
        "034-int-default-with-a-leading-zero",
        "035-big-endian-encoding",
        "036-a-newer-schema-version",
        "037-an-unknown-property",
        "038-a-url-in-a-token",
        "039-an-optional-input-in-a-bare-token",
        "040-not-json",
        "041-an-input-nobody-names",
        "042-a-bool-that-is-never-placed",
        "043-number-default-without-the-integer-part",
        "044-number-default-with-a-trailing-dot",
        "045-int-default-with-leading-zeros",
        "046-number-default-with-a-leading-zero-before-a-fraction",
        "047-number-default-with-a-leading-plus",
        "048-int-default-that-is-a-minus-sign-alone",
        "049-int-default-with-a-surrounding-space",
        "050-int-bound-outside-32-bits",
        "051-icon-key-starting-with-a-digit",
        "052-icon-key-ending-with-a-hyphen",
        "053-icon-key-of-33-characters",
        "054-unknown-csv-separator",
        "055-translated-hint-over-its-cap",
        "056-when-false-token-with-a-look-alike-character",
        "057-when-false-token-with-a-web-address",
        "058-when-false-token-whose-fixed-value-would-be-refused-at-run",
        "059-over-64-tokens-only-because-when-false-is-the-longer-list",
        "060-number-default-just-above-a-maximum-that-two-doubles-cannot-tell-apart",
    ];
}
