using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Tomlyn.Syntax;

namespace TomlEditor
{
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IClassificationTag))]
    [ContentType(Constants.LanguageName)]
    public class SyntaxHighligting : TokenClassificationTaggerBase
    {
        public override Dictionary<object, string> ClassificationMap { get; } = new()
        {
            { "table", PredefinedClassificationTypeNames.SymbolReference },
            { "name", PredefinedClassificationTypeNames.SymbolDefinition },
            { "operator", PredefinedClassificationTypeNames.Operator },
            { SyntaxKind.Key, PredefinedClassificationTypeNames.SymbolDefinition },
            { SyntaxKind.String, PredefinedClassificationTypeNames.String },
            { SyntaxKind.Token, PredefinedClassificationTypeNames.Operator },
            { SyntaxKind.Float, PredefinedClassificationTypeNames.Number },
            { SyntaxKind.Integer, PredefinedClassificationTypeNames.Number },
            { SyntaxKind.Boolean, PredefinedClassificationTypeNames.Keyword },
        };
    }

    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IStructureTag))]
    [ContentType(Constants.LanguageName)]
    public class Outlining : TokenOutliningTaggerBase
    { }

    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IErrorTag))]
    [ContentType(Constants.LanguageName)]
    public class ErrorSquigglies : TokenErrorTaggerBase
    { }

    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [ContentType(Constants.LanguageName)]
    internal sealed class Tooltips : TokenQuickInfoBase
    { }

    [Export(typeof(IBraceCompletionContextProvider))]
    [BracePair('(', ')')]
    [BracePair('[', ']')]
    [BracePair('{', '}')]
    [BracePair('"', '"')]
    [BracePair('*', '*')]
    [ContentType(Constants.LanguageName)]
    [ProvideBraceCompletion(Constants.LanguageName)]
    internal sealed class BraceCompletion : BraceCompletionBase
    { }

    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [ContentType(Constants.LanguageName)]
    internal sealed class CompletionCommitManager : CompletionCommitManagerBase
    {
        public override IEnumerable<char> CommitChars => new char[] { ' ', '\'', '"', ',', '.', ';', ':', '\\', '$' };
    }

    [Export(typeof(IViewTaggerProvider))]
    [TagType(typeof(TextMarkerTag))]
    [ContentType(Constants.LanguageName)]
    internal sealed class BraceMatchingTaggerProvider : BraceMatchingBase
    {
        // This will match parenthesis, curly brackets, and square brackets by default.
        // Override the BraceList property to modify the list of braces to match.
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType(Constants.LanguageName)]
    [TagType(typeof(TextMarkerTag))]
    public class SameWordHighlighter : SameWordHighlighterBase
    { }

    //[Export(typeof(IWpfTextViewCreationListener))]
    //[ContentType(Constants.LanguageName)]
    //[TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    //public class HideMargings : WpfTextViewCreationListener
    //{
    //    private readonly Regex _taskRegex = new(@"(?<keyword>TODO|HACK|UNDONE):(?<phrase>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    //    private TableDataSource _dataSource;
    //    private DocumentView _docView;

    //    [Import] internal IBufferTagAggregatorFactoryService _bufferTagAggregator = null;
    //    private Document _document;

    //    protected override void Created(DocumentView docView)
    //    {
    //        _document = docView.TextBuffer.GetDocument();
    //        _docView ??= docView;
    //        _dataSource ??= new TableDataSource(docView.TextBuffer.ContentType.DisplayName);

    //        _docView.TextView.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginName, false);
    //        _docView.TextView.Options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginName, true);
    //        _docView.TextView.Options.SetOptionValue(DefaultTextViewHostOptions.ShowEnhancedScrollBarOptionName, false);

    //        _document.Parsed += OnParsed;
    //    }

    //    private void OnParsed(Document document)
    //    {
    //        ParseCommentsAsync().FireAndForget();
    //    }

    //    private async Task ParseCommentsAsync()
    //    {
    //        await TaskScheduler.Default;

    //        IEnumerable<HtmlBlock> comments = _document.Markdown.Descendants<HtmlBlock>().Where(html => html.Type == HtmlBlockType.Comment);

    //        if (!comments.Any())
    //        {
    //            _dataSource.CleanAllErrors();
    //            return;
    //        }

    //        List<ErrorListItem> list = new();

    //        foreach (HtmlBlock comment in comments)
    //        {
    //            SnapshotSpan span = new(_docView.TextBuffer.CurrentSnapshot, comment.ToSpan());
    //            var text = span.GetText();

    //            foreach (Match match in _taskRegex.Matches(text))
    //            {
    //                ErrorListItem error = new()
    //                {
    //                    FileName = _docView.FilePath,
    //                    ErrorCategory = "suggestion",
    //                    Severity = Microsoft.VisualStudio.Shell.Interop.__VSERRORCATEGORY.EC_MESSAGE,
    //                    Message = match.Groups["phrase"].Value.Replace("-->", "").Replace("*/", "").Trim(),
    //                    Line = comment.Line,
    //                    Column = comment.Column,
    //                    ErrorCode = match.Groups["keyword"].Value.ToUpperInvariant(),
    //                    Icon = KnownMonikers.StatusInformationOutline,
    //                };

    //                list.Add(error);
    //            }
    //        }

    //        _dataSource.AddErrors(list);
    //    }

    //    protected override void Closed(IWpfTextView textView)
    //    {
    //        _dataSource.CleanAllErrors();
    //        _document.Parsed -= OnParsed;
    //    }
    //}
}

