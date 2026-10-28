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
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Rdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The server and gateway dialogs declare their validation rules as attributes, and the message
/// an attribute carries is a locale key that the view model turns into the user's language.
/// </summary>
/// <remarks>
/// <para>
/// The attributes used to carry the English sentence, and a private table mapped each sentence
/// back to its key. Two copies of one message: the table drifted from the attributes the day a
/// sentence was reworded, and the field then showed English whatever the language.
/// </para>
/// <para>
/// A key is not a sentence, so a message that is not a key cannot be told apart from one by
/// looking at the screen in English. This reads every attribute off the view models and asks the
/// three catalogues for each one.
/// </para>
/// </remarks>
public sealed class DialogValidationMessagesAreKeysTests
{
    private static readonly string[] Locales = ["en", "fr", "es"];

    private static readonly Type[] DialogViewModels =
    [
        typeof(ServerDialogViewModel),
        typeof(GatewayDialogViewModel),
    ];

    /// <summary>
    /// The floor is one-sided: the failure this guards is the reflection seeing fewer attributes
    /// than the view models declare (a toolkit that stopped copying them to the generated
    /// property), which would leave every other test here green over nothing.
    /// </summary>
    private const int MinimumDeclaredMessages = 21;

    private static IReadOnlyList<(string Owner, string Property, string Message)> DeclaredMessages { get; } =
        DialogViewModels
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(property => property
                    .GetCustomAttributes<ValidationAttribute>(inherit: true)
                    .Where(attribute => attribute.ErrorMessage is not null)
                    .Select(attribute => (type.Name, property.Name, attribute.ErrorMessage!))))
            .ToList();

    [Fact]
    public void TheDeclaredMessagesAreFoundByReflection()
    {
        Assert.True(
            DeclaredMessages.Count >= MinimumDeclaredMessages,
            $"Expected at least {MinimumDeclaredMessages} declared messages, found {DeclaredMessages.Count}.");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void EveryDeclaredMessageIsAKeyOfTheCatalogue(string locale)
    {
        Dictionary<string, string> strings = LoadLocale(locale);

        List<string> missing = DeclaredMessages
            .Where(declared => !strings.ContainsKey(declared.Message))
            .Select(declared => $"{declared.Owner}.{declared.Property}: \"{declared.Message}\"")
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The English catalogue is where the bounds of the fixed-size and colour-depth rules are
    /// spelled out for the user; the Core constants are what a test pins against the enforced
    /// bounds. They must still be the same sentence, or the pin guards a text nobody reads.
    /// </summary>
    [Theory]
    [InlineData("ValidationRdpFixedWidthRange", nameof(RdpDisplayLimits.FixedWidthRangeMessage))]
    [InlineData("ValidationRdpFixedHeightRange", nameof(RdpDisplayLimits.FixedHeightRangeMessage))]
    [InlineData("ValidationColorDepth", nameof(RdpDisplayLimits.ColorDepthRangeMessage))]
    public void TheEnglishRangeSentencesAreTheOnesTheCoreTestPins(string key, string coreConstant)
    {
        string pinned = (string)typeof(RdpDisplayLimits)
            .GetField(coreConstant, BindingFlags.Public | BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Equal(pinned, LoadLocale("en")[key]);
    }

    [Fact]
    public async Task AGatewayFailureReachesTheFieldAsTheSentenceOfTheUsersLanguage()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "fr");
        GatewayDialogViewModel viewModel = new() { Localizer = localizer, Port = 70000 };

        viewModel.ValidateCommand.Execute(null);

        Assert.Equal(localizer["ValidationGatewayNameRequired"], viewModel.NameError);
        Assert.Equal(localizer["ValidationGatewayPortRange"], viewModel.PortError);
        Assert.NotEqual("ValidationGatewayNameRequired", viewModel.NameError);
    }

    [Fact]
    public async Task AServerFailureReachesTheFieldAsTheSentenceOfTheUsersLanguage()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "fr");
        ServerDialogViewModel viewModel = new() { Localizer = localizer };

        viewModel.DisplayName = string.Empty;
        viewModel.ValidateCommand.Execute(null);

        Assert.Equal(localizer["ValidationDisplayNameRequired"], viewModel.DisplayNameError);
        Assert.NotEqual("ValidationDisplayNameRequired", viewModel.DisplayNameError);
    }

    private static Dictionary<string, string> LoadLocale(string locale)
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "locales", locale + ".json");
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Locale {locale} did not parse."));
    }
}
