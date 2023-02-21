global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace TomlEditor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.TomlEditorString)]
    [ProvideOptionPage(typeof(OptionsProvider.AdvancedOptionsPage), "TomlEditor", "Advanced", 0, 0, true, SupportsProfiles = true, NoShowAllView = true)]

    [ProvideLanguageService(typeof(LanguageFactory), Constants.LanguageName, 0, ShowHotURLs = false, DefaultToNonHotURLs = true, EnableLineNumbers = true, EnableAsyncCompletion = true, ShowCompletion = true, ShowDropDownOptions = true)]
    [ProvideLanguageExtension(typeof(LanguageFactory), Constants.FileExtension)]

    [ProvideEditorFactory(typeof(LanguageFactory), 214, false, CommonPhysicalViewAttributes = (int)__VSPHYSICALVIEWATTRIBUTES.PVA_SupportsPreview, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorLogicalView(typeof(LanguageFactory), VSConstants.LOGVIEWID.TextView_string, IsTrusted = true)]
    [ProvideEditorExtension(typeof(LanguageFactory), Constants.FileExtension, 65536, NameResourceID = 214)]

    [ProvideFileIcon(Constants.FileExtension, "d40a88fd-d311-40db-9df5-63680d49f8d3:0")]
    [ProvideBindingPath()]
    public sealed class TomlEditorPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            LanguageFactory language = new(this);
            RegisterEditorFactory(language);
            ((IServiceContainer)this).AddService(typeof(LanguageFactory), language, true);

            await Commenting.InitializeAsync();
        }
    }
}