using System.ComponentModel;
using System.Runtime.InteropServices;
using BaseClasses;

namespace TomlEditor
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class AdvancedOptions : BaseOptionPage<Advanced> { }
    }

    public class Advanced : BaseOptionModel<Advanced>, IRatingConfig
    {
        [Browsable(false)]
        public int RatingRequests { get; set; }
    }
}
