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

        /// <summary>
        /// Checks if a position falls within a given source span.
        /// </summary>
        public static bool ContainsPosition(this SourceSpan span, int position)
        {
            return position >= span.Start.Offset && position <= span.End.Offset;
        }

        /// <summary>
        /// Checks if a position falls within a given nullable source span.
        /// </summary>
        public static bool ContainsPosition(this SourceSpan? span, int position)
        {
            if (!span.HasValue)
            {
                return false;
            }

            return span.Value.ContainsPosition(position);
        }
    }
}

