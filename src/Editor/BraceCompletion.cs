using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    /// <summary>
    /// Provides automatic brace completion for TOML files.
    /// </summary>
    [Export(typeof(IBraceCompletionContextProvider))]
    [BracePair('(', ')')]
    [BracePair('[', ']')]
    [BracePair('{', '}')]
    [BracePair('"', '"')]
    [BracePair('*', '*')]
    [ContentType(Constants.LanguageName)]
    [ProvideBraceCompletion(Constants.LanguageName)]
    [Name(nameof(TomlBraceCompletion))]
    internal sealed class TomlBraceCompletion : BraceCompletionBase
    {
    }
}
