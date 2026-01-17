using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor.Schema
{
    [Export(typeof(IUIElementProvider<Completion, ICompletionSession>))]
    [Name("TOML Schema Completion Tooltip")]
    [ContentType(Constants.LanguageName)]
    public class CompletionElementProvider : IUIElementProvider<Completion, ICompletionSession>
    {
        [Import]
        private IViewElementFactoryService ViewElementFactoryService { get; set; }

        public UIElement GetUIElement(Completion itemToRender, ICompletionSession context, UIElementType elementType)
        {
            if (elementType == UIElementType.Tooltip)
            {
                ContainerElement tooltip = BuildTooltip(itemToRender);
                if (tooltip != null)
                {
                    ITextView textView = context.TextView;
                    UIElement element = ViewElementFactoryService.CreateViewElement<UIElement>(textView, tooltip);

                    return new Border
                    {
                        Child = element,
                        Padding = new Thickness(6)
                    };
                }
            }

            return null;
        }

        private static ContainerElement BuildTooltip(Completion item)
        {
            if (!item.Properties.TryGetProperty("Description", out string description) || 
                string.IsNullOrEmpty(description))
            {
                return null;
            }

            var elements = new List<object>();

            // Add type info if available
            if (item.Properties.TryGetProperty("Type", out string type) && !string.IsNullOrEmpty(type))
            {
                elements.Add(new ContainerElement(
                    ContainerElementStyle.Wrapped,
                    new ClassifiedTextElement(
                        new ClassifiedTextRun(Microsoft.VisualStudio.Language.StandardClassification.PredefinedClassificationTypeNames.Keyword, type))));
            }

            // Add description
            elements.Add(new ContainerElement(
                ContainerElementStyle.Wrapped,
                new ClassifiedTextElement(
                    new ClassifiedTextRun(Microsoft.VisualStudio.Language.StandardClassification.PredefinedClassificationTypeNames.NaturalLanguage, description))));

            // Add deprecated warning if applicable
            if (item.Properties.TryGetProperty("IsDeprecated", out bool isDeprecated) && isDeprecated)
            {
                elements.Add(new ContainerElement(
                    ContainerElementStyle.Wrapped,
                    new ClassifiedTextElement(
                        new ClassifiedTextRun(Microsoft.VisualStudio.Language.StandardClassification.PredefinedClassificationTypeNames.ExcludedCode, "⚠ Deprecated"))));
            }

            return new ContainerElement(ContainerElementStyle.Stacked, elements);
        }
    }
}
