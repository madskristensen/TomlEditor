using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NJsonSchema;
using Tomlyn;

namespace TomlEditor.Schema
{
    /// <summary>
    /// Service for loading, caching, and validating TOML against JSON Schemas.
    /// Schemas are specified via inline directive: #:schema &lt;url&gt;
    /// </summary>
    public class TomlSchemaService
    {
        private static readonly Regex SchemaDirectiveRegex = new Regex(
            @"^\s*#:schema\s+(?<url>\S+)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly Dictionary<string, JsonSchema> _schemaCache = new Dictionary<string, JsonSchema>();
        private static readonly HttpClient HttpClient = new HttpClient();

        static TomlSchemaService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "TomlEditor-VisualStudio");
            HttpClient.Timeout = System.TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Extracts the schema URL from the document text using the #:schema directive.
        /// </summary>
        public static string GetSchemaUrl(string documentText)
        {
            if (string.IsNullOrEmpty(documentText))
            {
                return null;
            }

            Match match = SchemaDirectiveRegex.Match(documentText);
            return match.Success ? match.Groups["url"].Value : null;
        }

        /// <summary>
        /// Gets the schema for a document, loading from cache or fetching from URL.
        /// </summary>
        public async Task<JsonSchema> GetSchemaAsync(string documentText)
        {
            string url = GetSchemaUrl(documentText);

            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            if (_schemaCache.TryGetValue(url, out JsonSchema cached))
            {
                return cached;
            }

            JsonSchema schema = await LoadSchemaAsync(url);

            if (schema != null)
            {
                _schemaCache[url] = schema;
            }

            return schema;
        }

        /// <summary>
        /// Validates TOML text against its schema and returns validation errors.
        /// </summary>
        public async Task<IList<SchemaValidationError>> ValidateAsync(string tomlText)
        {
            var errors = new List<SchemaValidationError>();

            JsonSchema schema = await GetSchemaAsync(tomlText);
            if (schema == null)
            {
                return errors;
            }

            try
            {
                // Parse TOML to model and convert to JSON
                var tomlModel = Toml.ToModel(tomlText);
                string json = JsonConvert.SerializeObject(tomlModel);

                // Validate JSON against schema
                var validationErrors = schema.Validate(json);

                foreach (var error in validationErrors)
                {
                    errors.Add(new SchemaValidationError
                    {
                        Path = error.Path?.TrimStart('#', '/').Replace("/", ".") ?? string.Empty,
                        Property = error.Property,
                        Message = GetFriendlyErrorMessage(error),
                        Kind = error.Kind
                    });
                }
            }
            catch
            {
                // TOML parsing failed - syntax errors are handled by Tomlyn
            }

            return errors;
        }

        /// <summary>
        /// Gets property information from the schema at a given path.
        /// </summary>
        public async Task<SchemaPropertyInfo> GetPropertyInfoAsync(string documentText, string path)
        {
            JsonSchema schema = await GetSchemaAsync(documentText);
            if (schema == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            JsonSchemaProperty prop = GetPropertyAtPath(schema, path);
            if (prop == null)
            {
                return null;
            }

            return CreatePropertyInfo(prop, path);
        }

        /// <summary>
        /// Gets available completions at a given path in the schema.
        /// </summary>
        public async Task<IEnumerable<SchemaCompletion>> GetCompletionsAsync(string documentText, string tablePath)
        {
            JsonSchema schema = await GetSchemaAsync(documentText);
            if (schema == null)
            {
                return Enumerable.Empty<SchemaCompletion>();
            }

            JsonSchema targetSchema;
            if (string.IsNullOrEmpty(tablePath))
            {
                targetSchema = schema;
            }
            else
            {
                JsonSchemaProperty prop = GetPropertyAtPath(schema, tablePath);
                targetSchema = prop?.ActualSchema;
            }

            if (targetSchema?.ActualProperties == null)
            {
                return Enumerable.Empty<SchemaCompletion>();
            }

            return targetSchema.ActualProperties.Select(kvp => new SchemaCompletion
            {
                Key = kvp.Key,
                Description = kvp.Value.Description,
                Type = GetTypeString(kvp.Value),
                IsDeprecated = kvp.Value.IsDeprecated
            });
        }

        private static async Task<JsonSchema> LoadSchemaAsync(string url)
        {
            try
            {
                if (url.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
                {
                    string filePath = new System.Uri(url).LocalPath;
                    return await JsonSchema.FromFileAsync(filePath);
                }
                else
                {
                    return await JsonSchema.FromUrlAsync(url);
                }
            }
            catch
            {
                return null;
            }
        }

        private static JsonSchemaProperty GetPropertyAtPath(JsonSchema schema, string path)
        {
            if (schema == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            string[] parts = path.Split('.');
            JsonSchema current = schema;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];

                // Try to find property in current schema
                if (current.ActualProperties != null && 
                    current.ActualProperties.TryGetValue(part, out JsonSchemaProperty prop))
                {
                    if (i == parts.Length - 1)
                    {
                        return prop;
                    }

                    current = prop.ActualSchema;
                }
                // Check additionalProperties
                else if (current.AdditionalPropertiesSchema != null)
                {
                    if (i == parts.Length - 1)
                    {
                        // Return a "virtual" property based on additionalProperties
                        return new JsonSchemaProperty
                        {
                            Description = current.AdditionalPropertiesSchema.Description,
                            Type = current.AdditionalPropertiesSchema.Type
                        };
                    }

                    current = current.AdditionalPropertiesSchema;
                }
                else
                        {
                            return null;
                        }

                        if (current == null)
                        {
                            return null;
                        }
                    }

                    return null;
                }

                private static SchemaPropertyInfo CreatePropertyInfo(JsonSchemaProperty prop, string path)
                {
                    var actual = prop.ActualSchema;

                    // Safely get IsRequired - can throw if property has no parent
                    bool isRequired = false;
                    try
                    {
                        isRequired = prop.IsRequired;
                    }
                    catch
                    {
                        // Property doesn't have a parent schema (e.g., from additionalProperties)
                    }

                    var info = new SchemaPropertyInfo
                    {
                        Name = path.Contains(".") ? path.Substring(path.LastIndexOf('.') + 1) : path,
                        Path = path,
                        Description = prop.Description ?? actual?.Description,
                        Type = GetTypeString(prop),
                        IsDeprecated = prop.IsDeprecated,
                        IsRequired = isRequired,
                        Default = prop.Default?.ToString(),
                        EnumValues = actual?.Enumeration?.Select(e => e?.ToString()).ToArray()
                    };

                    return info;
                }

        private static string GetTypeString(JsonSchemaProperty prop)
        {
            if (prop == null)
            {
                return null;
            }

            var actual = prop.ActualSchema;
            if (actual == null)
            {
                return prop.Type.ToString().ToLowerInvariant();
            }

            if (actual.Type != JsonObjectType.None)
            {
                return actual.Type.ToString().ToLowerInvariant();
            }

            if (actual.Enumeration?.Any() == true)
            {
                return "enum";
            }

            if (actual.OneOf?.Any() == true || actual.AnyOf?.Any() == true)
            {
                return "union";
            }

            return null;
        }

        private static string GetFriendlyErrorMessage(NJsonSchema.Validation.ValidationError error)
        {
            string message = error.Kind.ToString();

            // Make the message more user-friendly
            switch (error.Kind)
            {
                case NJsonSchema.Validation.ValidationErrorKind.PropertyRequired:
                    return $"Missing required property '{error.Property}'";
                case NJsonSchema.Validation.ValidationErrorKind.NoAdditionalPropertiesAllowed:
                    return $"Unknown property '{error.Property}'";
                case NJsonSchema.Validation.ValidationErrorKind.StringExpected:
                    return "Expected a string value";
                case NJsonSchema.Validation.ValidationErrorKind.IntegerExpected:
                    return "Expected an integer value";
                case NJsonSchema.Validation.ValidationErrorKind.NumberExpected:
                    return "Expected a number value";
                case NJsonSchema.Validation.ValidationErrorKind.BooleanExpected:
                    return "Expected a boolean value (true/false)";
                case NJsonSchema.Validation.ValidationErrorKind.ArrayExpected:
                    return "Expected an array value";
                case NJsonSchema.Validation.ValidationErrorKind.ObjectExpected:
                    return "Expected an object/table value";
                case NJsonSchema.Validation.ValidationErrorKind.NotInEnumeration:
                    return $"Value is not one of the allowed values";
                default:
                    return message;
            }
        }
    }

    /// <summary>
    /// Represents a schema validation error with path information.
    /// </summary>
    public class SchemaValidationError
    {
        public string Path { get; set; }
        public string Property { get; set; }
        public string Message { get; set; }
        public NJsonSchema.Validation.ValidationErrorKind Kind { get; set; }
    }

    /// <summary>
    /// Represents information about a schema property for QuickInfo tooltips.
    /// </summary>
    public class SchemaPropertyInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public bool IsDeprecated { get; set; }
        public bool IsRequired { get; set; }
        public string Default { get; set; }
        public string[] EnumValues { get; set; }
    }

    /// <summary>
    /// Represents a completion item from the schema.
    /// </summary>
    public class SchemaCompletion
    {
        public string Key { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public bool IsDeprecated { get; set; }
    }
}
