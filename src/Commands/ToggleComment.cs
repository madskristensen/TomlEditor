using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    [Export(typeof(ICommandHandler))]
    [Name(nameof(ToggleComment))]
    [ContentType(Constants.LanguageName)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    public class ToggleComment : ICommandHandler<ToggleLineCommentCommandArgs>
    {
        public string DisplayName => "Toggle Line Comment";

        public bool ExecuteCommand(ToggleLineCommentCommandArgs args, CommandExecutionContext executionContext)
        {
            ITextView textView = args.TextView;
            ITextBuffer buffer = args.SubjectBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;

            // Get the lines affected by the selection or caret position
            NormalizedSnapshotSpanCollection selectedSpans = textView.Selection.SelectedSpans;
            SnapshotSpan span = selectedSpans.Count > 0
                ? new SnapshotSpan(selectedSpans.First().Start, selectedSpans.Last().End)
                : new SnapshotSpan(textView.Caret.Position.BufferPosition, 0);

            var startLineNumber = snapshot.GetLineNumberFromPosition(span.Start);
            var endLineNumber = snapshot.GetLineNumberFromPosition(span.End);

            // If selection ends at the start of a line, don't include that line
            if (span.Length > 0 && span.End == snapshot.GetLineFromLineNumber(endLineNumber).Start)
            {
                endLineNumber = Math.Max(startLineNumber, endLineNumber - 1);
            }

            // Determine if we should comment or uncomment based on the first non-empty line
            var shouldComment = ShouldComment(snapshot, startLineNumber, endLineNumber);

            using (ITextEdit edit = buffer.CreateEdit())
            {
                for (var i = startLineNumber; i <= endLineNumber; i++)
                {
                    ITextSnapshotLine line = snapshot.GetLineFromLineNumber(i);

                    if (shouldComment)
                    {
                        CommentLine(edit, line);
                    }
                    else
                    {
                        UncommentLine(edit, line);
                    }
                }

                edit.Apply();
            }

            return true;
        }

        public CommandState GetCommandState(ToggleLineCommentCommandArgs args)
        {
            return CommandState.Available;
        }

        /// <summary>
        /// Determines whether to comment or uncomment based on whether all non-empty lines are already commented.
        /// </summary>
        private static bool ShouldComment(ITextSnapshot snapshot, int startLine, int endLine)
        {
            for (var i = startLine; i <= endLine; i++)
            {
                ITextSnapshotLine line = snapshot.GetLineFromLineNumber(i);
                var lineText = line.GetText();
                var trimmed = lineText.TrimStart();

                // Skip empty lines when determining comment state
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                // If any non-empty line is not commented, we should comment all lines
                if (!trimmed.StartsWith(Constants.CommentChar.ToString()))
                {
                    return true;
                }
            }

            // All non-empty lines are commented, so we should uncomment
            return false;
        }

        private static void CommentLine(ITextEdit edit, ITextSnapshotLine line)
        {
            var lineText = line.GetText();

            // Find the indentation to preserve it
            var indentLength = lineText.Length - lineText.TrimStart().Length;

            // Insert comment character after the indentation
            edit.Insert(line.Start.Position + indentLength, Constants.CommentChar.ToString());
        }

        private static void UncommentLine(ITextEdit edit, ITextSnapshotLine line)
        {
            var lineText = line.GetText();
            var indentLength = lineText.Length - lineText.TrimStart().Length;
            var trimmed = lineText.TrimStart();

            // Only uncomment if the line starts with a comment character
            if (trimmed.StartsWith(Constants.CommentChar.ToString()))
            {
                var commentPosition = line.Start.Position + indentLength;
                edit.Delete(commentPosition, 1);
            }
        }
    }
}
