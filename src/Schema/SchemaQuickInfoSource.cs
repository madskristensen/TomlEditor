using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using Tomlyn.Syntax;

namespace TomlEditor.Schema
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [ContentType(Constants.LanguageName)]
    [Name("TOML Schema QuickInfo")]
    [Order(Before = "Default Quick Info Presenter")]
    internal sealed class SchemaQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            return textBuffer.Properties.GetOrCreateSingletonProperty(() => new SchemaQuickInfoSource(textBuffer));
        }
    }

    internal sealed class SchemaQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _buffer;
        private readonly TomlSchemaService _schemaService = new TomlSchemaService();

        public SchemaQuickInfoSource(ITextBuffer buffer)
        {
            _buffer = buffer;
        }

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(
            IAsyncQuickInfoSession session,
            CancellationToken cancellationToken)
        {
            SnapshotPoint? triggerPoint = session.GetTriggerPoint(_buffer.CurrentSnapshot);

            if (!triggerPoint.HasValue)
            {
                return null;
            }

            string documentText = _buffer.CurrentSnapshot.GetText();

            // Check if schema is specified
            if (string.IsNullOrEmpty(TomlSchemaService.GetSchemaUrl(documentText)))
            {
                return null;
            }

            Document document = _buffer.GetDocument();

            if (document?.Model == null)
            {
                return null;
            }

            // Find the key at the trigger point
            KeyInfo keyInfo = FindKeyAtPosition(triggerPoint.Value, document);

            if (keyInfo == null)
            {
                return null;
            }

            // Get property info from schema
            SchemaPropertyInfo propertyInfo = await _schemaService.GetPropertyInfoAsync(documentText, keyInfo.FullPath);

            if (propertyInfo == null)
            {
                return null;
            }

            // Build the tooltip content
            object content = BuildTooltipContent(propertyInfo);
            ITrackingSpan applicableSpan = _buffer.CurrentSnapshot.CreateTrackingSpan(
                keyInfo.Span,
                SpanTrackingMode.EdgeInclusive);

            return new QuickInfoItem(applicableSpan, content);
        }

        private static KeyInfo FindKeyAtPosition(SnapshotPoint point, Document document)
        {
            int position = point.Position;

            // Check root-level key-value pairs
            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (IsPositionInSpan(position, kvp.Key?.Span))
                {
                    string keyName = kvp.Key.ToString()?.Trim();
                    return new KeyInfo
                    {
                        KeyName = keyName,
                        FullPath = keyName,
                        Span = kvp.Key.Span.ToSpan()
                    };
                }
            }

            // Check tables
            string currentTablePath = string.Empty;

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                if (table.Span.Start.Offset > position)
                {
                    break;
                }

                currentTablePath = table.Name?.ToString()?.Trim() ?? string.Empty;

                // Check table name hover
                if (IsPositionInSpan(position, table.Name?.Span))
                {
                    return new KeyInfo
                    {
                        KeyName = currentTablePath,
                        FullPath = currentTablePath,
                        Span = table.Name.Span.ToSpan()
                    };
                }

                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp && IsPositionInSpan(position, kvp.Key?.Span))
                    {
                        string keyName = kvp.Key.ToString()?.Trim();

                        return new KeyInfo
                        {
                            KeyName = keyName,
                            FullPath = string.IsNullOrEmpty(currentTablePath)
                                ? keyName
                                : $"{currentTablePath}.{keyName}",
                            Span = kvp.Key.Span.ToSpan()
                        };
                    }
                }
            }

            return null;
        }

        private static bool IsPositionInSpan(int position, SourceSpan? span)
        {
            if (!span.HasValue)
            {
                return false;
            }

            return position >= span.Value.Start.Offset && position <= span.Value.End.Offset;
        }

        private static object BuildTooltipContent(SchemaPropertyInfo property)
        {
            var elements = new List<object>();

            // Header: key name with type
            string header = string.IsNullOrEmpty(property.Type)
                ? property.Name
                : $"{property.Name}: {property.Type}";

            elements.Add(new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, header)));

            // Deprecated warning
            if (property.IsDeprecated)
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.ExcludedCode, "⚠️ Deprecated")));
            }

            // Required indicator
            if (property.IsRequired)
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, "(required)")));
            }

            // Description
            if (!string.IsNullOrEmpty(property.Description))
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, property.Description)));
            }

            // Enum values
            if (property.EnumValues != null && property.EnumValues.Length > 0)
            {
                string enumValues = string.Join(", ", property.EnumValues.Select(e => $"'{e}'"));
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, $"Allowed: {enumValues}")));
                }

                // Default value
                if (!string.IsNullOrEmpty(property.Default))
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, $"Default: {property.Default}")));
                }

                return new ContainerElement(ContainerElementStyle.Stacked, elements);
            }

            public void Dispose()
            {
                // Nothing to dispose
            }

        private class KeyInfo
        {
            public string KeyName { get; set; }
            public string FullPath { get; set; }
            public Span Span { get; set; }
        }
    }
}
