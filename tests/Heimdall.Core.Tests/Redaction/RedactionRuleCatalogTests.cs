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

using System.Text.RegularExpressions;
using Heimdall.Core.Redaction;

namespace Heimdall.Core.Tests.Redaction;

public sealed class RedactionRuleCatalogTests
{
    // Pinned on purpose: ids appear inside mask tokens and in documentation. Adding or removing a rule must
    // change this list in the same commit.
    private static readonly string[] ExpectedRuleIds =
    [
        "private-key-block",
        "authorization-header",
        "url-credentials",
        "cli-password",
        "password-prompt",
        "key-value-secret",
        "aws-access-key",
        "github-token",
        "slack-token",
        "jwt",
        "ipv4-address",
        "host-name",
    ];

    // Producer: RedactionRuleCatalog.Default (RedactionRuleCatalog.Build), the shipped rule list.
    [Fact]
    public void Default_RuleIds_MatchThePinnedListInBothDirections()
    {
        var actual = RedactionRuleCatalog.Default.Select(rule => rule.Id).ToList();

        Assert.Equal(ExpectedRuleIds, actual);
        Assert.Empty(ExpectedRuleIds.Except(actual));
        Assert.Empty(actual.Except(ExpectedRuleIds));
    }

    [Fact]
    public void Default_RuleIds_AreUniqueKebabCase()
    {
        var ids = RedactionRuleCatalog.Default.Select(rule => rule.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", id));
    }

    // Producer: RedactionRule.Patterns, every regex built in RedactionPatterns.
    [Fact]
    public void Default_EveryPattern_DeclaresTheSharedTimeoutOrIsNonBacktracking()
    {
        foreach (RedactionRule rule in RedactionRuleCatalog.Default)
        {
            Assert.NotEmpty(rule.Patterns);
            foreach (Regex pattern in rule.Patterns)
            {
                bool nonBacktracking = pattern.Options.HasFlag(RegexOptions.NonBacktracking);
                Assert.True(
                    nonBacktracking || pattern.MatchTimeout == RedactionRule.MatchTimeout,
                    $"{rule.Id}: {pattern} declares {pattern.MatchTimeout}");
            }
        }
    }

    // Producer: RedactionRuleCatalog.Build, requiresPseudonymization on the two pseudonym rules only.
    [Fact]
    public void Default_OnlyThePseudonymRulesRequireTheOption()
    {
        var optIn = RedactionRuleCatalog.Default.Where(rule => rule.RequiresPseudonymization).Select(rule => rule.Id).ToList();

        Assert.Equal(["ipv4-address", "host-name"], optIn);
    }

    // Producer: RedactionMask.Token, the documented technical format.
    [Fact]
    public void MaskToken_IsBuiltFromTheConstantFormat()
    {
        Assert.Equal("[masked:jwt]", RedactionMask.Token("jwt"));
        Assert.True(RedactionMask.IsMaskToken("[masked:jwt]"));
        Assert.False(RedactionMask.IsMaskToken("masked:jwt"));
    }
}
