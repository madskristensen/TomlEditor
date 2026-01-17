using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Tomlyn;
using Tomlyn.Syntax;
using TomlEditor.Commands;

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
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.FORMATDOCUMENT, () => CommandHelper.ExecuteOnTomlDocument(FormatDocument));
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.FORMATSELECTION, () => CommandHelper.ExecuteOnTomlDocument(FormatSelection));
        }

        private static void FormatDocument(DocumentView doc)
        {
            var text = doc.TextBuffer.CurrentSnapshot.GetText();
            var formatted = FormatToml(text);

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
            var startLine = snapshot.GetLineNumberFromPosition(selection.Start);
            var endLine = snapshot.GetLineNumberFromPosition(selection.End);

            // Expand selection to full lines
            ITextSnapshotLine firstLine = snapshot.GetLineFromLineNumber(startLine);
            ITextSnapshotLine lastLine = snapshot.GetLineFromLineNumber(endLine);
            var expandedSpan = new SnapshotSpan(firstLine.Start, lastLine.End);

            var selectedText = expandedSpan.GetText();
            var formatted = FormatLines(selectedText);

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
        /// indents child tables, and ensures consistent line endings.
        /// </summary>
        private static string FormatLines(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var formatted = new StringBuilder();
            var keyValuePattern = new Regex(@"^(\s*)([^#=\[\]]+?)\s*=\s*(.*)$");
            var tablePattern = new Regex(@"^\s*\[([^\[\]]+)\]\s*$");
            var arrayTablePattern = new Regex(@"^\s*\[\[([^\[\]]+)\]\]\s*$");

            string currentTablePath = string.Empty;
            int currentIndentLevel = 0;
            string indentUnit = "  "; // 2 spaces per level

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimEnd();
                var trimmedStart = trimmed.TrimStart();

                // Check for array table header [[table]]
                Match arrayTableMatch = arrayTablePattern.Match(trimmed);
                if (arrayTableMatch.Success)
                {
                    string tablePath = arrayTableMatch.Groups[1].Value.Trim();
                    int indentLevel = GetTableIndentLevel(tablePath, ref currentTablePath);
                    currentIndentLevel = indentLevel;
                    string indent = new string(' ', indentLevel * indentUnit.Length);

                    // Add blank line before table if not at start and previous line is not blank
                    if (formatted.Length > 0 && !EndsWithBlankLine(formatted))
                    {
                        formatted.AppendLine();
                    }

                    trimmed = $"{indent}[[{tablePath}]]";
                }
                // Check for table header [table]
                else if (tablePattern.Match(trimmed) is Match tableMatch && tableMatch.Success)
                {
                    string tablePath = tableMatch.Groups[1].Value.Trim();
                    int indentLevel = GetTableIndentLevel(tablePath, ref currentTablePath);
                    currentIndentLevel = indentLevel;
                    string indent = new string(' ', indentLevel * indentUnit.Length);

                    // Add blank line before table if not at start and previous line is not blank
                    if (formatted.Length > 0 && !EndsWithBlankLine(formatted))
                    {
                        formatted.AppendLine();
                    }

                    trimmed = $"{indent}[{tablePath}]";
                }
                // Format key-value pairs
                else if (!trimmedStart.StartsWith("#") && keyValuePattern.Match(trimmed) is Match kvMatch && kvMatch.Success)
                {
                    var key = kvMatch.Groups[2].Value.Trim();
                    var value = kvMatch.Groups[3].Value;
                    string indent = new string(' ', (currentIndentLevel + 1) * indentUnit.Length);

                    trimmed = $"{indent}{key} = {value}";
                }
                // Preserve comments and blank lines with appropriate indentation
                else if (trimmedStart.StartsWith("#"))
                {
                    string indent = new string(' ', (currentIndentLevel + 1) * indentUnit.Length);
                    trimmed = $"{indent}{trimmedStart}";
                }
                // Blank lines - keep as is
                else if (string.IsNullOrWhiteSpace(trimmed))
                {
                    trimmed = string.Empty;
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

        /// <summary>
        /// Determines the indent level for a table based on its path and parent relationship.
        /// </summary>
        private static int GetTableIndentLevel(string tablePath, ref string currentTablePath)
        {
            // Check if this table is a child of the current table
            if (!string.IsNullOrEmpty(currentTablePath) && 
                tablePath.StartsWith(currentTablePath + ".", StringComparison.Ordinal))
            {
                // Count the dots to determine nesting level relative to root
                int dots = tablePath.Count(c => c == '.');
                currentTablePath = tablePath;
                return dots;
            }

            // Check if this is a root table or starting a new hierarchy
            int tableDots = tablePath.Count(c => c == '.');

            // Find the common parent level
            if (!string.IsNullOrEmpty(currentTablePath))
            {
                string[] currentParts = currentTablePath.Split('.');
                string[] newParts = tablePath.Split('.');

                int commonDepth = 0;
                for (int j = 0; j < Math.Min(currentParts.Length, newParts.Length); j++)
                {
                    if (currentParts[j] == newParts[j])
                    {
                        commonDepth = j + 1;
                    }
                    else
                    {
                        break;
                    }
                }

                currentTablePath = tablePath;

                // If there's a common parent, indent based on the new path's depth
                if (commonDepth > 0)
                {
                    return tableDots;
                }
            }

            currentTablePath = tablePath;
            return tableDots;
        }

        /// <summary>
        /// Checks if the StringBuilder ends with a blank line.
        /// </summary>
        private static bool EndsWithBlankLine(StringBuilder sb)
        {
            if (sb.Length < 2)
            {
                return false;
            }

            // Check for \r\n\r\n or \n\n at the end
            string str = sb.ToString();
            return str.EndsWith("\r\n\r\n") || str.EndsWith("\n\n") || 
                   str.EndsWith("\r\n") && str.Length >= 4 && str[str.Length - 3] == '\n';
        }

        private static void ReplaceAllText(ITextBuffer buffer, string newText)
        {
            buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), newText);
        }
    }
}
