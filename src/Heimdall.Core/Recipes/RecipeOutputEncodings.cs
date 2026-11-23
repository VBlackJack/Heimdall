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

using System.Text;

namespace Heimdall.Core.Recipes;

/// <summary>Maps <see cref="RecipeOutputEncoding"/> to a .NET encoding.</summary>
internal static class RecipeOutputEncodings
{
    /// <summary>
    /// UTF-8 without a byte order mark, or the OEM code page of the machine, which is the code page of
    /// a console child (not the one of the user's culture). The OEM code pages live in the code-pages
    /// provider on .NET; registering it again is harmless. When the code page cannot be obtained the
    /// result is UTF-8, never an exception.
    /// </summary>
    public static Encoding Resolve(RecipeOutputEncoding kind)
    {
        Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (kind == RecipeOutputEncoding.Utf8)
            return utf8;

        int codePage = RecipeProgramNative.OemCodePage();
        if (codePage <= 0)
            return utf8;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return utf8;
        }
    }
}
