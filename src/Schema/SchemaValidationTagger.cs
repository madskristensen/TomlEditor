using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using NJsonSchema.Validation;
using Tomlyn.Syntax;

namespace TomlEditor.Schema
{
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IErrorTag))]
    [ContentType(Constants.LanguageName)]
    [Name("TOML Schema Validation")]
    internal sealed class SchemaValidationTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            return buffer.Properties.GetOrCreateSingletonProperty(() => new SchemaValidationTagger(buffer)) as ITagger<T>;
        }
    }

    internal sealed class SchemaValidationTagger : ITagger<IErrorTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly Document _document;
        private readonly TomlSchemaService _schemaService = new();
        private List<ITagSpan<IErrorTag>> _errorTags = [];
        private bool _isDisposed;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public SchemaValidationTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            _document = buffer.GetDocument();

            if (_document != null)
            {
                _document.Parsed += OnDocumentParsed;
            }
        }

        private void OnDocumentParsed(Document document)
        {
            _ = ValidateAsync();
        }

        private async Task ValidateAsync()
        {
            var errors = new List<ITagSpan<IErrorTag>>();
            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            var text = snapshot.GetText();
            var fileName = _document?.FileName;

            // Use NJsonSchema for validation (handles both directive and catalog matching)
            IList<SchemaValidationError> validationErrors = await _schemaService.ValidateAsync(text, fileName);

            foreach (SchemaValidationError error in validationErrors)
            {
                // Try to find the TOML span for this error
                SnapshotSpan? span = FindSpanForError(error, snapshot);

                if (span.HasValue)
                {
                    var errorType = IsWarning(error.Kind) 
                        ? PredefinedErrorTypeNames.Warning 
                        : PredefinedErrorTypeNames.SyntaxError;

                    var tag = new ErrorTag(errorType, error.Message);
                    errors.Add(new TagSpan<IErrorTag>(span.Value, tag));
                }
            }

            UpdateTags(errors);
        }

        private SnapshotSpan? FindSpanForError(SchemaValidationError error, ITextSnapshot snapshot)
        {
            if (_document?.Model == null)
            {
                return null;
            }

            var path = error.Path;
            var property = error.Property;

            // If there's a specific property, try to find it
            if (!string.IsNullOrEmpty(property))
            {
                var fullPath = string.IsNullOrEmpty(path) ? property : $"{path}.{property}";
                SourceSpan? sourceSpan = FindKeySpan(fullPath);

                if (sourceSpan.HasValue)
                {
                    return ToSnapshotSpan(sourceSpan.Value, snapshot);
                }
            }

            // Try to find by path
            if (!string.IsNullOrEmpty(path))
            {
                SourceSpan? sourceSpan = FindKeySpan(path);

                if (sourceSpan.HasValue)
                {
                    return ToSnapshotSpan(sourceSpan.Value, snapshot);
                }

                // Try to find the value span instead
                sourceSpan = FindValueSpan(path);

                if (sourceSpan.HasValue)
                {
                    return ToSnapshotSpan(sourceSpan.Value, snapshot);
                }
            }

            // Fallback: return first line of document
            if (snapshot.LineCount > 0)
            {
                ITextSnapshotLine firstLine = snapshot.GetLineFromLineNumber(0);
                return new SnapshotSpan(firstLine.Start, firstLine.End);
            }

            return null;
        }

        private SourceSpan? FindKeySpan(string path)
        {
            if (_document?.Model == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            var parts = path.Split('.');

            // Check root-level keys
            if (parts.Length == 1)
            {
                foreach (KeyValueSyntax kvp in _document.Model.KeyValues)
                {
                    if (kvp.Key?.ToString()?.Trim() == parts[0])
                    {
                        return kvp.Key.Span;
                    }
                }
            }

            // Check tables
            foreach (TableSyntaxBase table in _document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;

                // Check if the path starts with this table
                if (path.StartsWith(tableName + ".") || path == tableName)
                {
                    if (path == tableName)
                    {
                        return table.Name?.Span;
                    }

                    var remainder = path.Substring(tableName.Length + 1);
                    var keyToFind = remainder.Split('.')[0];

                    foreach (SyntaxNode item in table.Items)
                    {
                        if (item is KeyValueSyntax kvp && kvp.Key?.ToString()?.Trim() == keyToFind)
                        {
                            return kvp.Key.Span;
                        }
                    }
                }
            }

            return null;
        }

        private SourceSpan? FindValueSpan(string path)
        {
            if (_document?.Model == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            var parts = path.Split('.');

            // Check root-level keys
            if (parts.Length == 1)
            {
                foreach (KeyValueSyntax kvp in _document.Model.KeyValues)
                {
                    if (kvp.Key?.ToString()?.Trim() == parts[0])
                    {
                        return kvp.Value?.Span;
                    }
                }
            }

            // Check tables
            foreach (TableSyntaxBase table in _document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;

                if (path.StartsWith(tableName + "."))
                {
                    var remainder = path.Substring(tableName.Length + 1);
                    var keyToFind = remainder.Split('.')[0];

                    foreach (SyntaxNode item in table.Items)
                    {
                        if (item is KeyValueSyntax kvp && kvp.Key?.ToString()?.Trim() == keyToFind)
                        {
                            return kvp.Value?.Span;
                        }
                    }
                }
            }

            return null;
        }

        private static SnapshotSpan? ToSnapshotSpan(SourceSpan sourceSpan, ITextSnapshot snapshot)
        {
            try
            {
                var start = sourceSpan.Start.Offset;
                var length = sourceSpan.End.Offset - start;

                if (start >= 0 && start + length <= snapshot.Length && length > 0)
                {
                    return new SnapshotSpan(snapshot, start, length);
                }
            }
            catch
            {
                // Span calculation error
            }

            return null;
        }

        private static bool IsWarning(ValidationErrorKind kind)
        {
            switch (kind)
            {
                case ValidationErrorKind.NoAdditionalPropertiesAllowed:
                    return true;
                default:
                    return false;
            }
        }

        private void UpdateTags(List<ITagSpan<IErrorTag>> newTags)
        {
            _errorTags = newTags;

            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(_buffer.CurrentSnapshot, 0, _buffer.CurrentSnapshot.Length)));
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            return _errorTags;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                if (_document != null)
                {
                    _document.Parsed -= OnDocumentParsed;
                }

                _isDisposed = true;
            }
        }
    }
}
