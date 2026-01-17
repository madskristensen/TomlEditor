using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Tomlyn.Syntax;

namespace TomlEditor.Commands
{
    /// <summary>
    /// Handles Find All References command for TOML keys.
    /// Shows all occurrences of a key in the current document.
    /// </summary>
    public class FindAllReferencesCommand
    {
        public static async Task InitializeAsync()
        {
            // Intercept the Find All References command for TOML files
            await VS.Commands.InterceptAsync(VSConstants.VSStd97CmdID.FindReferences, Execute);
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

                // Get caret position
                var position = doc.TextView.Caret.Position.BufferPosition.Position;

                // Find the key name at the cursor position
                var keyInfo = FindKeyAtPosition(position, document);
                if (keyInfo == null || string.IsNullOrEmpty(keyInfo.KeyName))
                {
                    await VS.StatusBar.ShowMessageAsync("No TOML key found at cursor position.");
                    return CommandProgression.Stop;
                }

                // Find all references to this key
                var references = FindAllKeyReferences(document, keyInfo.KeyName, keyInfo.TablePrefix);

                if (references.Count == 0)
                {
                    await VS.StatusBar.ShowMessageAsync($"No references found for '{keyInfo.DisplayName}'.");
                    return CommandProgression.Stop;
                }

                // Display results in Find Symbol Results window
                await ShowReferencesAsync(document.FileName, keyInfo.DisplayName, references);

                return CommandProgression.Stop;
            });
        }

        private static KeyInfo FindKeyAtPosition(int position, Document document)
        {
            // Check root-level key-value pairs
            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (kvp.Key != null && kvp.Key.Span.ContainsPosition(position))
                {
                    return new KeyInfo
                    {
                        KeyName = GetSimpleKeyName(kvp.Key),
                        TablePrefix = string.Empty,
                        DisplayName = kvp.Key.ToString()?.Trim()
                    };
                }
            }

            // Check tables
            var currentTablePath = string.Empty;

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                if (table.Span.Start.Offset > position)
                {
                    break;
                }

                currentTablePath = table.Name?.ToString()?.Trim() ?? string.Empty;

                // Check if cursor is on table name
                if (table.Name != null && table.Name.Span.ContainsPosition(position))
                {
                    // For table names, we want to find all references to keys within this table
                    return new KeyInfo
                    {
                        KeyName = string.Empty,
                        TablePrefix = currentTablePath,
                        DisplayName = currentTablePath,
                        IsTableName = true
                    };
                }

                // Check keys within the table
                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp && kvp.Key != null && kvp.Key.Span.ContainsPosition(position))
                    {
                        var keyName = GetSimpleKeyName(kvp.Key);
                        return new KeyInfo
                        {
                            KeyName = keyName,
                            TablePrefix = currentTablePath,
                            DisplayName = string.IsNullOrEmpty(currentTablePath)
                                ? keyName
                                : $"{currentTablePath}.{keyName}"
                        };
                    }
                }
            }

            return null;
        }

        private static string GetSimpleKeyName(KeySyntax key)
        {
            // Get the full key name as a string
            return key.ToString()?.Trim();
        }

        private static List<KeyReference> FindAllKeyReferences(Document document, string keyName, string tablePrefix)
        {
            var references = new List<KeyReference>();

            // If searching for a table name
            if (string.IsNullOrEmpty(keyName))
            {
                foreach (TableSyntaxBase table in document.Model.Tables)
                {
                    var tableName = table.Name?.ToString()?.Trim();
                    if (tableName == tablePrefix)
                    {
                        references.Add(new KeyReference
                        {
                            Line = table.Name.Span.Start.Line,
                            Column = table.Name.Span.Start.Column,
                            StartOffset = table.Name.Span.Start.Offset,
                            Length = table.Name.Span.Length
                        });
                    }
                }
                return references;
            }

            // Search root-level keys
            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (string.IsNullOrEmpty(tablePrefix) && kvp.Key != null)
                {
                    var currentKeyName = GetSimpleKeyName(kvp.Key);
                    if (currentKeyName == keyName)
                    {
                        references.Add(new KeyReference
                        {
                            Line = kvp.Key.Span.Start.Line,
                            Column = kvp.Key.Span.Start.Column,
                            StartOffset = kvp.Key.Span.Start.Offset,
                            Length = kvp.Key.Span.Length
                        });
                    }
                }
            }

            // Search within tables
            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;

                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp && kvp.Key != null)
                    {
                        var currentKeyName = GetSimpleKeyName(kvp.Key);

                        // Match if key name matches and table prefix matches
                        if (currentKeyName == keyName && tableName == tablePrefix)
                        {
                            references.Add(new KeyReference
                            {
                                Line = kvp.Key.Span.Start.Line,
                                Column = kvp.Key.Span.Start.Column,
                                StartOffset = kvp.Key.Span.Start.Offset,
                                Length = kvp.Key.Span.Length
                            });
                        }
                    }
                }
            }

            return references;
        }

        private static async Task ShowReferencesAsync(string fileName, string keyName, List<KeyReference> references)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Show results in a dedicated output window
            await ShowInFindResultsWindowAsync(fileName, keyName, references);
        }

        private static async Task ShowInFindResultsWindowAsync(string fileName, string keyName, List<KeyReference> references)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Show results in Output window
            var outputWindow = await VS.Windows.CreateOutputWindowPaneAsync("TOML Find All References");
            await outputWindow.ClearAsync();
            await outputWindow.WriteLineAsync($"Find All References: '{keyName}'");
            await outputWindow.WriteLineAsync($"Found {references.Count} reference(s) in '{System.IO.Path.GetFileName(fileName)}'");
            await outputWindow.WriteLineAsync("");

            foreach (var reference in references)
            {
                await outputWindow.WriteLineAsync($"  Line {reference.Line + 1}, Column {reference.Column + 1}");
            }

            await outputWindow.WriteLineAsync("");
            await outputWindow.WriteLineAsync("(Double-click a line number to navigate)");
            await outputWindow.ActivateAsync();
        }

                private class KeyInfo
                {
                    public string KeyName { get; set; }
                    public string TablePrefix { get; set; }
                    public string DisplayName { get; set; }
                    public bool IsTableName { get; set; }
                }

                private class KeyReference
                {
                    public int Line { get; set; }
                    public int Column { get; set; }
                    public int StartOffset { get; set; }
                    public int Length { get; set; }
                }
            }
        }
