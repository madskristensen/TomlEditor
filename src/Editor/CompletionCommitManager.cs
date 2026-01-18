using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    /// <summary>
    /// Manages which characters trigger completion commits for TOML files.
    /// Note: '=' is intentionally excluded so it types naturally without committing key completion.
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [ContentType(Constants.LanguageName)]
    [Name(nameof(TomlCompletionCommitManager))]
    internal sealed class TomlCompletionCommitManager : CompletionCommitManagerBase
    {
        // '=' is NOT included - let it type naturally, then user presses Ctrl+Space for value completion
        public override IEnumerable<char> CommitChars => ['\t', ' ', '\'', '"', ',', '.', ';', ':', '\\', '$', '\n', '['];
    }
}
