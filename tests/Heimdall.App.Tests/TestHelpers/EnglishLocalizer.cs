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

using System.IO;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests.TestHelpers;

/// <summary>
/// A localizer loaded with the shipped English catalogue, for tests that assert on the words a
/// view model shows.
/// </summary>
/// <remarks>
/// A view model built without a localizer has no words to show: it answers with the locale key.
/// It used to carry its own English copy of every string, which is a second catalogue to keep in
/// step with the first. A test that wants to read a sentence loads the real one instead.
/// Each call builds a fresh instance, because a test may switch its locale.
/// </remarks>
internal static class EnglishLocalizer
{
    private const string EnglishLocale = "en";

    public static LocalizationManager Create()
    {
        LocalizationManager manager = new();
        manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), EnglishLocale)
            .GetAwaiter()
            .GetResult();
        return manager;
    }
}
