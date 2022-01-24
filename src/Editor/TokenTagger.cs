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

            List<ITagSpan<TokenTag>> list = new();

            if (_document.Model != null)
            {
                ConvertTables(list, _document.Model.Tables);
                TagDocument(list, _document.Model);

                if (list.Any())
                {
                    CreateErrorListItems(list);
                    OnTagsUpdated(list);
                }
            }

            return Task.CompletedTask;
        }

        private void TagDocument(List<ITagSpan<TokenTag>> list, SyntaxNode item)
        {
            for (var i = 0; i < item?.ChildrenCount; i++)
            {
                if (_document.IsParsing)
                {
                    // Abort and wait for the next parse event to finish
                    return;
                }

                SyntaxNode child = item.GetChildren(i);
                TagDocument(list, child);
            }

            ConvertNodeToTag(list, item);
        }

        private void TagChildren(List<ITagSpan<TokenTag>> list, SyntaxNode prop)
        {
            for (var i = 0; i < prop.ChildrenCount; i++)
            {
                SyntaxNode child = prop.GetChildren(i);

                if (child != null)
                {
                    ConvertNodeToTag(list, child, child.Kind);
                    TagChildren(list, child);
                }
            }
        }

        private void ConvertTables(List<ITagSpan<TokenTag>> list, IEnumerable<TableSyntaxBase> tables)
        {
            foreach (TableSyntaxBase table in tables)
            {
                ConvertNodeToTag(list, table.OpenBracket, "table");
                ConvertNodeToTag(list, table.Name, "table");
                ConvertNodeToTag(list, table.CloseBracket, "table");
            }
        }

        private void ConvertNodeToTag(List<ITagSpan<TokenTag>> list, SyntaxNode item, object type = null)
        {
            if (item == null || item.Span.End.Offset < 1)
            {
                return;
            }

            AddComments(list, item.LeadingTrivia);

            var supportsOutlining = item is TableSyntaxBase table && table.Items.Any();
            SnapshotSpan span = new(Buffer.CurrentSnapshot, item.Span.ToSpan());
            TokenTag tag = CreateToken(type ?? item.Kind, true, supportsOutlining, null);
            list.Add(new TagSpan<TokenTag>(span, tag));

            AddComments(list, item.TrailingTrivia);
        }

        private void AddComments(List<ITagSpan<TokenTag>> list, List<SyntaxTrivia> trivias)
        {
            if (trivias != null)
            {
                foreach (SyntaxTrivia trivia in trivias.Where(t => t.Kind == TokenKind.Comment))
                {
                    TokenTag commentTag = CreateToken(trivia.Kind, false, false, null);
                    SnapshotSpan commentSpan = new(Buffer.CurrentSnapshot, trivia.Span.ToSpan());
                    list.Add(new TagSpan<TokenTag>(commentSpan, commentTag));
                }
            }
        }

        private void CreateErrorListItems(List<ITagSpan<TokenTag>> list)
        {
            foreach (DiagnosticMessage error in _document.Model.Diagnostics)
            {
                ITagSpan<TokenTag> span = list.FirstOrDefault(s => s.Span.Start <= error.Span.Start.Offset && s.Span.End >= error.Span.End.Offset + 1);

                if (span == null)
                {
                    continue;
                }

                span.Tag.Errors = new[] {new  ErrorListItem
                {
                    ProjectName = "",
                    FileName = _document.FileName,
                    Message = error.Message.Replace("␍␤", ""),
                    ErrorCategory = error.Kind == DiagnosticMessageKind.Error ? PredefinedErrorTypeNames.SyntaxError : PredefinedErrorTypeNames.Warning,
                    Severity = error.Kind == DiagnosticMessageKind.Error ? __VSERRORCATEGORY.EC_ERROR : __VSERRORCATEGORY.EC_WARNING,
                    Line = error.Span.Start.Line,
                    Column = error.Span.Start.Column,
                    BuildTool = Vsix.Name,
                }};
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
