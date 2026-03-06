using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TomlEditor.Commands;

namespace TomlEditor
{
    /// <summary>
    /// Handles Format Document and Format Selection commands for TOML files.
    /// </summary>
    public class Formatting
    {
        private static readonly Regex TablePattern = new(@"^\s*\[([^\[\]]+)\]\s*$", RegexOptions.Compiled);
        private static readonly Regex ArrayTablePattern = new(@"^\s*\[\[([^\[\]]+)\]\]\s*$", RegexOptions.Compiled);
        private const string IndentUnit = "  ";

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
            DocumentSyntax doc = SyntaxParser.Parse(text, validate: true);

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
            var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var formatted = new StringBuilder();

            var currentTablePath = string.Empty;
            var currentIndentLevel = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimEnd();
                var trimmedStart = trimmed.TrimStart();

                // Check for array table header [[table]]
                Match arrayTableMatch = ArrayTablePattern.Match(trimmed);
                if (arrayTableMatch.Success)
                {
                    var tablePath = arrayTableMatch.Groups[1].Value.Trim();
                    var indentLevel = GetTableIndentLevel(tablePath, ref currentTablePath);
                    currentIndentLevel = indentLevel;
                    var indent = new string(' ', indentLevel * IndentUnit.Length);

                    // Add blank line before table if not at start and previous line is not blank
                    if (formatted.Length > 0 && !EndsWithBlankLine(formatted))
                    {
                        formatted.AppendLine();
                    }

                    trimmed = $"{indent}[[{tablePath}]]";
                }
                // Check for table header [table]
                else if (TablePattern.Match(trimmed) is Match tableMatch && tableMatch.Success)
                {
                    var tablePath = tableMatch.Groups[1].Value.Trim();
                    var indentLevel = GetTableIndentLevel(tablePath, ref currentTablePath);
                    currentIndentLevel = indentLevel;
                    var indent = new string(' ', indentLevel * IndentUnit.Length);

                    // Add blank line before table if not at start and previous line is not blank
                    if (formatted.Length > 0 && !EndsWithBlankLine(formatted))
                    {
                        formatted.AppendLine();
                    }

                    trimmed = $"{indent}[{tablePath}]";
                }
                // Format key-value pairs
                else if (!trimmedStart.StartsWith("#") && TrySplitKeyValue(trimmed, out var key, out var value))
                {
                    var indent = new string(' ', currentIndentLevel * IndentUnit.Length);

                    trimmed = $"{indent}{key} = {value}";
                }
                // Preserve comments and blank lines with appropriate indentation
                else if (trimmedStart.StartsWith("#"))
                {
                    var indent = new string(' ', currentIndentLevel * IndentUnit.Length);
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
                var dots = tablePath.Count(c => c == '.');
                currentTablePath = tablePath;
                return dots;
            }

            // Check if this is a root table or starting a new hierarchy
            var tableDots = tablePath.Count(c => c == '.');

            // Find the common parent level
            if (!string.IsNullOrEmpty(currentTablePath))
            {
                var currentParts = currentTablePath.Split('.');
                var newParts = tablePath.Split('.');

                var commonDepth = 0;
                for (var j = 0; j < Math.Min(currentParts.Length, newParts.Length); j++)
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

            var last = sb[sb.Length - 1];
            var secondLast = sb[sb.Length - 2];

            if (last == '\n' && secondLast == '\n')
            {
                return true;
            }

            if (sb.Length >= 4 && last == '\n' && secondLast == '\r' && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r')
            {
                return true;
            }

            return false;
        }

        private static bool TrySplitKeyValue(string line, out string key, out string value)
        {
            key = null;
            value = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var quote = '\0';
            var escaped = false;
            var equalsIndex = -1;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == '#')
                {
                    break;
                }

                if (c == '=')
                {
                    equalsIndex = i;
                    break;
                }
            }

            if (equalsIndex <= 0)
            {
                return false;
            }

            key = line.Substring(0, equalsIndex).Trim();
            value = line.Substring(equalsIndex + 1).TrimStart();

            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return true;
        }

        private static void ReplaceAllText(ITextBuffer buffer, string newText)
        {
            buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), newText);
        }

        [Conditional("DEBUG")]
        internal static void RunRegressionChecks()
        {
            var quotedEquals = "title = \"a=b\"";
            Debug.Assert(FormatLines(quotedEquals) == quotedEquals, "Formatter should preserve '=' inside quoted values.");

            var trailingComment = "name=\"value\" # keep";
            Debug.Assert(FormatLines(trailingComment) == "name = \"value\" # keep", "Formatter should preserve trailing comments while normalizing spacing.");

            var simple = "a=1";
            Debug.Assert(FormatLines(simple) == "a = 1", "Formatter should normalize basic key-value spacing.");
        }
    }
}
