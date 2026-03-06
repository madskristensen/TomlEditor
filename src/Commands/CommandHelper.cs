using System.Linq;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Shell;
using NJsonSchema.Validation;
using TomlEditor.Schema;
using Tomlyn.Syntax;

namespace TomlEditor.Commands
{
    /// <summary>
    /// Provides helper methods for executing commands that operate on TOML documents.
    /// </summary>
    internal static class CommandHelper
    {
        /// <summary>
        /// Executes an action on the active TOML document.
        /// Returns Stop if the action was executed, Continue otherwise.
        /// </summary>
        public static CommandProgression ExecuteOnTomlDocument(Action<DocumentView> action)
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                DocumentView doc = await VS.Documents.GetActiveDocumentViewAsync();

                if (doc?.TextBuffer != null && doc.TextBuffer.ContentType.IsOfType(Constants.LanguageName))
                {
                    action(doc);
                    return CommandProgression.Stop;
                }

                return CommandProgression.Continue;
            });
        }
    }

    /// <summary>
    /// Applies simple schema-driven quick fixes for TOML documents.
    /// </summary>
    internal static class SchemaQuickFixCommand
    {
        private const string SchemaDirectiveTemplate = "#:schema <url>";

        /// <summary>
        /// Initializes schema quick-fix command interception.
        /// </summary>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            CommandID commandId = new(PackageGuids.TomlEditor, PackageIds.ApplySchemaQuickFix);
            OleMenuCommand command = new(async (_, _) => await ExecuteAsync(), commandId);
            command.BeforeQueryStatus += (_, _) =>
            {
                var isToml = ThreadHelper.JoinableTaskFactory.Run(() => IsTomlDocumentActiveAsync());
                command.Enabled = isToml;
                command.Visible = isToml;
            };

            commandService.AddCommand(command);
        }

        private static async System.Threading.Tasks.Task<bool> IsTomlDocumentActiveAsync()
        {
            DocumentView doc = await VS.Documents.GetActiveDocumentViewAsync();
            return doc?.TextBuffer != null && doc.TextBuffer.ContentType.IsOfType(Constants.LanguageName);
        }

        private static async Task ExecuteAsync()
        {
            DocumentView doc = await VS.Documents.GetActiveDocumentViewAsync();

            if (doc?.TextBuffer == null || !doc.TextBuffer.ContentType.IsOfType(Constants.LanguageName))
            {
                return;
            }

            ITextSnapshot snapshot = doc.TextBuffer.CurrentSnapshot;
            var text = snapshot.GetText();
            Document document = doc.TextBuffer.GetDocument();
            var fileName = document?.FileName;

            if (!TomlSchemaService.HasSchema(text, fileName))
            {
                doc.TextBuffer.Insert(0, SchemaDirectiveTemplate + Environment.NewLine);
                await VS.StatusBar.ShowMessageAsync("Inserted schema directive template.");
                return;
            }

            if (document?.Model == null)
            {
                await VS.StatusBar.ShowMessageAsync("TOML model is not ready yet. Try again.");
                return;
            }

            var schemaService = TomlSchemaService.Shared;
            var errors = await schemaService.ValidateAsync(text, fileName);
            if (errors == null || errors.Count == 0)
            {
                await VS.StatusBar.ShowMessageAsync("No schema fixes available.");
                return;
            }

            foreach (SchemaValidationError error in errors)
            {
                if (TryApplyUnknownKeyFix(doc.TextBuffer, snapshot, document, error))
                {
                    await VS.StatusBar.ShowMessageAsync("Applied schema fix: removed unknown key.");
                    return;
                }

                if (TryApplyMissingRequiredFix(doc.TextBuffer, snapshot, document, error))
                {
                    await VS.StatusBar.ShowMessageAsync("Applied schema fix: added missing required key.");
                    return;
                }
            }

            await VS.StatusBar.ShowMessageAsync("No schema fixes available.");
        }

        private static bool TryApplyUnknownKeyFix(ITextBuffer buffer, ITextSnapshot snapshot, Document document, SchemaValidationError error)
        {
            if (error.Kind != ValidationErrorKind.NoAdditionalPropertiesAllowed)
            {
                return false;
            }

            SourceSpan? keySpan = FindKeySpan(document, BuildFullPath(error.Path, error.Property));
            if (!keySpan.HasValue)
            {
                return false;
            }

            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(keySpan.Value.Start.Line);
            var deleteStart = line.Start.Position;
            var deleteLength = line.LengthIncludingLineBreak;

            if (deleteLength <= 0)
            {
                return false;
            }

            buffer.Delete(new Span(deleteStart, deleteLength));
            return true;
        }

        private static bool TryApplyMissingRequiredFix(ITextBuffer buffer, ITextSnapshot snapshot, Document document, SchemaValidationError error)
        {
            if (error.Kind != ValidationErrorKind.PropertyRequired || string.IsNullOrWhiteSpace(error.Property))
            {
                return false;
            }

            var tablePath = error.Path?.Trim('.');
            var insertText = $"{error.Property} = " + Environment.NewLine;

            if (string.IsNullOrEmpty(tablePath))
            {
                var rootInsertPosition = 0;
                if (document.Model.KeyValues.Any())
                {
                    var lastKey = document.Model.KeyValues.Last();
                    rootInsertPosition = lastKey.Span.End.Offset;
                    var line = snapshot.GetLineFromPosition(Math.Min(rootInsertPosition, snapshot.Length > 0 ? snapshot.Length - 1 : 0));
                    rootInsertPosition = line.EndIncludingLineBreak.Position;
                }

                buffer.Insert(rootInsertPosition, insertText);
                return true;
            }

            TableSyntaxBase matchingTable = document.Model.Tables.LastOrDefault(t => string.Equals(t.Name?.ToString()?.Trim(), tablePath, StringComparison.Ordinal));
            if (matchingTable == null)
            {
                return false;
            }

            ITextSnapshotLine tableLine = snapshot.GetLineFromLineNumber(matchingTable.Span.Start.Line);
            var indentation = GetLeadingWhitespace(tableLine.GetText()) + "  ";
            insertText = indentation + insertText;

            var insertPosition = snapshot.GetLineFromLineNumber(matchingTable.Span.End.Line).EndIncludingLineBreak.Position;
            buffer.Insert(insertPosition, insertText);
            return true;
        }

        private static SourceSpan? FindKeySpan(Document document, string path)
        {
            if (document?.Model == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (kvp.Key?.ToString()?.Trim() == path)
                {
                    return kvp.Key.Span;
                }
            }

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;

                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp)
                    {
                        var keyName = kvp.Key?.ToString()?.Trim();
                        var fullPath = string.IsNullOrEmpty(tableName) ? keyName : $"{tableName}.{keyName}";
                        if (fullPath == path)
                        {
                            return kvp.Key?.Span;
                        }
                    }
                }
            }

            return null;
        }

        private static string BuildFullPath(string path, string property)
        {
            if (string.IsNullOrEmpty(property))
            {
                return path;
            }

            return string.IsNullOrEmpty(path) ? property : $"{path}.{property}";
        }

        private static string GetLeadingWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var index = 0;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return text.Substring(0, index);
        }
    }
}
