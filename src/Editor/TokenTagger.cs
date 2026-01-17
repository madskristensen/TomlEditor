using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Tomlyn.Syntax;

namespace TomlEditor
{
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(TokenTag))]
    [ContentType(Constants.LanguageName)]
    [Name(Constants.LanguageName)]
    internal sealed class TokenTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new TokenTagger(buffer)) as ITagger<T>;
    }

    internal class TokenTagger : TokenTaggerBase, IDisposable
    {
        private readonly Document _document;
        private static readonly ImageId _errorIcon = KnownMonikers.StatusWarning.ToImageId();
        private bool _isDisposed;

        internal TokenTagger(ITextBuffer buffer) : base(buffer)
        {
            _document = buffer.GetDocument();
            _document.Parsed += ReParse;
        }

        private void ReParse(Document document)
        {
            _ = TokenizeAsync();
        }

        public override Task TokenizeAsync()
        {
            // Make sure this is running on a background thread.
            ThreadHelper.ThrowIfOnUIThread();

            List<ITagSpan<TokenTag>> list = [];

            if (_document.Model != null)
            {
                ConvertTables(list, _document.Model.Tables);
                TagDocument(list);

                if (list.Any())
                {
                    CreateErrorListItems(list);
                    OnTagsUpdated(list);
                }
            }

            return Task.CompletedTask;
        }

        private void TagDocument(List<ITagSpan<TokenTag>> list)
        {
            SyntaxToken prevToken = null;
            bool inTableName = false;

            foreach (SyntaxNodeBase node in _document.Model.Tokens(true))
            {
                if (node is SyntaxTrivia trivia && trivia.Kind == TokenKind.Comment)
                {
                    TokenTag commentTag = CreateToken(trivia.Kind, false, false, null);
                    SnapshotSpan commentSpan = new(Buffer.CurrentSnapshot, trivia.Span.ToSpan());
                    list.Add(new TagSpan<TokenTag>(commentSpan, commentTag));
                    inTableName = false;
                }
                else if (node is SyntaxToken token && !string.IsNullOrWhiteSpace(token.Text))
                {
                    // Check if we're starting a table name (after [ or [[)
                    if (prevToken?.TokenKind == TokenKind.OpenBracket || prevToken?.TokenKind == TokenKind.OpenBracketDouble)
                    {
                        inTableName = true;
                    }
                    // Check if we're ending a table name (at ] or ]])
                    else if (token.TokenKind == TokenKind.CloseBracket || token.TokenKind == TokenKind.CloseBracketDouble)
                    {
                        inTableName = false;
                    }

                    // Tag table name tokens with "name", everything else with their token kind
                    if (inTableName && token.TokenKind != TokenKind.Dot)
                    {
                        ConvertNodeToTag(list, token, "name");
                    }
                    else
                    {
                        ConvertNodeToTag(list, token, token.TokenKind);
                    }

                    prevToken = token;
                }
            }
        }

        private void ConvertTables(List<ITagSpan<TokenTag>> list, IEnumerable<TableSyntaxBase> tables)
        {
            foreach (TableSyntaxBase table in tables)
            {
                // Tag the entire table for outlining support
                ConvertNodeToTag(list, table, "table");
            }
        }

        private void ConvertNodeToTag(List<ITagSpan<TokenTag>> list, SyntaxNode item, object type)
        {
            if (item == null || item.Span.End.Offset < 1)
            {
                return;
            }

            var supportsOutlining = item is TableSyntaxBase table && table.Items.Any();
            SnapshotSpan span = new(Buffer.CurrentSnapshot, item.Span.ToSpan());
            TokenTag tag = CreateToken(type, true, supportsOutlining, null);
            list.Add(new TagSpan<TokenTag>(span, tag));
        }

        private void CreateErrorListItems(List<ITagSpan<TokenTag>> list)
        {
            foreach (DiagnosticMessage error in _document.Model.Diagnostics)
            {
                // Create a span for the exact error location
                var errorSpan = error.Span.ToSpan();

                // If the error span is empty or invalid, try to highlight at least one character
                if (errorSpan.Length == 0 && errorSpan.Start < Buffer.CurrentSnapshot.Length)
                {
                    // Extend to end of line or at least one character
                    ITextSnapshotLine line = Buffer.CurrentSnapshot.GetLineFromPosition(errorSpan.Start);
                    var endPos = Math.Min(line.End.Position, errorSpan.Start + 1);
                    errorSpan = new Span(errorSpan.Start, Math.Max(1, endPos - errorSpan.Start));
                }

                // Ensure span is within buffer bounds
                if (errorSpan.Start >= Buffer.CurrentSnapshot.Length)
                {
                    continue;
                }

                if (errorSpan.End > Buffer.CurrentSnapshot.Length)
                {
                    errorSpan = new Span(errorSpan.Start, Buffer.CurrentSnapshot.Length - errorSpan.Start);
                }

                SnapshotSpan snapshotSpan = new(Buffer.CurrentSnapshot, errorSpan);
                TokenTag errorTag = CreateToken(TokenKind.Invalid, false, false, null);
                errorTag.Errors =
                [
                    new ErrorListItem
                    {
                        ProjectName = "",
                        FileName = _document.FileName,
                        Message = error.Message.Replace("␍␤", ""),
                        ErrorCategory = error.Kind == DiagnosticMessageKind.Error ? PredefinedErrorTypeNames.SyntaxError : PredefinedErrorTypeNames.Warning,
                        Severity = error.Kind == DiagnosticMessageKind.Error ? __VSERRORCATEGORY.EC_ERROR : __VSERRORCATEGORY.EC_WARNING,
                        Line = error.Span.Start.Line,
                        Column = error.Span.Start.Column,
                        BuildTool = Vsix.Name,
                    }
                ];

                list.Add(new TagSpan<TokenTag>(snapshotSpan, errorTag));
            }
        }

        public override Task<object> GetTooltipAsync(SnapshotPoint triggerPoint)
        {
            ITagSpan<TokenTag> item = TagsCache.FirstOrDefault(s => s.Tag.Errors.Any() && s.Span.Contains(triggerPoint.Position));

            // Error messages
            if (item != null)
            {
                ContainerElement elm = new(
                    ContainerElementStyle.Wrapped,
                    new ImageElement(_errorIcon),
                    string.Join(Environment.NewLine, item.Tag.Errors.Select(e => e.Message)));

                return Task.FromResult<object>(elm);
            }

            return Task.FromResult<object>(null);
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _document.Parsed -= ReParse;
                _document.Dispose();
            }

            _isDisposed = true;
        }
    }
}
