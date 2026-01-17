using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Newtonsoft.Json;
using NJsonSchema;
using Tomlyn;

namespace TomlEditor.Schema
{
    /// <summary>
    /// Service for loading, caching, and validating TOML against JSON Schemas.
    /// Schemas are specified via inline directive: #:schema &lt;url&gt;
    /// or automatically matched from SchemaStore.org catalog.
    /// </summary>
    public class TomlSchemaService
    {
        private const string SchemaStoreCatalogUrl = "https://www.schemastore.org/api/json/catalog.json";

        private static readonly Regex SchemaDirectiveRegex = new Regex(
            @"^\s*#:schema\s+(?<url>\S+)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly Dictionary<string, JsonSchema> _schemaCache = new Dictionary<string, JsonSchema>();
        private static readonly HttpClient HttpClient = new HttpClient();

        private static List<SchemaStoreCatalogEntry> _catalogEntries;
        private static readonly object _catalogLock = new object();
        private static Task _catalogLoadTask;

        static TomlSchemaService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "TomlEditor-VisualStudio");
            HttpClient.Timeout = System.TimeSpan.FromSeconds(10);

            // Start loading the catalog in the background
            _catalogLoadTask = LoadCatalogAsync();
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
        /// Checks if a schema is available for the document (either via directive or catalog match).
        /// </summary>
        public static bool HasSchema(string documentText, string fileName)
        {
            // Check for explicit directive
            if (!string.IsNullOrEmpty(GetSchemaUrl(documentText)))
            {
                return true;
            }

            // Check catalog (non-blocking)
            if (_catalogEntries != null && !string.IsNullOrEmpty(fileName))
            {
                return FindSchemaInCatalog(fileName) != null;
            }

            return false;
        }

        /// <summary>
        /// Gets the schema for a document, loading from cache or fetching from URL.
        /// Falls back to SchemaStore.org catalog matching if no directive is present.
        /// </summary>
        public async Task<JsonSchema> GetSchemaAsync(string documentText, string fileName = null)
        {
            // First, check for explicit #:schema directive
            string url = GetSchemaUrl(documentText);

            // If no directive, try to match from SchemaStore catalog
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(fileName))
            {
                await EnsureCatalogLoadedAsync();
                url = FindSchemaInCatalog(fileName);
            }

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
            public async Task<IList<SchemaValidationError>> ValidateAsync(string tomlText, string fileName = null)
            {
                var errors = new List<SchemaValidationError>();

                JsonSchema schema = await GetSchemaAsync(tomlText, fileName);
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
            public async Task<SchemaPropertyInfo> GetPropertyInfoAsync(string documentText, string path, string fileName = null)
            {
                JsonSchema schema = await GetSchemaAsync(documentText, fileName);
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
            public async Task<IEnumerable<SchemaCompletion>> GetCompletionsAsync(string documentText, string tablePath, string fileName = null)
            {
                JsonSchema schema = await GetSchemaAsync(documentText, fileName);
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

            #region SchemaStore Catalog

            private static async Task LoadCatalogAsync()
            {
                try
                {
                    string json = await HttpClient.GetStringAsync(SchemaStoreCatalogUrl);
                    var catalog = JsonConvert.DeserializeObject<SchemaStoreCatalog>(json);

                    if (catalog?.Schemas != null)
                    {
                        // Filter to only TOML-related schemas
                        lock (_catalogLock)
                        {
                            _catalogEntries = catalog.Schemas
                                .Where(s => s.FileMatch != null && s.FileMatch.Any(f => f.EndsWith(".toml")))
                                .ToList();
                        }
                    }
                }
                catch
                {
                    // Catalog loading failed - continue without catalog support
                    _catalogEntries = new List<SchemaStoreCatalogEntry>();
                }
            }

            private static async Task EnsureCatalogLoadedAsync()
            {
                if (_catalogLoadTask != null)
                {
                    await _catalogLoadTask;
                }
            }

            private static string FindSchemaInCatalog(string fileName)
            {
                if (_catalogEntries == null || string.IsNullOrEmpty(fileName))
                {
                    return null;
                }

                // Get just the filename without path
                string name = System.IO.Path.GetFileName(fileName);
                // Also get the full path for patterns that include directory matching
                string fullPath = fileName.Replace("\\", "/");

                foreach (var entry in _catalogEntries)
                {
                    if (entry.FileMatch == null)
                    {
                        continue;
                    }

                    foreach (string pattern in entry.FileMatch)
                    {
                        if (MatchesGlobPattern(name, fullPath, pattern))
                        {
                            return entry.Url;
                        }
                            }
                        }

                        return null;
                    }

                    /// <summary>
                    /// Matches a filename against a glob pattern using Microsoft.Extensions.FileSystemGlobbing.
                    /// </summary>
                    private static bool MatchesGlobPattern(string fileName, string fullPath, string pattern)
                    {
                        if (string.IsNullOrEmpty(pattern))
                        {
                            return false;
                        }

                        try
                        {
                            var matcher = new Matcher(System.StringComparison.OrdinalIgnoreCase);
                            matcher.AddInclude(pattern);

                            // Try matching against the full path first
                            string directory = System.IO.Path.GetDirectoryName(fullPath) ?? ".";
                            var directoryInfo = new InMemoryDirectoryInfo(directory, new[] { fullPath });
                            var result = matcher.Execute(directoryInfo);

                            if (result.HasMatches)
                            {
                                return true;
                            }

                            // Also try matching against just the filename for patterns like "pyproject.toml"
                            var fileOnlyInfo = new InMemoryDirectoryInfo(".", new[] { fileName });
                            result = matcher.Execute(fileOnlyInfo);

                            return result.HasMatches;
                        }
                        catch
                        {
                            // Fall back to simple comparison
                            return fileName.Equals(pattern, System.StringComparison.OrdinalIgnoreCase) ||
                                   fileName.Equals(System.IO.Path.GetFileName(pattern), System.StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    private class SchemaStoreCatalog
                    {
                        [JsonProperty("schemas")]
                            public List<SchemaStoreCatalogEntry> Schemas { get; set; }
                        }

                        #endregion

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

            /// <summary>
            /// Represents an entry in the SchemaStore.org catalog.
            /// </summary>
            internal class SchemaStoreCatalogEntry
            {
                [JsonProperty("name")]
                public string Name { get; set; }

                [JsonProperty("description")]
                public string Description { get; set; }

                [JsonProperty("url")]
                public string Url { get; set; }

                [JsonProperty("fileMatch")]
                public string[] FileMatch { get; set; }
            }
        }
