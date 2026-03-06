using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
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
                ITextSnapshot snapshot = doc.TextBuffer.CurrentSnapshot;
                var references = FindAllKeyReferences(document, keyInfo.KeyName, keyInfo.TablePrefix, snapshot);

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

        private static List<KeyReference> FindAllKeyReferences(Document document, string keyName, string tablePrefix, ITextSnapshot snapshot)
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
                            Length = table.Name.Span.Length,
                            LineText = GetLineText(snapshot, table.Name.Span.Start.Line)
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
                            Length = kvp.Key.Span.Length,
                            LineText = GetLineText(snapshot, kvp.Key.Span.Start.Line)
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
                                Length = kvp.Key.Span.Length,
                                LineText = GetLineText(snapshot, kvp.Key.Span.Start.Line)
                            });
                        }
                    }
                }
            }

            return references
                .OrderBy(r => r.Line)
                .ThenBy(r => r.Column)
                .ToList();
        }

        private static string GetLineText(ITextSnapshot snapshot, int lineNumber)
        {
            if (snapshot == null || lineNumber < 0 || lineNumber >= snapshot.LineCount)
            {
                return string.Empty;
            }

            return snapshot.GetLineFromLineNumber(lineNumber).GetText().Trim();
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
                var message = string.IsNullOrWhiteSpace(reference.LineText) ? keyName : reference.LineText;
                await outputWindow.WriteLineAsync($"{fileName}({reference.Line + 1},{reference.Column + 1}): {message}");
            }

            await outputWindow.WriteLineAsync("");
            await outputWindow.WriteLineAsync("(Double-click a result line to navigate)");
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
            public string LineText { get; set; }
        }
    }

    /// <summary>
    /// Handles Rename command for TOML keys within the current document scope.
    /// </summary>
    internal static class RenameKeyCommand
    {
        /// <summary>
        /// Initializes the TOML rename command interceptor.
        /// </summary>
        public static async Task InitializeAsync()
        {
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.RENAME, Execute);
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

                var position = doc.TextView.Caret.Position.BufferPosition.Position;
                KeyInfo keyInfo = FindKeyAtPosition(position, document);

                if (keyInfo == null || string.IsNullOrEmpty(keyInfo.KeyName))
                {
                    await VS.StatusBar.ShowMessageAsync("No TOML key found at cursor position.");
                    return CommandProgression.Stop;
                }

                ITextSnapshot snapshot = doc.TextBuffer.CurrentSnapshot;
                List<KeyReference> references = FindAllKeyReferences(document, keyInfo.KeyName, keyInfo.TablePrefix, snapshot);

                if (references.Count == 0)
                {
                    await VS.StatusBar.ShowMessageAsync($"No references found for '{keyInfo.DisplayName}'.");
                    return CommandProgression.Stop;
                }

                var scopeDescription = string.IsNullOrEmpty(keyInfo.TablePrefix)
                    ? "root-level keys"
                    : $"table '{keyInfo.TablePrefix}'";

                var newName = PromptForNewName(keyInfo.KeyName, scopeDescription, references.Count);
                if (newName == null)
                {
                    return CommandProgression.Stop;
                }

                if (newName == keyInfo.KeyName)
                {
                    return CommandProgression.Stop;
                }

                if (!IsValidKeyName(newName))
                {
                    await VS.StatusBar.ShowMessageAsync("Invalid key name.");
                    return CommandProgression.Stop;
                }

                if (ScopeContainsKey(document, keyInfo.TablePrefix, newName))
                {
                    await VS.StatusBar.ShowMessageAsync($"Cannot rename to '{newName}' because that key already exists in the target scope.");
                    return CommandProgression.Stop;
                }

                using ITextEdit edit = doc.TextBuffer.CreateEdit();

                foreach (KeyReference reference in references.OrderByDescending(r => r.StartOffset))
                {
                    edit.Replace(reference.StartOffset, reference.Length, newName);
                }

                edit.Apply();

                await VS.StatusBar.ShowMessageAsync($"Renamed '{keyInfo.DisplayName}' to '{newName}' ({references.Count} updates).");
                return CommandProgression.Stop;
            });
        }

        private static bool IsValidKeyName(string value)
        {
            return value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');
        }

        private static KeyInfo FindKeyAtPosition(int position, Document document)
        {
            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (kvp.Key != null && kvp.Key.Span.ContainsPosition(position))
                {
                    var keyName = kvp.Key.ToString()?.Trim();
                    return new KeyInfo
                    {
                        KeyName = keyName,
                        TablePrefix = string.Empty,
                        DisplayName = keyName
                    };
                }
            }

            var currentTablePath = string.Empty;

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                if (table.Span.Start.Offset > position)
                {
                    break;
                }

                currentTablePath = table.Name?.ToString()?.Trim() ?? string.Empty;

                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp && kvp.Key != null && kvp.Key.Span.ContainsPosition(position))
                    {
                        var keyName = kvp.Key.ToString()?.Trim();
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

        private static List<KeyReference> FindAllKeyReferences(Document document, string keyName, string tablePrefix, ITextSnapshot snapshot)
        {
            var references = new List<KeyReference>();

            foreach (KeyValueSyntax kvp in document.Model.KeyValues)
            {
                if (string.IsNullOrEmpty(tablePrefix) && kvp.Key != null && kvp.Key.ToString()?.Trim() == keyName)
                {
                    references.Add(new KeyReference
                    {
                        StartOffset = kvp.Key.Span.Start.Offset,
                        Length = kvp.Key.Span.Length
                    });
                }
            }

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;
                if (tableName != tablePrefix)
                {
                    continue;
                }

                foreach (SyntaxNode item in table.Items)
                {
                    if (item is KeyValueSyntax kvp && kvp.Key != null && kvp.Key.ToString()?.Trim() == keyName)
                    {
                        references.Add(new KeyReference
                        {
                            StartOffset = kvp.Key.Span.Start.Offset,
                            Length = kvp.Key.Span.Length
                        });
                    }
                }
            }

            return references;
        }

        private static bool ScopeContainsKey(Document document, string tablePrefix, string keyName)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (keyName == null)
            {
                throw new ArgumentNullException(nameof(keyName));
            }

            if (string.IsNullOrEmpty(tablePrefix))
            {
                return document.Model.KeyValues
                    .Any(kvp => string.Equals(kvp.Key?.ToString()?.Trim(), keyName, StringComparison.Ordinal));
            }

            foreach (TableSyntaxBase table in document.Model.Tables)
            {
                var tableName = table.Name?.ToString()?.Trim() ?? string.Empty;
                if (!string.Equals(tableName, tablePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (table.Items
                    .OfType<KeyValueSyntax>()
                    .Any(kvp => string.Equals(kvp.Key?.ToString()?.Trim(), keyName, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string PromptForNewName(string currentName, string scopeDescription, int referenceCount)
        {
            using Form form = new()
            {
                Width = 520,
                Height = 200,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = "Rename TOML Key",
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
            };

            Label scopeLabel = new()
            {
                Left = 12,
                Top = 12,
                Width = 480,
                Text = $"Scope: {scopeDescription} ({referenceCount} reference(s))"
            };

            Label label = new()
            {
                Left = 12,
                Top = 36,
                Width = 390,
                Text = "New key name:"
            };

            TextBox textBox = new()
            {
                Left = 12,
                Top = 58,
                Width = 480,
                Text = currentName
            };

            Label validationLabel = new()
            {
                Left = 12,
                Top = 86,
                Width = 480,
                Height = 36,
                Text = string.Empty
            };

            Button okButton = new()
            {
                Text = "OK",
                Left = 336,
                Width = 75,
                Top = 124,
                DialogResult = DialogResult.OK
            };

            Button cancelButton = new()
            {
                Text = "Cancel",
                Left = 417,
                Width = 75,
                Top = 124,
                DialogResult = DialogResult.Cancel
            };

            void ValidateInput(object _, EventArgs __)
            {
                var candidate = textBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    validationLabel.Text = "Key name is required.";
                    okButton.Enabled = false;
                    return;
                }

                if (!IsValidKeyName(candidate))
                {
                    validationLabel.Text = "Key name can only contain letters, digits, '_', '-', or '.'.";
                    okButton.Enabled = false;
                    return;
                }

                validationLabel.Text = string.Empty;
                okButton.Enabled = true;
            }

            textBox.TextChanged += ValidateInput;

            form.Controls.Add(scopeLabel);
            form.Controls.Add(label);
            form.Controls.Add(textBox);
            form.Controls.Add(validationLabel);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            form.Shown += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
                ValidateInput(null, EventArgs.Empty);
            };

            DialogResult result = form.ShowDialog();
            if (result != DialogResult.OK)
            {
                return null;
            }

            var value = textBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private sealed class KeyInfo
        {
            public string KeyName { get; set; }
            public string TablePrefix { get; set; }
            public string DisplayName { get; set; }
        }

        private sealed class KeyReference
        {
            public int StartOffset { get; set; }
            public int Length { get; set; }
        }
    }
}
