using Newtonsoft.Json;

namespace TomlEditor.Schema
{
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
