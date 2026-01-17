namespace TomlEditor.Schema
{
    /// <summary>
    /// Represents a completion item from the schema.
    /// </summary>
    public class SchemaCompletion
    {
        public string Key { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public bool IsDeprecated { get; set; }

        /// <summary>
        /// Indicates whether this completion represents a table (object type) rather than a simple key-value pair.
        /// </summary>
        public bool IsTable { get; set; }
    }
}
