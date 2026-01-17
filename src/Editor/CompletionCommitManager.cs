using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    /// <summary>
    /// Manages which characters trigger completion commits for TOML files.
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [ContentType(Constants.LanguageName)]
    internal sealed class CompletionCommitManager : CompletionCommitManagerBase
    {
        public override IEnumerable<char> CommitChars => new char[] { '\t', ' ', '\'', '"', ',', '.', ';', ':', '\\', '$', '=', '\n', '[' };
    }
}
