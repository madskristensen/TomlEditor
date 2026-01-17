namespace TomlEditor.Schema
{
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
}
