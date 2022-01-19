using Microsoft.VisualStudio.Text;
using Tomlyn.Syntax;

namespace TomlEditor
{
    public static class ExtensionMethods
    {
        public static Span ToSpan(this SourceSpan span)
        {
            return Span.FromBounds(span.Start.Offset, span.End.Offset + 1);
        }

        public static Document GetDocument(this ITextBuffer buffer)
        {
            return buffer.Properties.GetOrCreateSingletonProperty(() => new Document(buffer));
        }
    }
}
