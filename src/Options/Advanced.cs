using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TomlEditor
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class AdvancedOptionsPage : BaseOptionPage<AdvancedOptions> { }
    }

    public class AdvancedOptions : BaseOptionModel<AdvancedOptions>, IRatingConfig
    {
        [Browsable(false)]
        public int RatingRequests { get; set; }
    }
}
