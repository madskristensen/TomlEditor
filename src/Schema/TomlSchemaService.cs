using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;
using Newtonsoft.Json;
using NJsonSchema;
using NJsonSchema.Validation;
using Tomlyn;
using Tomlyn.Model;

namespace TomlEditor.Schema
{
    /// <summary>
    /// Service for loading, caching, and validating TOML against JSON Schemas.
    /// Schemas are specified via inline directive: #:schema &lt;url&gt;
    /// or automatically matched from SchemaStore.org catalog.
    /// </summary>
    public class TomlSchemaService
    {
        private const string _schemaStoreCatalogUrl = "https://www.schemastore.org/api/json/catalog.json";
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7);
        private static readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "TomlEditor", "Schemas");
        private static readonly string _catalogCacheFile = Path.Combine(_cacheDirectory, "catalog.json");

        private static readonly Regex _schemaDirectiveRegex = new(
            @"^\s*#:schema\s+(?<url>\S+)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly Dictionary<string, JsonSchema> _schemaCache = [];
        private static readonly HttpClient _httpClient = new();

        private static List<SchemaStoreCatalogEntry> _catalogEntries;
        private static readonly object _catalogLock = new();
        private static readonly Task _catalogLoadTask;

        static TomlSchemaService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "TomlEditor-VisualStudio");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);

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

            Match match = _schemaDirectiveRegex.Match(documentText);
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
        /// Gets navigation information to the schema definition for a property path.
        /// Returns the cached file path and line number for navigation.
        /// </summary>
        public async Task<SchemaNavigationInfo> GetSchemaNavigationInfoAsync(string documentText, string propertyPath, string fileName = null)
        {
            // First, check for explicit #:schema directive
            var url = GetSchemaUrl(documentText);

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

            // Get or create cached schema file
            var cachedFilePath = await GetOrCreateCachedSchemaFileAsync(url);
            if (string.IsNullOrEmpty(cachedFilePath))
            {
                return null;
            }

            // Find the line number for the property path
            var lineNumber = FindPropertyLineInSchema(cachedFilePath, propertyPath);

            return new SchemaNavigationInfo
            {
                FilePath = cachedFilePath,
                LineNumber = lineNumber,
                PropertyPath = propertyPath
            };
        }

        private static readonly Dictionary<string, string> _schemaFileCache = [];
        private static readonly object _fileCacheLock = new();

        /// <summary>
        /// Checks if a cached file exists and is not expired.
        /// </summary>
        private static bool IsCacheValid(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            DateTime lastWriteTime = File.GetLastWriteTimeUtc(filePath);
            return DateTime.UtcNow - lastWriteTime < _cacheExpiration;
        }

        /// <summary>
        /// Gets or creates a cached schema file on disk for the given URL.
        /// </summary>
        private async Task<string> GetOrCreateCachedSchemaFileAsync(string url)
        {
            var schemaName = GetSchemaNameFromUrl(url);
            Directory.CreateDirectory(_cacheDirectory);
            var cachedFilePath = Path.Combine(_cacheDirectory, schemaName + ".json");

            // Check if we have a valid cached file
            lock (_fileCacheLock)
            {
                if (_schemaFileCache.TryGetValue(url, out var existingPath) && IsCacheValid(existingPath))
                {
                    return existingPath;
                }

                // Also check disk cache even if not in memory cache
                if (IsCacheValid(cachedFilePath))
                {
                    _schemaFileCache[url] = cachedFilePath;
                    return cachedFilePath;
                }
            }

            try
            {
                string schemaContent;

                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = new Uri(url).LocalPath;
                    schemaContent = File.ReadAllText(filePath);
                }
                else
                {
                    schemaContent = await _httpClient.GetStringAsync(url);
                }

                File.WriteAllText(cachedFilePath, schemaContent);

                lock (_fileCacheLock)
                {
                    _schemaFileCache[url] = cachedFilePath;
                }

                return cachedFilePath;
            }
            catch
            {
                // If download fails but we have an old cached file, use it anyway
                if (File.Exists(cachedFilePath))
                {
                    lock (_fileCacheLock)
                    {
                        _schemaFileCache[url] = cachedFilePath;
                    }
                    return cachedFilePath;
                }

                return null;
            }
        }

        private static string GetSchemaNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = Path.GetFileNameWithoutExtension(uri.LocalPath);
                if (string.IsNullOrEmpty(name))
                {
                    name = uri.Host.Replace(".", "_");
                }
                // Sanitize the name for use as a filename
                foreach (var c in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(c, '_');
                }
                return name;
            }
            catch
            {
                return "schema_" + url.GetHashCode().ToString("X8");
            }
        }

        /// <summary>
        /// Finds the line number in the schema file where the property is defined.
        /// </summary>
        private static int FindPropertyLineInSchema(string filePath, string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return 1;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                var pathParts = propertyPath.Split('.');
                var targetProperty = pathParts[pathParts.Length - 1];

                // Search for the property definition pattern: "propertyName": {
                var searchPattern = $"\"{targetProperty}\"";

                // Track nesting to find the right property at the correct depth
                var propertiesDepth = 0;
                var targetDepth = pathParts.Length;

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    // Count "properties" occurrences to track depth
                    if (line.Contains("\"properties\""))
                    {
                        propertiesDepth++;
                    }

                    // Check if this line contains our target property at approximately the right depth
                    if (line.Contains(searchPattern) && propertiesDepth >= targetDepth - 1)
                    {
                        return i + 1; // Line numbers are 1-based
                    }
                }

                // Fallback: just find the first occurrence of the property name
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(searchPattern))
                    {
                        return i + 1;
                    }
                }
            }
            catch
            {
                // Ignore errors in line finding
            }

            return 1; // Default to first line
        }

        /// <summary>
        /// Gets the schema for a document, loading from cache or fetching from URL.
        /// Falls back to SchemaStore.org catalog matching if no directive is present.
        /// </summary>
        public async Task<JsonSchema> GetSchemaAsync(string documentText, string fileName = null)
        {
            // First, check for explicit #:schema directive
            var url = GetSchemaUrl(documentText);

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
                TomlTable tomlModel = Toml.ToModel(tomlText);
                var json = JsonConvert.SerializeObject(tomlModel);

                // Validate JSON against schema
                ICollection<ValidationError> validationErrors = schema.Validate(json);

                foreach (ValidationError error in validationErrors)
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
                return [];
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
                return [];
            }

            return targetSchema.ActualProperties.Select(kvp =>
            {
                JsonSchema actualSchema = kvp.Value.ActualSchema;

                // Check if this property represents a table (object type)
                var isTable = actualSchema?.Type == JsonObjectType.Object ||
                               actualSchema?.ActualProperties?.Count > 0;

                // Check if this property represents an array of tables
                var isArrayOfTables = actualSchema?.Type == JsonObjectType.Array &&
                                       actualSchema?.Item?.ActualProperties?.Count > 0;

                return new SchemaCompletion
                {
                    Key = kvp.Key,
                    Description = kvp.Value.Description,
                    Type = GetTypeString(kvp.Value),
                    IsDeprecated = kvp.Value.IsDeprecated,
                    IsTable = isTable || isArrayOfTables
                };
            });
        }

        /// <summary>
        /// Gets available table name completions from the schema.
        /// Returns all properties that are of type "object" (which map to TOML tables).
        /// </summary>
        public async Task<IEnumerable<SchemaCompletion>> GetTableCompletionsAsync(string documentText, string partialPath, string fileName = null)
        {
            JsonSchema schema = await GetSchemaAsync(documentText, fileName);
            if (schema == null)
            {
                return [];
            }

            var results = new List<SchemaCompletion>();
            CollectTablePaths(schema, string.Empty, partialPath, results);
            return results;
        }

        /// <summary>
        /// Recursively collects all table paths from the schema.
        /// </summary>
        private static void CollectTablePaths(JsonSchema schema, string currentPath, string partialPath, List<SchemaCompletion> results)
        {
            if (schema?.ActualProperties == null)
            {
                return;
            }

            foreach (KeyValuePair<string, JsonSchemaProperty> kvp in schema.ActualProperties)
            {
                var fullPath = string.IsNullOrEmpty(currentPath) ? kvp.Key : $"{currentPath}.{kvp.Key}";
                JsonSchema actualSchema = kvp.Value.ActualSchema;

                // Check if this property represents a table (object type)
                var isTable = actualSchema?.Type == JsonObjectType.Object ||
                               actualSchema?.ActualProperties?.Count > 0;

                // Check if this property represents an array of tables
                var isArrayOfTables = actualSchema?.Type == JsonObjectType.Array &&
                                       actualSchema?.Item?.ActualProperties?.Count > 0;

                if (isTable || isArrayOfTables)
                {
                    // Only include if it matches the partial path (prefix match)
                    if (string.IsNullOrEmpty(partialPath) || fullPath.StartsWith(partialPath, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SchemaCompletion
                        {
                            Key = fullPath,
                            Description = kvp.Value.Description ?? actualSchema?.Description,
                            Type = isArrayOfTables ? "array of tables" : "table",
                            IsDeprecated = kvp.Value.IsDeprecated
                        });
                    }

                    // Recurse into nested tables
                    JsonSchema nestedSchema = isArrayOfTables ? actualSchema?.Item : actualSchema;
                    if (nestedSchema != null)
                    {
                        CollectTablePaths(nestedSchema, fullPath, partialPath, results);
                    }
                }
            }
        }

        #region SchemaStore Catalog

        private static async Task LoadCatalogAsync()
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);

                string json = null;

                // Try to load from cache first
                if (IsCacheValid(_catalogCacheFile))
                {
                    json = File.ReadAllText(_catalogCacheFile);
                }
                else
                {
                    // Download fresh catalog
                    json = await _httpClient.GetStringAsync(_schemaStoreCatalogUrl);
                    File.WriteAllText(_catalogCacheFile, json);
                }

                SchemaStoreCatalog catalog = JsonConvert.DeserializeObject<SchemaStoreCatalog>(json);

                if (catalog?.Schemas != null)
                {
                    // Filter to only TOML-related schemas
                    lock (_catalogLock)
                    {
                        _catalogEntries = [.. catalog.Schemas.Where(s => s.FileMatch != null && s.FileMatch.Any(f => f.EndsWith(".toml")))];
                    }
                }
            }
            catch
            {
                // If download fails, try to use old cached catalog
                if (File.Exists(_catalogCacheFile))
                {
                    try
                    {
                        var json = File.ReadAllText(_catalogCacheFile);
                        SchemaStoreCatalog catalog = JsonConvert.DeserializeObject<SchemaStoreCatalog>(json);
                        if (catalog?.Schemas != null)
                        {
                            lock (_catalogLock)
                            {
                                _catalogEntries = [.. catalog.Schemas.Where(s => s.FileMatch != null && s.FileMatch.Any(f => f.EndsWith(".toml")))];
                            }
                            return;
                        }
                    }
                    catch
                    {
                        // Ignore cache read errors
                    }
                }

                // Catalog loading failed - continue without catalog support
                _catalogEntries = [];
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
            var name = Path.GetFileName(fileName);
            // Also get the full path for patterns that include directory matching
            var fullPath = fileName.Replace("\\", "/");

            foreach (SchemaStoreCatalogEntry entry in _catalogEntries)
            {
                if (entry.FileMatch == null)
                {
                    continue;
                }

                foreach (var pattern in entry.FileMatch)
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
                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern);

                // Try matching against the full path first
                var directory = Path.GetDirectoryName(fullPath) ?? ".";
                var directoryInfo = new InMemoryDirectoryInfo(directory, [fullPath]);
                PatternMatchingResult result = matcher.Execute(directoryInfo);

                if (result.HasMatches)
                {
                    return true;
                }

                // Also try matching against just the filename for patterns like "pyproject.toml"
                var fileOnlyInfo = new InMemoryDirectoryInfo(".", [fileName]);
                result = matcher.Execute(fileOnlyInfo);

                return result.HasMatches;
            }
            catch
            {
                // Fall back to simple comparison
                return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                       fileName.Equals(Path.GetFileName(pattern), StringComparison.OrdinalIgnoreCase);
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
                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = new Uri(url).LocalPath;
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

            var parts = path.Split('.');
            JsonSchema current = schema;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

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
            JsonSchema actual = prop.ActualSchema;

            // Safely get IsRequired - can throw if property has no parent
            var isRequired = false;
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

            JsonSchema actual = prop.ActualSchema;
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
            var message = error.Kind.ToString();

            // Make the message more user-friendly
            return error.Kind switch
            {
                ValidationErrorKind.PropertyRequired => $"Missing required property '{error.Property}'",
                ValidationErrorKind.NoAdditionalPropertiesAllowed => $"Unknown property '{error.Property}'",
                ValidationErrorKind.StringExpected => "Expected a string value",
                ValidationErrorKind.IntegerExpected => "Expected an integer value",
                ValidationErrorKind.NumberExpected => "Expected a number value",
                ValidationErrorKind.BooleanExpected => "Expected a boolean value (true/false)",
                ValidationErrorKind.ArrayExpected => "Expected an array value",
                ValidationErrorKind.ObjectExpected => "Expected an object/table value",
                ValidationErrorKind.NotInEnumeration => $"Value is not one of the allowed values",
                _ => message,
            };
        }
    }
}
