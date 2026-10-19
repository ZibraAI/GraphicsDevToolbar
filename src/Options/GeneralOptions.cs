using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace GraphicsDevToolbar.Options
{
    [DesignerCategory("Code")]
    public class GeneralOptions : DialogPage
    {
        [Category("Command line arguments")]
        [DisplayName("Auto re-apply saved arguments")]
        [Description("Automatically re-apply arguments that were last entered with this plugin to a given project. " +
            "These arguments are saved outside of the project files, in the LocalAppData folder, so when this option is enabled, " +
            "command line arguments can be preserved even when the project is completely deleted and re-created, " +
            "e.g. after deleting the project and cleanly regenerating it with CMake. " +
            "Arguments are re-applied when a project loads, and the previous ones are logged to the Graphics Dev Toolbar output pane. " +
            "Arguments changed while the project stays loaded (e.g. in the Property Pages) are kept, and the saved ones can be restored from the arguments window.")]
        [DefaultValue(false)]
        public bool AutoReapplyArguments { get; set; }
    }
}
