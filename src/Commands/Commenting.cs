using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Formatting;
using TomlEditor.Commands;

namespace TomlEditor
{
    /// <summary>
    /// Handles Comment/Uncomment block commands for TOML files.
    /// </summary>
    public class Commenting
    {
        public static async Task InitializeAsync()
        {
            // We need to manually intercept the commenting command, because language services swallow these commands.
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.COMMENT_BLOCK, () => CommandHelper.ExecuteOnTomlDocument(Comment));
            await VS.Commands.InterceptAsync(VSConstants.VSStd2KCmdID.UNCOMMENT_BLOCK, () => CommandHelper.ExecuteOnTomlDocument(Uncomment));
        }

        private static void Comment(DocumentView doc)
        {
            SnapshotSpan spans = doc.TextView.Selection.SelectedSpans.First();
            Collection<ITextViewLine> lines = doc.TextView.TextViewLines.GetTextViewLinesIntersectingSpan(spans);

            foreach (ITextViewLine line in lines.Reverse())
            {
                doc.TextBuffer.Insert(line.Start.Position, Constants.CommentChar.ToString());
            }
        }

        private static void Uncomment(DocumentView doc)
        {
            SnapshotSpan spans = doc.TextView.Selection.SelectedSpans.First();
            Collection<ITextViewLine> lines = doc.TextView.TextViewLines.GetTextViewLinesIntersectingSpan(spans);

            foreach (ITextViewLine line in lines.Reverse())
            {
                var span = Span.FromBounds(line.Start, line.End);
                var originalText = doc.TextBuffer.CurrentSnapshot.GetText(span).TrimStart(Constants.CommentChar);
                Span commentCharSpan = new(span.Start, span.Length - originalText.Length);

                doc.TextBuffer.Delete(commentCharSpan);
            }
        }
    }
}