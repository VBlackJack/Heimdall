/*
 * Copyright 2025 Julien Bombled
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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Validates JSON sync files against predefined schemas.
/// Provides security by ensuring imported data conforms to expected structure.
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly JsonSchema CategorySchema;
    private static readonly JsonSchema TemplateSchema;
    private static readonly JsonSchema ActionSchema;
    private static readonly JsonSchema BatchSchema;

    static JsonSchemaValidator()
    {
        CategorySchema = JsonSchema.FromText(CategorySchemaJson);
        TemplateSchema = JsonSchema.FromText(BuildTemplateSchemaJson());
        ActionSchema = JsonSchema.FromText(BuildActionSchemaJson());
        BatchSchema = JsonSchema.FromText(BatchSchemaJson);
    }

    /// <summary>
    /// Validates a category JSON file
    /// </summary>
    public static SchemaValidationResult ValidateCategory(string json)
    {
        return ValidateJson(json, CategorySchema, "category");
    }

    /// <summary>
    /// Validates a template JSON file
    /// </summary>
    public static SchemaValidationResult ValidateTemplate(string json)
    {
        return ValidateJson(json, TemplateSchema, "template");
    }

    /// <summary>
    /// Validates an action JSON file
    /// </summary>
    public static SchemaValidationResult ValidateAction(string json)
    {
        return ValidateJson(json, ActionSchema, "action");
    }

    /// <summary>
    /// Validates a batch JSON file
    /// </summary>
    public static SchemaValidationResult ValidateBatch(string json)
    {
        return ValidateJson(json, BatchSchema, "batch");
    }

    private static SchemaValidationResult ValidateJson(string json, JsonSchema schema, string entityType)
    {
        try
        {
            var jsonNode = JsonNode.Parse(json);
            if (jsonNode == null)
            {
                return new SchemaValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { $"Invalid JSON: could not parse {entityType} file" }
                };
            }

            var result = schema.Evaluate(jsonNode);

            if (result.IsValid)
            {
                return new SchemaValidationResult { IsValid = true };
            }

            var errors = new List<string>();
            CollectErrors(result, errors);

            return new SchemaValidationResult
            {
                IsValid = false,
                Errors = errors.Any() ? errors : new List<string> { $"Schema validation failed for {entityType}" }
            };
        }
        catch (JsonException ex)
        {
            return new SchemaValidationResult
            {
                IsValid = false,
                Errors = new List<string> { $"JSON parse error: {ex.Message}" }
            };
        }
        catch (Exception ex)
        {
            return new SchemaValidationResult
            {
                IsValid = false,
                Errors = new List<string> { $"Validation error: {ex.Message}" }
            };
        }
    }

    private static void CollectErrors(EvaluationResults result, List<string> errors)
    {
        if (result.Errors != null)
        {
            foreach (var error in result.Errors)
            {
                var path = result.InstanceLocation?.ToString() ?? "";
                errors.Add($"{path}: {error.Key} - {error.Value}");
            }
        }

        if (result.Details != null)
        {
            foreach (var detail in result.Details)
            {
                CollectErrors(detail, errors);
            }
        }
    }

    #region JSON Schemas

    private const string CategorySchemaJson = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["id", "name"],
        "properties": {
            "id": {
                "type": "string",
                "format": "uuid",
                "description": "Unique identifier (GUID)"
            },
            "name": {
                "type": "string",
                "minLength": 1,
                "maxLength": 100,
                "description": "Category name"
            },
            "description": {
                "type": ["string", "null"],
                "maxLength": 500
            },
            "iconKey": {
                "type": ["string", "null"],
                "maxLength": 50
            },
            "colorHex": {
                "type": ["string", "null"],
                "pattern": "^#[0-9A-Fa-f]{6}$"
            },
            "isSystemCategory": {
                "type": "boolean"
            },
            "displayOrder": {
                "type": "integer",
                "minimum": 0
            },
            "isHidden": {
                "type": "boolean"
            }
        },
        "additionalProperties": false
    }
    """;

    private const string TemplateSchemaJson = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["id", "name", "commandPattern"],
        "properties": {
            "id": {
                "type": "string",
                "format": "uuid"
            },
            "name": {
                "type": "string",
                "minLength": 1,
                "maxLength": 200
            },
            "platform": {
                "type": "string",
                "enum": ["Windows", "Linux", "Both"]
            },
            "commandPattern": {
                "type": "string",
                "minLength": 1,
                "maxLength": __COMMAND_PATTERN_MAX_LENGTH__
            },
            "parameters": {
                "type": ["array", "null"],
                "items": {
                    "type": "object",
                    "required": ["name"],
                    "properties": {
                        "name": {
                            "type": "string",
                            "minLength": 1,
                            "maxLength": 50
                        },
                        "label": {
                            "type": "string",
                            "maxLength": 100
                        },
                        "type": {
                            "type": "string",
                            "enum": ["__PARAMETER_TYPES__"]
                        },
                        "allowedValues": {
                            "type": ["array", "null"],
                            "items": {
                                "type": "string",
                                "maxLength": 200
                            },
                            "maxItems": 50
                        },
                        "quoting": {
                            "type": ["string", "null"],
                            "enum": ["__QUOTING_MODES__"]
                        },
                        "defaultValue": {
                            "type": ["string", "null"]
                        },
                        "required": {
                            "type": "boolean"
                        },
                        "description": {
                            "type": ["string", "null"],
                            "maxLength": 500
                        }
                    },
                    "additionalProperties": false
                }
            }
        },
        "additionalProperties": false
    }
    """;

    private const string ParameterTypesPlaceholder = "[\"__PARAMETER_TYPES__\"]";
    private const string QuotingModesPlaceholder = "[\"__QUOTING_MODES__\"]";

    private const string CommandPatternMaxLengthPlaceholder = "__COMMAND_PATTERN_MAX_LENGTH__";

    /// <summary>
    /// The template schema with its parameter types taken from
    /// <see cref="TemplateParameterTypes.Accepted"/>, the list the generator understands, its
    /// quoting modes from <see cref="QuotingMode"/>, and its pattern bound from
    /// <see cref="ValidationConstants.MaxTemplateCommandPatternLength"/>, so a pattern the
    /// library stores is not refused by the sync.
    /// </summary>
    internal static string BuildTemplateSchemaJson()
        => TemplateSchemaJson
            .Replace(
                CommandPatternMaxLengthPlaceholder,
                ValidationConstants.MaxTemplateCommandPatternLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                ParameterTypesPlaceholder,
                JsonSerializer.Serialize(TemplateParameterTypes.Accepted),
                StringComparison.Ordinal)
            .Replace(
                QuotingModesPlaceholder,
                JsonSerializer.Serialize(Enum.GetNames<QuotingMode>().Cast<string?>().Append(null)),
                StringComparison.Ordinal);

    private const string LevelNamesPlaceholder = "[\"__LEVEL_NAMES__\"]";

    /// <summary>
    /// The action schema with its level list taken from <see cref="CriticalityLevelNames"/>,
    /// the one source shared with the import that reads the level.
    /// </summary>
    internal static string BuildActionSchemaJson()
        => ActionSchemaJson.Replace(
            LevelNamesPlaceholder,
            JsonSerializer.Serialize(CriticalityLevelNames.AcceptedNames),
            StringComparison.Ordinal);

    private const string ActionSchemaJson = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["id", "title", "category"],
        "properties": {
            "id": {
                "type": "string",
                "format": "uuid"
            },
            "title": {
                "type": "string",
                "minLength": 1,
                "maxLength": 200
            },
            "description": {
                "type": "string",
                "maxLength": 2000
            },
            "category": {
                "type": "string",
                "minLength": 1,
                "maxLength": 100
            },
            "platform": {
                "type": "string",
                "enum": ["Windows", "Linux", "Both"]
            },
            "level": {
                "type": "string",
                "enum": ["__LEVEL_NAMES__"]
            },
            "tags": {
                "type": ["array", "null"],
                "items": {
                    "type": "string",
                    "maxLength": 50
                },
                "maxItems": 20
            },
            "windowsTemplateId": {
                "type": ["string", "null"],
                "format": "uuid"
            },
            "linuxTemplateId": {
                "type": ["string", "null"],
                "format": "uuid"
            },
            "examples": {
                "type": ["array", "null"],
                "items": {
                    "$ref": "#/$defs/example"
                },
                "maxItems": 50
            },
            "windowsExamples": {
                "type": ["array", "null"],
                "items": {
                    "$ref": "#/$defs/example"
                },
                "maxItems": 50
            },
            "linuxExamples": {
                "type": ["array", "null"],
                "items": {
                    "$ref": "#/$defs/example"
                },
                "maxItems": 50
            },
            "notes": {
                "type": ["string", "null"],
                "maxLength": 5000
            },
            "links": {
                "type": ["array", "null"],
                "items": {
                    "type": "object",
                    "required": ["title", "url"],
                    "properties": {
                        "title": {
                            "type": "string",
                            "maxLength": 200
                        },
                        "url": {
                            "type": "string",
                            "format": "uri",
                            "maxLength": 2000
                        }
                    },
                    "additionalProperties": false
                },
                "maxItems": 10
            },
            "isUserCreated": {
                "type": "boolean"
            },
            "updatedAt": {
                "type": ["string", "null"],
                "format": "date-time"
            },
            "translations": {
                "type": ["object", "null"],
                "maxProperties": 20,
                "propertyNames": {
                    "pattern": "^[a-z]{2}$"
                },
                "additionalProperties": {
                    "$ref": "#/$defs/translation"
                }
            }
        },
        "additionalProperties": false,
        "$defs": {
            "translation": {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "maxLength": 200
                    },
                    "description": {
                        "type": "string",
                        "maxLength": 2000
                    },
                    "notes": {
                        "type": "string",
                        "maxLength": 5000
                    },
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "windows": {
                                "$ref": "#/$defs/parameterTranslations"
                            },
                            "linux": {
                                "$ref": "#/$defs/parameterTranslations"
                            }
                        },
                        "additionalProperties": false
                    },
                    "examples": {
                        "type": "object",
                        "maxProperties": 50,
                        "additionalProperties": {
                            "type": "string",
                            "maxLength": 500
                        }
                    }
                },
                "additionalProperties": false
            },
            "parameterTranslations": {
                "type": "object",
                "maxProperties": 50,
                "additionalProperties": {
                    "type": "object",
                    "properties": {
                        "label": {
                            "type": "string",
                            "maxLength": 200
                        },
                        "description": {
                            "type": "string",
                            "maxLength": 500
                        }
                    },
                    "additionalProperties": false
                }
            },
            "example": {
                "type": "object",
                "required": ["command"],
                "properties": {
                    "command": {
                        "type": "string",
                        "maxLength": 2000
                    },
                    "description": {
                        "type": "string",
                        "maxLength": 500
                    },
                    "platform": {
                        "type": ["string", "null"],
                        "enum": ["Windows", "Linux", "Both", null]
                    }
                },
                "additionalProperties": false
            }
        }
    }
    """;

    private const string BatchSchemaJson = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["id", "name"],
        "properties": {
            "id": {
                "type": "string",
                "format": "uuid"
            },
            "name": {
                "type": "string",
                "minLength": 1,
                "maxLength": 200
            },
            "description": {
                "type": ["string", "null"],
                "maxLength": 2000
            },
            "executionMode": {
                "type": "string",
                "enum": ["StopOnError", "ContinueOnError"]
            },
            "tags": {
                "type": ["array", "null"],
                "items": {
                    "type": "string",
                    "maxLength": 50
                },
                "maxItems": 20
            },
            "commands": {
                "type": ["array", "null"],
                "items": {
                    "type": "object",
                    "required": ["command"],
                    "properties": {
                        "id": {
                            "type": ["string", "null"]
                        },
                        "order": {
                            "type": "integer",
                            "minimum": 0
                        },
                        "actionId": {
                            "type": ["string", "null"]
                        },
                        "actionTitle": {
                            "type": "string",
                            "maxLength": 200
                        },
                        "command": {
                            "type": "string",
                            "minLength": 1,
                            "maxLength": 2000
                        },
                        "platform": {
                            "type": ["string", "null"],
                            "enum": ["Windows", "Linux", "Both", null]
                        },
                        "description": {
                            "type": ["string", "null"],
                            "maxLength": 500
                        },
                        "parameters": {
                            "type": ["object", "null"],
                            "additionalProperties": {
                                "type": "string"
                            }
                        }
                    },
                    "additionalProperties": false
                },
                "maxItems": 100
            },
            "variables": {
                "type": ["object", "null"],
                "additionalProperties": {
                    "type": "string"
                }
            },
            "isUserCreated": {
                "type": "boolean"
            },
            "updatedAt": {
                "type": ["string", "null"],
                "format": "date-time"
            }
        },
        "additionalProperties": false
    }
    """;

    #endregion
}

/// <summary>
/// Result of JSON schema validation
/// </summary>
public sealed class SchemaValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}
