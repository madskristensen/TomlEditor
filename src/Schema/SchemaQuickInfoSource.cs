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
            Document document = _buffer.GetDocument();

            if (document?.Model == null)
            {
                return null;
            }

            string fileName = document.FileName;

            // Find the key at the trigger point
            KeyInfo keyInfo = FindKeyAtPosition(triggerPoint.Value, document);

            if (keyInfo == null)
            {
                return null;
            }

            // Get property info from schema (handles both directive and catalog matching)
            SchemaPropertyInfo propertyInfo = await _schemaService.GetPropertyInfoAsync(documentText, keyInfo.FullPath, fileName);

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

                    // Header: key name with type (styled like a declaration)
                    var headerRuns = new List<ClassifiedTextRun>
                    {
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, property.Name)
                    };

                    if (!string.IsNullOrEmpty(property.Type))
                    {
                        headerRuns.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Punctuation, ": "));
                        headerRuns.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, property.Type));
                    }

                    if (property.IsRequired)
                    {
                        headerRuns.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Punctuation, " "));
                        headerRuns.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, "(required)"));
                    }

                    elements.Add(new ClassifiedTextElement(headerRuns.ToArray()));

                    // Deprecated warning
                    if (property.IsDeprecated)
                    {
                        elements.Add(new ClassifiedTextElement(
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.PreprocessorKeyword, "⚠️ Deprecated")));
                    }

                    // Description - clean up markdown
                    if (!string.IsNullOrEmpty(property.Description))
                    {
                        string cleanDescription = CleanMarkdown(property.Description);
                        if (!string.IsNullOrWhiteSpace(cleanDescription))
                        {
                            elements.Add(new ClassifiedTextElement(
                                new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, cleanDescription)));
                        }
                    }

                    // Enum values
                    if (property.EnumValues != null && property.EnumValues.Length > 0)
                    {
                        int maxToShow = 10;
                        var valuesToShow = property.EnumValues.Take(maxToShow);
                        string enumList = string.Join(" | ", valuesToShow.Select(e => $"\"{e}\""));

                        if (property.EnumValues.Length > maxToShow)
                        {
                            enumList += $" ... (+{property.EnumValues.Length - maxToShow} more)";
                        }

                        var enumRuns = new List<ClassifiedTextRun>
                        {
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "Allowed: "),
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.String, enumList)
                        };

                        elements.Add(new ClassifiedTextElement(enumRuns.ToArray()));
                    }

                    // Default value
                    if (!string.IsNullOrEmpty(property.Default))
                    {
                        var defaultRuns = new List<ClassifiedTextRun>
                        {
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "Default: "),
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.String, property.Default)
                        };

                        elements.Add(new ClassifiedTextElement(defaultRuns.ToArray()));
                    }

                    return new ContainerElement(ContainerElementStyle.Stacked, elements);
                }

                /// <summary>
                /// Removes markdown formatting from text for cleaner tooltip display.
                /// </summary>
                private static string CleanMarkdown(string text)
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        return text;
                    }

                    // Remove code blocks (```...```)
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.Multiline);

                    // Remove inline code (`...`)
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");

                    // Remove markdown links [text](url) -> text
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

                    // Remove bold/italic markers
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\*([^*]+)\*", "$1");
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "$1");
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"_([^_]+)_", "$1");

                    // Remove headers (# Header)
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);

                    // Remove bullet points
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*[-*+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);

                    // Collapse multiple newlines
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");

                    // Trim and collapse whitespace
                    text = text.Trim();

                    return text;
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
