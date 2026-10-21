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

using FluentAssertions;
using TwinShell.Core.Constants;
using TwinShell.Infrastructure.Services;

namespace TwinShell.Infrastructure.Tests;

public sealed class JsonSchemaValidatorTests
{
    // The bound is the one the library stores (ValidationConstants), read here rather than
    // written out, so the sync cannot refuse a pattern the library accepted.
    [Fact]
    public void ValidateTemplate_CommandPatternAtTheStoredBound_IsValid()
    {
        string json = CreateTemplateJson(new string('a', ValidationConstants.MaxTemplateCommandPatternLength));

        SchemaValidationResult result = JsonSchemaValidator.ValidateTemplate(json);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateTemplate_CommandPatternOneCharacterOverTheStoredBound_IsInvalid()
    {
        string json = CreateTemplateJson(new string('a', ValidationConstants.MaxTemplateCommandPatternLength + 1));

        SchemaValidationResult result = JsonSchemaValidator.ValidateTemplate(json);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateAction_WithTenLinks_IsValid()
    {
        string json = CreateActionJson(linksJson: CreateLinksJson(10));

        SchemaValidationResult result = JsonSchemaValidator.ValidateAction(json);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateAction_WithElevenLinks_IsInvalid()
    {
        string json = CreateActionJson(linksJson: CreateLinksJson(11));

        SchemaValidationResult result = JsonSchemaValidator.ValidateAction(json);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateAction_WithElevenExamples_RemainsValid()
    {
        string json = CreateActionJson(examplesJson: CreateExamplesJson(11));

        SchemaValidationResult result = JsonSchemaValidator.ValidateAction(json);

        result.IsValid.Should().BeTrue();
    }

    private static string CreateTemplateJson(string commandPattern)
    {
        return $$"""
        {
          "id": "11111111-1111-4111-8111-111111111111",
          "name": "Template",
          "commandPattern": "{{commandPattern}}"
        }
        """;
    }

    private static string CreateActionJson(
        string linksJson = "null",
        string examplesJson = "null")
    {
        return $$"""
        {
          "id": "22222222-2222-4222-8222-222222222222",
          "title": "Action",
          "category": "Category",
          "links": {{linksJson}},
          "examples": {{examplesJson}}
        }
        """;
    }

    private static string CreateLinksJson(int count)
    {
        IEnumerable<string> links = Enumerable.Range(0, count)
            .Select(index => $$"""{"title":"Link {{index}}","url":"https://example.com/{{index}}"}""");

        return "[" + string.Join(",", links) + "]";
    }

    private static string CreateExamplesJson(int count)
    {
        IEnumerable<string> examples = Enumerable.Range(0, count)
            .Select(index => $$"""{"command":"echo {{index}}"}""");

        return "[" + string.Join(",", examples) + "]";
    }
}
