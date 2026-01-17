using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor.Schema
{
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [ContentType(Constants.LanguageName)]
    [Name("TOML Schema Completion Commit Manager")]
    internal sealed class TomlCompletionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView)
        {
            return textView.Properties.GetOrCreateSingletonProperty(() => new TomlCompletionCommitManager());
        }
    }

    internal sealed class TomlCompletionCommitManager : IAsyncCompletionCommitManager
    {
        private static readonly ImmutableArray<char> CommitChars = ImmutableArray.Create(' ', '\t', '=', '\'', '"', ',', '.', ';', ':', '\\', '\n');

        public IEnumerable<char> PotentialCommitCharacters => CommitChars;

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token)
        {
            return CommitChars.Contains(typedChar);
        }

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
        {
            // Let the default handler commit the completion
            return CommitResult.Unhandled;
        }
    }
}
