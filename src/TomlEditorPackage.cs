global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using TomlEditor.Commands;

namespace TomlEditor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.TomlEditorString)]

    [ProvideLanguageService(typeof(TomLanguage), Constants.LanguageName, 0, ShowHotURLs = false, DefaultToNonHotURLs = true, EnableLineNumbers = true, EnableAsyncCompletion = true, ShowCompletion = true, ShowDropDownOptions = true, MatchBraces = true, MatchBracesAtCaret = true)]
    [ProvideLanguageExtension(typeof(TomLanguage), Constants.FileExtension)]

    [ProvideEditorFactory(typeof(TomLanguage), 0, false, CommonPhysicalViewAttributes = (int)__VSPHYSICALVIEWATTRIBUTES.PVA_SupportsPreview, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorLogicalView(typeof(TomLanguage), VSConstants.LOGVIEWID.TextView_string, IsTrusted = true)]
    [ProvideEditorExtension(typeof(TomLanguage), Constants.FileExtension, 1000)]

    [ProvideFileIcon(Constants.FileExtension, "d40a88fd-d311-40db-9df5-63680d49f8d3:0")]
    [ProvideBindingPath()]
    public sealed class TomlEditorPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            TomLanguage language = new(this);
            RegisterEditorFactory(language);
            ((IServiceContainer)this).AddService(typeof(TomLanguage), language, true);

            await Commenting.InitializeAsync();
            await Formatting.InitializeAsync();
            await GoToDefinitionCommand.InitializeAsync();
            await FindAllReferencesCommand.InitializeAsync();
        }
    }
}