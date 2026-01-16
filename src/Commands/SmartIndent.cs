using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    [Export(typeof(ICommandHandler))]
    [Name(nameof(SmartIndent))]
    [ContentType(Constants.LanguageName)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    public class SmartIndent : ICommandHandler<ReturnKeyCommandArgs>
    {
        public string DisplayName => nameof(SmartIndent);

        public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext executionContext)
        {
            ITextView textView = args.TextView;
            ITextBuffer buffer = args.SubjectBuffer;
            SnapshotPoint caretPosition = textView.Caret.Position.BufferPosition;
            ITextSnapshotLine currentLine = caretPosition.GetContainingLine();

            var lineText = currentLine.GetText();

            using (ITextEdit edit = buffer.CreateEdit())
            {
                if (string.IsNullOrWhiteSpace(lineText))
                {
                    // Remove whitespace from the current line and insert a new line with no indentation
                    edit.Delete(currentLine.Start.Position, currentLine.Length);
                    edit.Insert(currentLine.Start.Position, Environment.NewLine);
                }
                else
                {
                    // Insert a new line with the same indentation as the current line
                    var indentation = GetIndentation(lineText);
                    edit.Insert(caretPosition.Position, Environment.NewLine + indentation);
                }

                edit.Apply();
            }

            return true;
        }

        public CommandState GetCommandState(ReturnKeyCommandArgs args)
        {
            return CommandState.Available;
        }

        private static string GetIndentation(string lineText)
        {
            // If the line is empty or only whitespace, return no indentation
            if (string.IsNullOrWhiteSpace(lineText))
            {
                return string.Empty;
            }

            // Extract leading whitespace from the line
            var index = 0;
            while (index < lineText.Length && char.IsWhiteSpace(lineText[index]))
            {
                index++;
            }

            return lineText.Substring(0, index);
        }
    }
}
