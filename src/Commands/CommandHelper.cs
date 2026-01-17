namespace TomlEditor.Commands
{
    /// <summary>
    /// Provides helper methods for executing commands that operate on TOML documents.
    /// </summary>
    internal static class CommandHelper
    {
        /// <summary>
        /// Executes an action on the active TOML document.
        /// Returns Stop if the action was executed, Continue otherwise.
        /// </summary>
        public static CommandProgression ExecuteOnTomlDocument(Action<DocumentView> action)
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                DocumentView doc = await VS.Documents.GetActiveDocumentViewAsync();

                if (doc?.TextBuffer != null && doc.TextBuffer.ContentType.IsOfType(Constants.LanguageName))
                {
                    action(doc);
                    return CommandProgression.Stop;
                }

                return CommandProgression.Continue;
            });
        }
    }
}
