using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Tomlyn;
using Tomlyn.Syntax;

namespace TomlEditor
{
    /// <summary>
    /// Handles Format Document and Format Selection commands for TOML files.
    /// </summary>
    public class Formatting
    {
        public static async Task InitializeAsync()
        {
            // Intercept the formatting commands for TOML files
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.FORMATDOCUMENT, () => Execute(FormatDocument));
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.FORMATSELECTION, () => Execute(FormatSelection));
        }

        private static CommandProgression Execute(Action<DocumentView> action)
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

        private static void FormatDocument(DocumentView doc)
        {
            string text = doc.TextBuffer.CurrentSnapshot.GetText();
            string formatted = FormatToml(text);

            if (formatted != null && formatted != text)
            {
                ReplaceAllText(doc.TextBuffer, formatted);
            }
        }

        private static void FormatSelection(DocumentView doc)
        {
            SnapshotSpan selection = doc.TextView.Selection.SelectedSpans.FirstOrDefault();

            if (selection.IsEmpty)
            {
                // If no selection, format the entire document
                FormatDocument(doc);
                return;
            }

            // Get lines that intersect with the selection to format complete lines
            ITextSnapshot snapshot = doc.TextBuffer.CurrentSnapshot;
            int startLine = snapshot.GetLineNumberFromPosition(selection.Start);
            int endLine = snapshot.GetLineNumberFromPosition(selection.End);

            // Expand selection to full lines
            ITextSnapshotLine firstLine = snapshot.GetLineFromLineNumber(startLine);
            ITextSnapshotLine lastLine = snapshot.GetLineFromLineNumber(endLine);
            var expandedSpan = new SnapshotSpan(firstLine.Start, lastLine.End);

            string selectedText = expandedSpan.GetText();
            string formatted = FormatLines(selectedText);

            if (formatted != null && formatted != selectedText)
            {
                doc.TextBuffer.Replace(expandedSpan.Span, formatted);
            }
        }

        /// <summary>
        /// Formats TOML text while preserving comments.
        /// Uses Tomlyn's syntax tree to validate, then applies line-based formatting.
        /// </summary>
        private static string FormatToml(string text)
        {
            // First validate the TOML is parseable
            DocumentSyntax doc = Toml.Parse(text, options: TomlParserOptions.ParseAndValidate);

            if (doc.HasErrors)
            {
                // Invalid TOML, cannot format
                return null;
            }

            return FormatLines(text);
        }

        /// <summary>
        /// Applies line-based formatting: trims lines, normalizes spacing around '=' signs,
        /// and ensures consistent line endings.
        /// </summary>
        private static string FormatLines(string text)
        {
            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var formatted = new StringBuilder();
            var keyValuePattern = new Regex(@"^(\s*)([^#=\[\]]+?)\s*=\s*(.*)$");

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimEnd();

                // Format key-value pairs: ensure single space around '='
                Match match = keyValuePattern.Match(trimmed);

                if (match.Success && !trimmed.TrimStart().StartsWith("#"))
                {
                    string indent = match.Groups[1].Value;
                    string key = match.Groups[2].Value.Trim();
                    string value = match.Groups[3].Value;

                    trimmed = $"{indent}{key} = {value}";
                }

                formatted.Append(trimmed);

                // Add newline for all but the last line
                if (i < lines.Length - 1)
                {
                    formatted.AppendLine();
                }
            }

            return formatted.ToString();
        }

        private static void ReplaceAllText(ITextBuffer buffer, string newText)
        {
            buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), newText);
        }
    }
}
