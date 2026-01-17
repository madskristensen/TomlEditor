using System.Collections.Generic;
using System.IO;
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
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromDays(7);
        private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "TomlEditor", "Schemas");
        private static readonly string CatalogCacheFile = Path.Combine(CacheDirectory, "catalog.json");

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
            HttpClient.Timeout = TimeSpan.FromSeconds(10);

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
        /// Gets navigation information to the schema definition for a property path.
        /// Returns the cached file path and line number for navigation.
        /// </summary>
        public async Task<SchemaNavigationInfo> GetSchemaNavigationInfoAsync(string documentText, string propertyPath, string fileName = null)
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

            // Get or create cached schema file
            string cachedFilePath = await GetOrCreateCachedSchemaFileAsync(url);
            if (string.IsNullOrEmpty(cachedFilePath))
            {
                return null;
            }

            // Find the line number for the property path
            int lineNumber = FindPropertyLineInSchema(cachedFilePath, propertyPath);

            return new SchemaNavigationInfo
            {
                FilePath = cachedFilePath,
                        LineNumber = lineNumber,
                        PropertyPath = propertyPath
                    };
                }

                private static readonly Dictionary<string, string> _schemaFileCache = new Dictionary<string, string>();
                private static readonly object _fileCacheLock = new object();

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
                    return DateTime.UtcNow - lastWriteTime < CacheExpiration;
                }

                /// <summary>
                /// Gets or creates a cached schema file on disk for the given URL.
                /// </summary>
                private async Task<string> GetOrCreateCachedSchemaFileAsync(string url)
                {
                    string schemaName = GetSchemaNameFromUrl(url);
                    Directory.CreateDirectory(CacheDirectory);
                    string cachedFilePath = Path.Combine(CacheDirectory, schemaName + ".json");

                    // Check if we have a valid cached file
                    lock (_fileCacheLock)
                    {
                        if (_schemaFileCache.TryGetValue(url, out string existingPath) && IsCacheValid(existingPath))
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
                            string filePath = new Uri(url).LocalPath;
                            schemaContent = File.ReadAllText(filePath);
                        }
                        else
                        {
                            schemaContent = await HttpClient.GetStringAsync(url);
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
                                string name = Path.GetFileNameWithoutExtension(uri.LocalPath);
                                if (string.IsNullOrEmpty(name))
                                {
                                    name = uri.Host.Replace(".", "_");
                                }
                                // Sanitize the name for use as a filename
                                foreach (char c in Path.GetInvalidFileNameChars())
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
                                string[] lines = File.ReadAllLines(filePath);
                                string[] pathParts = propertyPath.Split('.');
                                string targetProperty = pathParts[pathParts.Length - 1];

                                // Search for the property definition pattern: "propertyName": {
                                string searchPattern = $"\"{targetProperty}\"";

                                // Track nesting to find the right property at the correct depth
                                int propertiesDepth = 0;
                                int targetDepth = pathParts.Length;

                                for (int i = 0; i < lines.Length; i++)
                                {
                                    string line = lines[i];

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
                                for (int i = 0; i < lines.Length; i++)
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

                    return targetSchema.ActualProperties.Select(kvp =>
                    {
                        var actualSchema = kvp.Value.ActualSchema;

                        // Check if this property represents a table (object type)
                        bool isTable = actualSchema?.Type == JsonObjectType.Object ||
                                       actualSchema?.ActualProperties?.Count > 0;

                        // Check if this property represents an array of tables
                        bool isArrayOfTables = actualSchema?.Type == JsonObjectType.Array &&
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
                        return Enumerable.Empty<SchemaCompletion>();
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

                    foreach (var kvp in schema.ActualProperties)
                    {
                        string fullPath = string.IsNullOrEmpty(currentPath) ? kvp.Key : $"{currentPath}.{kvp.Key}";
                        var actualSchema = kvp.Value.ActualSchema;

                        // Check if this property represents a table (object type)
                        bool isTable = actualSchema?.Type == JsonObjectType.Object ||
                                       actualSchema?.ActualProperties?.Count > 0;

                        // Check if this property represents an array of tables
                        bool isArrayOfTables = actualSchema?.Type == JsonObjectType.Array &&
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
                            var nestedSchema = isArrayOfTables ? actualSchema?.Item : actualSchema;
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
                            Directory.CreateDirectory(CacheDirectory);

                            string json = null;

                            // Try to load from cache first
                            if (IsCacheValid(CatalogCacheFile))
                            {
                                json = File.ReadAllText(CatalogCacheFile);
                            }
                            else
                            {
                                // Download fresh catalog
                                json = await HttpClient.GetStringAsync(SchemaStoreCatalogUrl);
                                File.WriteAllText(CatalogCacheFile, json);
                            }

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
                            // If download fails, try to use old cached catalog
                            if (File.Exists(CatalogCacheFile))
                            {
                                try
                                {
                                    string json = File.ReadAllText(CatalogCacheFile);
                                    var catalog = JsonConvert.DeserializeObject<SchemaStoreCatalog>(json);
                                    if (catalog?.Schemas != null)
                                    {
                                        lock (_catalogLock)
                                        {
                                            _catalogEntries = catalog.Schemas
                                                .Where(s => s.FileMatch != null && s.FileMatch.Any(f => f.EndsWith(".toml")))
                                                .ToList();
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
                        string name = Path.GetFileName(fileName);
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
                                            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                                            matcher.AddInclude(pattern);

                                            // Try matching against the full path first
                                            string directory = Path.GetDirectoryName(fullPath) ?? ".";
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
                                        string filePath = new Uri(url).LocalPath;
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
                    }
