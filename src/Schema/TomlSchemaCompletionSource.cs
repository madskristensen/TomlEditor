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

            // Check if there's a schema directive in the document
            string documentText = _textView.TextBuffer.CurrentSnapshot.GetText();
            if (string.IsNullOrEmpty(TomlSchemaService.GetSchemaUrl(documentText)))
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

            // Don't complete inside table headers [table] or [[array]]
            if (textBeforeCaret.TrimStart().StartsWith("["))
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
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

            return new CompletionStartData(
                CompletionParticipation.ProvidesItems,
                new SnapshotSpan(line.Start + wordStart, triggerLocation));
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
            CompletionContextInfo context = GetContextInfo(triggerLocation, document);

            var items = new List<CompletionItem>();

            if (context.IsValuePosition)
            {
                // Get property info to check for enums or booleans
                string propertyPath = string.IsNullOrEmpty(context.TablePath)
                    ? context.CurrentKey
                    : $"{context.TablePath}.{context.CurrentKey}";

                SchemaPropertyInfo propInfo = await _schemaService.GetPropertyInfoAsync(documentText, propertyPath);

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
                IEnumerable<SchemaCompletion> completions = await _schemaService.GetCompletionsAsync(documentText, context.TablePath);
                HashSet<string> existingKeys = GetExistingKeys(document, context.TablePath);

                foreach (SchemaCompletion completion in completions)
                {
                    // Skip keys that already exist
                    if (existingKeys.Contains(completion.Key))
                    {
                        continue;
                    }

                    ImageElement icon = completion.IsDeprecated ? DeprecatedIcon : KeyIcon;
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

        private CompletionContextInfo GetContextInfo(SnapshotPoint point, Document document)
        {
            var info = new CompletionContextInfo();

            ITextSnapshotLine line = point.GetContainingLine();
            string lineText = line.GetText();
            int column = point.Position - line.Start.Position;
            string textBeforeCaret = column > 0 ? lineText.Substring(0, column) : string.Empty;

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
        }
    }
}
