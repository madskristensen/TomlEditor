namespace TomlEditor.Schema
{
    /// <summary>
    /// Represents navigation information to a schema definition.
    /// </summary>
    public class SchemaNavigationInfo
    {
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public string PropertyPath { get; set; }
    }
}
