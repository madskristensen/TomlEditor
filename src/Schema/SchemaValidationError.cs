using NJsonSchema.Validation;

namespace TomlEditor.Schema
{
    /// <summary>
    /// Represents a schema validation error with path information.
    /// </summary>
    public class SchemaValidationError
    {
        public string Path { get; set; }
        public string Property { get; set; }
        public string Message { get; set; }
        public ValidationErrorKind Kind { get; set; }
    }
}
