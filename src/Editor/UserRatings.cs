using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace TomlEditor
{
    /// <summary>
    /// Tracks extension usage to prompt users for ratings after sufficient use.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(Constants.LanguageName)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    public class UserRatings : WpfTextViewCreationListener
    {
        private DateTime _openedDate;
        private RatingPrompt _rating;

        protected override void Created(DocumentView docView)
        {
            _openedDate = DateTime.Now;
            _rating = new RatingPrompt(Constants.MarketplaceId, Vsix.Name, AdvancedOptions.Instance, 5);
        }

        protected override void Closed(IWpfTextView textView)
        {
            if (_openedDate.AddMinutes(2) < DateTime.Now)
            {
                _rating.RegisterSuccessfulUsage();
            }
        }
    }
}
