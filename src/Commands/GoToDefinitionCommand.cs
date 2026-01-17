using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using TomlEditor.Schema;
using Tomlyn.Syntax;

namespace TomlEditor.Commands
{
    /// <summary>
    /// Handles Go To Definition command for TOML files to navigate to schema definitions.
    /// </summary>
    public class GoToDefinitionCommand
    {
        private static readonly TomlSchemaService _schemaService = new TomlSchemaService();

        public static async Task InitializeAsync()
        {
            // Intercept the Go To Definition command for TOML files
            await VS.Commands.InterceptAsync(VSConstants.VSStd97CmdID.GotoDefn, Execute);
        }

        private static CommandProgression Execute()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                DocumentView doc = await VS.Documents.GetActiveDocumentViewAsync();

                if (doc?.TextBuffer == null || !doc.TextBuffer.ContentType.IsOfType(Constants.LanguageName))
                {
                    return CommandProgression.Continue;
                }

                Document document = doc.TextBuffer.GetDocument();
                if (document?.Model == null)
                {
                    return CommandProgression.Continue;
                }

                string documentText = doc.TextBuffer.CurrentSnapshot.GetText();
                string fileName = document.FileName;

                // Check if schema is available
                if (!TomlSchemaService.HasSchema(documentText, fileName))
                {
                    return CommandProgression.Continue;
                }

                // Get caret position
                int position = doc.TextView.Caret.Position.BufferPosition.Position;

                // Find the property path at the cursor position
                string propertyPath = FindPropertyPathAtPosition(position, document);
                if (string.IsNullOrEmpty(propertyPath))
                {
                    return CommandProgression.Continue;
                }

                // Navigate to schema definition
                bool navigated = await NavigateToSchemaAsync(documentText, propertyPath, fileName);

                return navigated ? CommandProgression.Stop : CommandProgression.Continue;
            });
        }

        private static async System.Threading.Tasks.Task<bool> NavigateToSchemaAsync(string documentText, string propertyPath, string fileName)
        {
            SchemaNavigationInfo navInfo = await _schemaService.GetSchemaNavigationInfoAsync(documentText, propertyPath, fileName);

            if (navInfo == null || string.IsNullOrEmpty(navInfo.FilePath))
            {
                return false;
            }

            // Open the cached schema file in Visual Studio as a preview document
            DocumentView schemaDoc = await VS.Documents.OpenInPreviewTabAsync(navInfo.FilePath);
            if (schemaDoc?.TextView == null)
            {
                return false;
            }

            // Navigate to the specific line
            if (navInfo.LineNumber > 0)
            {
                ITextSnapshot snapshot = schemaDoc.TextView.TextBuffer.CurrentSnapshot;
                int targetLine = Math.Min(navInfo.LineNumber - 1, snapshot.LineCount - 1);
                ITextSnapshotLine line = snapshot.GetLineFromLineNumber(targetLine);

                // Move caret to the line
                schemaDoc.TextView.Caret.MoveTo(line.Start);

                // Center the line in the view
                schemaDoc.TextView.ViewScroller.EnsureSpanVisible(
                    new SnapshotSpan(line.Start, line.End),
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }

            return true;
                }

                        /// <summary>
                        /// Finds the full property path for the TOML key at the specified position.
                        /// </summary>
                        private static string FindPropertyPathAtPosition(int position, Document document)
                        {
                            // Check root-level key-value pairs
                            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
                            {
                                if (kvp.Key != null && kvp.Key.Span.ContainsPosition(position))
                                {
                                    return kvp.Key.ToString()?.Trim();
                                }

                                // Also check if cursor is on the value
                                if (kvp.Value != null && kvp.Value.Span.ContainsPosition(position))
                                {
                                    return kvp.Key.ToString()?.Trim();
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
                                if (table.Name != null && table.Name.Span.ContainsPosition(position))
                                {
                                    return currentTablePath;
                                }

                                foreach (SyntaxNode item in table.Items)
                                {
                                    if (item is KeyValueSyntax kvp)
                                    {
                                        bool keyMatch = kvp.Key != null && kvp.Key.Span.ContainsPosition(position);
                                        bool valueMatch = kvp.Value != null && kvp.Value.Span.ContainsPosition(position);

                                        if (keyMatch || valueMatch)
                                        {
                                            string keyName = kvp.Key.ToString()?.Trim();
                                            return string.IsNullOrEmpty(currentTablePath)
                                                ? keyName
                                                : $"{currentTablePath}.{keyName}";
                                        }
                                    }
                                }
                            }

                            return null;
                        }
                    }
                }
