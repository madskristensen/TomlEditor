using System.Collections;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Package;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Tomlyn.Syntax;

namespace TomlEditor
{
    internal partial class DropdownBars : TypeAndMemberDropdownBars, IDisposable
    {
        private readonly LanguageService _languageService;
        private readonly IWpfTextView _textView;
        private readonly Document _document;
        private bool _disposed;
        private bool _hasBufferChanged;

        public DropdownBars(IVsTextView textView, LanguageService languageService) : base(languageService)
        {
            _languageService = languageService;
            _textView = textView.ToIWpfTextView();
            _document = _textView.TextBuffer.GetDocument();
            _document.Parsed += OnDocumentParsed;

            InitializeAsync(textView).FireAndForget();
        }

        // This moves the caret to trigger initial drop down load
        private Task InitializeAsync(IVsTextView textView)
        {
            return ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
            {
                textView.SendExplicitFocus();
                _textView.Caret.MoveToNextCaretPosition();
                _textView.Caret.PositionChanged += CaretPositionChanged;
                _textView.Caret.MoveToPreviousCaretPosition();
            }).Task;
        }

        private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e) => SynchronizeDropdowns();
        private void OnDocumentParsed(Document document)
        {
            _hasBufferChanged = true;
            SynchronizeDropdowns();
        }

        private void SynchronizeDropdowns()
        {
            if (_document.IsParsing)
            {
                return;
            }

            _ = ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
            {
                _languageService.SynchronizeDropdowns();
            }, VsTaskRunContext.UIThreadIdlePriority);
        }

        public override bool OnSynchronizeDropdowns(LanguageService languageService, IVsTextView textView, int line, int col, ArrayList dropDownTypes, ArrayList dropDownMembers, ref int selectedType, ref int selectedMember)
        {
            if (_hasBufferChanged || dropDownMembers.Count == 0)
            {
                dropDownMembers.Clear();

                var tables = _document.Model.Tables.ToList();
                tables.Insert(0, new TableSyntax("Document"));

                tables
                    .Select(headingBlock => CreateDropDownMember(headingBlock, textView))
                    .ToList()
                    .ForEach(ddm => dropDownMembers.Add(ddm));
            }

            if (dropDownTypes.Count == 0)
            {
                var thisExt = $"{Vsix.Name} ({Vsix.Version})";
                var markdig = Path.GetFileName($"   Powered by Tomlyn");
                dropDownTypes.Add(new DropDownMember(thisExt, new TextSpan(), 126, DROPDOWNFONTATTR.FONTATTR_GRAY));
                dropDownTypes.Add(new DropDownMember(markdig, new TextSpan(), 126, DROPDOWNFONTATTR.FONTATTR_GRAY));
            }

            DropDownMember currentDropDown = dropDownMembers
                .OfType<DropDownMember>()
                .Where(d => d.Span.iStartLine <= line)
                .LastOrDefault();

            selectedMember = dropDownMembers.IndexOf(currentDropDown);
            selectedType = 0;
            _hasBufferChanged = false;

            return true;
        }

        //private IEnumerable<TableSyntaxBase> GetHeadings()
        //{
        //    var visitor = new TableVisitor();
        //    _document.Model.Accept(visitor);

        //    return visitor.List;
        //}

        private static DropDownMember CreateDropDownMember(TableSyntaxBase headingBlock, IVsTextView textView)
        {
            TextSpan textSpan = GetTextSpan(headingBlock, textView);
            var headingText = headingBlock.Name.ToString();

            return new DropDownMember(headingText, textSpan, 126, DROPDOWNFONTATTR.FONTATTR_PLAIN);
        }

        private static TextSpan GetTextSpan(TableSyntaxBase headingBlock, IVsTextView textView)
        {
            TextSpan textSpan = new();

            textView.GetLineAndColumn(headingBlock.Span.Start.Offset, out textSpan.iStartLine, out textSpan.iStartIndex);
            textView.GetLineAndColumn(headingBlock.Span.End.Offset + 1, out textSpan.iEndLine, out textSpan.iEndIndex);

            return textSpan;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _textView.Caret.PositionChanged -= CaretPositionChanged;
            _document.Parsed -= OnDocumentParsed;
        }
    }
}
