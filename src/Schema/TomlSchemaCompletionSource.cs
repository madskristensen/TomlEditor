using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Tomlyn.Syntax;

namespace TomlEditor.Schema
{
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [ContentType(Constants.LanguageName)]
    [Name("TOML Schema Completion")]
    internal sealed class TomlSchemaCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            return textView.Properties.GetOrCreateSingletonProperty(() => new TomlSchemaCompletionSource(textView));
        }
    }

    internal sealed class TomlSchemaCompletionSource : IAsyncCompletionSource
    {
        private static readonly ImageElement KeyIcon = new ImageElement(KnownMonikers.Property.ToImageId(), "Key");
        private static readonly ImageElement ValueIcon = new ImageElement(KnownMonikers.EnumerationItemPublic.ToImageId(), "Value");
        private static readonly ImageElement TableIcon = new ImageElement(KnownMonikers.TableGroup.ToImageId(), "Table");
        private static readonly ImageElement DeprecatedIcon = new ImageElement(KnownMonikers.StatusWarning.ToImageId(), "Deprecated");

        private readonly ITextView _textView;
        private readonly TomlSchemaService _schemaService = new TomlSchemaService();

        public TomlSchemaCompletionSource(ITextView textView)
        {
            _textView = textView;
        }

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            // Don't participate on deletion
            if (trigger.Reason == CompletionTriggerReason.Deletion)
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            // Check if there's a schema available (directive or catalog match)
            string documentText = _textView.TextBuffer.CurrentSnapshot.GetText();
            Document document = _textView.TextBuffer.GetDocument();
            string fileName = document?.FileName;

            if (!TomlSchemaService.HasSchema(documentText, fileName))
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            ITextSnapshotLine line = triggerLocation.GetContainingLine();
            string lineText = line.GetText();
            int column = triggerLocation.Position - line.Start.Position;
            string textBeforeCaret = column > 0 ? lineText.Substring(0, column) : string.Empty;

            // Don't complete in comments
            if (textBeforeCaret.TrimStart().StartsWith("#"))
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            // Check if we're inside a table header [table] or [[array]]
            string trimmed = textBeforeCaret.TrimStart();
            if (trimmed.StartsWith("[[") || trimmed.StartsWith("["))
            {
                // Table header completion - find start of table name after [ or [[
                int bracketStart = textBeforeCaret.LastIndexOf('[');
                int tableNameStart = bracketStart + 1;

                // Skip second bracket for array tables [[
                if (bracketStart > 0 && textBeforeCaret[bracketStart - 1] == '[')
                {
                    // Already handled by LastIndexOf finding the second [
                }
                else if (bracketStart + 1 < textBeforeCaret.Length && textBeforeCaret[bracketStart + 1] == '[')
                {
                    tableNameStart = bracketStart + 2;
                }

                return new CompletionStartData(
                    CompletionParticipation.ProvidesItems,
                    new SnapshotSpan(line.Start + tableNameStart, triggerLocation));
            }

            // Check if we're after an '=' sign (value position)
            int equalsIndex = textBeforeCaret.IndexOf('=');

            if (equalsIndex >= 0)
            {
                // Value completion - find the start of the value
                string afterEquals = textBeforeCaret.Substring(equalsIndex + 1).TrimStart();
                int valueStart = triggerLocation.Position - afterEquals.Length;

                return new CompletionStartData(
                    CompletionParticipation.ProvidesItems,
                    new SnapshotSpan(triggerLocation.Snapshot, valueStart, afterEquals.Length));
            }

            // Key completion - find the start of the current word
            int wordStart = column;

            while (wordStart > 0 && IsKeyChar(lineText[wordStart - 1]))
            {
                wordStart--;
            }

            // Create applicable span - from word start to caret position
            // This can be a zero-length span for invocation on blank line, which is valid
            SnapshotSpan applicableSpan = new SnapshotSpan(line.Start + wordStart, triggerLocation);

            // Always provide completions for key position (both for typing and Ctrl+Space invocation)
            return new CompletionStartData(
                CompletionParticipation.ProvidesItems,
                applicableSpan);
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            string documentText = _textView.TextBuffer.CurrentSnapshot.GetText();
            Document document = _textView.TextBuffer.GetDocument();
            string fileName = document?.FileName;
            CompletionContextInfo context = GetContextInfo(triggerLocation, document);

            var items = new List<CompletionItem>();

            if (context.IsTableHeader)
            {
                // Get table name completions from schema
                IEnumerable<SchemaCompletion> completions = await _schemaService.GetTableCompletionsAsync(documentText, context.PartialTableName, fileName);
                HashSet<string> existingTables = GetExistingTableNames(document);

                foreach (SchemaCompletion completion in completions)
                {
                    // Skip tables that already exist (unless it's an array table which can have multiple)
                    if (!context.IsArrayTable && existingTables.Contains(completion.Key))
                    {
                        continue;
                    }

                    ImageElement icon = completion.IsDeprecated ? DeprecatedIcon : TableIcon;
                    string displayText = completion.IsDeprecated ? $"{completion.Key} (deprecated)" : completion.Key;
                    string insertText = completion.Key;

                    var item = new CompletionItem(
                        displayText,
                        this,
                        icon,
                        ImmutableArray<CompletionFilter>.Empty,
                        string.Empty,
                        insertText,
                        insertText,
                        insertText,
                        ImmutableArray<ImageElement>.Empty);

                    item.Properties.AddProperty("Description", completion.Description ?? string.Empty);
                    item.Properties.AddProperty("Type", completion.Type ?? string.Empty);
                    item.Properties.AddProperty("IsDeprecated", completion.IsDeprecated);

                    items.Add(item);
                }
            }
            else if (context.IsValuePosition)
            {
                // Get property info to check for enums or booleans
                string propertyPath = string.IsNullOrEmpty(context.TablePath)
                    ? context.CurrentKey
                    : $"{context.TablePath}.{context.CurrentKey}";

                SchemaPropertyInfo propInfo = await _schemaService.GetPropertyInfoAsync(documentText, propertyPath, fileName);

                if (propInfo != null)
                {
                    // Add enum completions
                    if (propInfo.EnumValues != null)
                    {
                        foreach (string value in propInfo.EnumValues)
                        {
                            string displayText = $"\"{value}\"";
                            items.Add(new CompletionItem(displayText, this, ValueIcon));
                        }
                    }
                    // Add boolean completions
                    else if (propInfo.Type == "boolean")
                    {
                        items.Add(new CompletionItem("true", this, ValueIcon));
                        items.Add(new CompletionItem("false", this, ValueIcon));
                    }
                }
            }
            else
            {
                // Get key completions from schema
                IEnumerable<SchemaCompletion> completions = await _schemaService.GetCompletionsAsync(documentText, context.TablePath, fileName);
                HashSet<string> existingKeys = GetExistingKeys(document, context.TablePath);
                HashSet<string> existingTables = GetExistingTableNames(document);

                foreach (SchemaCompletion completion in completions)
                {
                    // Skip keys that already exist
                    if (existingKeys.Contains(completion.Key))
                    {
                        continue;
                    }

                    // For tables at root level, also skip if the table already exists
                    if (completion.IsTable && string.IsNullOrEmpty(context.TablePath) && existingTables.Contains(completion.Key))
                    {
                        continue;
                    }

                    // Use TableIcon for table completions, otherwise KeyIcon
                    ImageElement icon = completion.IsDeprecated ? DeprecatedIcon : (completion.IsTable ? TableIcon : KeyIcon);

                    // For tables, show with brackets in display and include type info
                    string displayText;
                    if (completion.IsTable)
                    {
                        displayText = completion.IsDeprecated ? $"[{completion.Key}] (deprecated)" : $"[{completion.Key}]";
                    }
                    else
                    {
                        displayText = completion.IsDeprecated ? $"{completion.Key} (deprecated)" : completion.Key;
                    }

                    // For tables at root level, insert with brackets
                    string insertText;
                    if (completion.IsTable && string.IsNullOrEmpty(context.TablePath))
                    {
                        insertText = $"[{completion.Key}]";
                    }
                    else
                    {
                        insertText = completion.Key;
                    }

                    var item = new CompletionItem(
                        displayText,
                        this,
                        icon,
                        ImmutableArray<CompletionFilter>.Empty,
                        string.Empty,
                        insertText,
                        completion.Key, // Sort text - use key without brackets for proper alphabetical sort
                        completion.Key, // Filter text - use key without brackets for filtering
                        ImmutableArray<ImageElement>.Empty);

                    item.Properties.AddProperty("Description", completion.Description ?? string.Empty);
                    item.Properties.AddProperty("Type", completion.Type ?? string.Empty);
                    item.Properties.AddProperty("IsDeprecated", completion.IsDeprecated);
                    item.Properties.AddProperty("IsTable", completion.IsTable);

                    items.Add(item);
                }
            }

            return new CompletionContext(items.ToImmutableArray());
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
        {
            if (item.Properties.TryGetProperty("Description", out string description) && !string.IsNullOrEmpty(description))
            {
                var elements = new List<object>();

                if (item.Properties.TryGetProperty("Type", out string type) && !string.IsNullOrEmpty(type))
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, $"({type}) ")));
                }

                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, description)));

                if (item.Properties.TryGetProperty("IsDeprecated", out bool isDeprecated) && isDeprecated)
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.ExcludedCode, " [Deprecated]")));
                }

                return Task.FromResult<object>(new ContainerElement(ContainerElementStyle.Stacked, elements));
            }

            return Task.FromResult<object>(null);
        }

        private static HashSet<string> GetExistingKeys(Document document, string tablePath)
        {
            var keys = new HashSet<string>();

            if (document?.Model == null)
            {
                return keys;
            }

            if (string.IsNullOrEmpty(tablePath))
            {
                // Root level - collect keys before first table
                foreach (KeyValueSyntax kvp in document.Model.KeyValues)
                {
                    string key = kvp.Key?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        keys.Add(key);
                    }
                }
            }
            else
            {
                foreach (TableSyntaxBase table in document.Model.Tables)
                {
                    if (table.Name?.ToString()?.Trim() == tablePath)
                    {
                        foreach (KeyValueSyntax kvp in table.Items.OfType<KeyValueSyntax>())
                        {
                            string key = kvp.Key?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(key))
                            {
                                keys.Add(key);
                            }
                        }
                        break;
                    }
                }
            }

                return keys;
            }

            private static HashSet<string> GetExistingTableNames(Document document)
            {
                var tables = new HashSet<string>();

                if (document?.Model == null)
                {
                    return tables;
                }

                foreach (TableSyntaxBase table in document.Model.Tables)
                {
                    string tableName = table.Name?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        tables.Add(tableName);
                    }
                }

                return tables;
            }

            private CompletionContextInfo GetContextInfo(SnapshotPoint point, Document document)
        {
            var info = new CompletionContextInfo();

            ITextSnapshotLine line = point.GetContainingLine();
            string lineText = line.GetText();
            int column = point.Position - line.Start.Position;
            string textBeforeCaret = column > 0 ? lineText.Substring(0, column) : string.Empty;

            // Check if we're inside a table header [table] or [[array]]
            string trimmed = textBeforeCaret.TrimStart();
            if (trimmed.StartsWith("[[") || trimmed.StartsWith("["))
            {
                info.IsTableHeader = true;

                // Extract the partial table name typed so far
                int bracketStart = textBeforeCaret.LastIndexOf('[');
                int tableNameStart = bracketStart + 1;
                if (bracketStart + 1 < textBeforeCaret.Length && textBeforeCaret[bracketStart + 1] == '[')
                {
                    tableNameStart = bracketStart + 2;
                    info.IsArrayTable = true;
                }

                info.PartialTableName = textBeforeCaret.Substring(tableNameStart).Trim();
                return info;
            }

            // Check if we're in value position (after '=')
            int equalsIndex = textBeforeCaret.IndexOf('=');

            if (equalsIndex >= 0)
            {
                info.IsValuePosition = true;
                info.CurrentKey = textBeforeCaret.Substring(0, equalsIndex).Trim();
            }

            // Find current table path
            info.TablePath = GetCurrentTablePath(point, document);

            return info;
        }

        private static string GetCurrentTablePath(SnapshotPoint point, Document document)
        {
            if (document?.Model == null)
            {
                return string.Empty;
            }

            int position = point.Position;
            string tablePath = string.Empty;

            // Find the last table header before the current position
            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                if (table.Span.Start.Offset < position)
                {
                    tablePath = table.Name?.ToString()?.Trim() ?? string.Empty;
                }
                else
                {
                    break;
                }
            }

            return tablePath;
        }

        private static bool IsKeyChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
        }

                private class CompletionContextInfo
                {
                    public string TablePath { get; set; } = string.Empty;
                    public string CurrentKey { get; set; } = string.Empty;
                    public bool IsValuePosition { get; set; }
                    public bool IsTableHeader { get; set; }
                    public bool IsArrayTable { get; set; }
                    public string PartialTableName { get; set; } = string.Empty;
                }
            }
        }
