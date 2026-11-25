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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>Which session values a recipe reads, so that a view can show the session it will use.</summary>
public sealed class RecipeSessionUseTests
{
    [Fact]
    public void ARecipeWithoutAPlaceholderUsesNoSession()
    {
        RecipeSessionUse use = RecipeSessionUse.Of(RecipeTestData.With([], RecipeArgument.Bare("-n"), RecipeArgument.Bare("{{Host}}")));

        Assert.False(use.UsesHost);
        Assert.False(use.UsesPort);
        Assert.False(use.UsesSession);
    }

    [Fact]
    public void TheHostAloneIsTheHostAlone()
    {
        RecipeSessionUse use = RecipeSessionUse.Of(RecipeTestData.With([], RecipeArgument.Bare("--host={Host}")));

        Assert.True(use.UsesHost);
        Assert.False(use.UsesPort);
        Assert.True(use.UsesSession);
    }

    [Fact]
    public void ThePortInAGroupIsFound()
    {
        RecipeSessionUse use = RecipeSessionUse.Of(RecipeTestData.With([], RecipeArgument.Group(["-p", "{Port}"])));

        Assert.False(use.UsesHost);
        Assert.True(use.UsesPort);
    }

    [Fact]
    public void ThePlaceholdersOfASwitchAreFoundToo()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-h", "{Host}"] };

        RecipeSessionUse use = RecipeSessionUse.Of(RecipeTestData.With([flag], RecipeArgument.Bare("{in:flag}")));

        Assert.True(use.UsesHost);
    }

    [Fact]
    public void AnInputPlaceholderIsNotASessionPlaceholder()
    {
        RecipeInput host = RecipeTestData.Input("Host", RecipeInputType.Hostname);

        RecipeSessionUse use = RecipeSessionUse.Of(RecipeTestData.With([host], RecipeArgument.Bare("{in:Host}")));

        Assert.False(use.UsesSession);
    }

    [Fact]
    public void TheRunnerAndTheResolverAreTheTypesBehindTheirInterfaces()
    {
        Assert.IsAssignableFrom<IRecipeRunner>(new RecipeRunner());
        Assert.IsAssignableFrom<IRecipeProgramResolver>(new RecipeProgramResolver());
    }
}
