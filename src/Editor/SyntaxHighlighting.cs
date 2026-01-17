using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Tomlyn.Syntax;

namespace TomlEditor
{
    /// <summary>
    /// Provides syntax highlighting for TOML files by mapping token kinds to classification types.
    /// </summary>
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IClassificationTag))]
    [ContentType(Constants.LanguageName)]
    public class SyntaxHighlighting : TokenClassificationTaggerBase
    {
        public override Dictionary<object, string> ClassificationMap { get; } = new()
        {
            { "table", PredefinedClassificationTypeNames.WhiteSpace },
            { "name", PredefinedClassificationTypeNames.SymbolReference },
            { TokenKind.Comment, PredefinedClassificationTypeNames.Comment },
            { TokenKind.BasicKey, PredefinedClassificationTypeNames.SymbolDefinition },
            { TokenKind.String, PredefinedClassificationTypeNames.String },
            { TokenKind.Equal, PredefinedClassificationTypeNames.Operator },
            { TokenKind.Float, PredefinedClassificationTypeNames.Number },
            { TokenKind.Integer, PredefinedClassificationTypeNames.Number },
            { TokenKind.IntegerBinary, PredefinedClassificationTypeNames.Number },
            { TokenKind.IntegerHexa, PredefinedClassificationTypeNames.Number },
            { TokenKind.IntegerOctal, PredefinedClassificationTypeNames.Number },
            { TokenKind.True, PredefinedClassificationTypeNames.Keyword },
            { TokenKind.False, PredefinedClassificationTypeNames.Keyword },
            { TokenKind.StringMulti, PredefinedClassificationTypeNames.String },
            { TokenKind.StringLiteralMulti, PredefinedClassificationTypeNames.String },
            { TokenKind.StringLiteral, PredefinedClassificationTypeNames.String },
        };
    }
}
