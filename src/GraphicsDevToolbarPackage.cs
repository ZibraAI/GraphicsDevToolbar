using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using GraphicsDevToolbar.Options;
using GraphicsDevToolbar.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace GraphicsDevToolbar
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(GeneralOptions), "Graphics Dev Toolbar", "General", 0, 0, true)]
    // Adds the extension folder to VS's assembly probing paths, so the image service can resolve the
    // "/GraphicsDevToolbar;Component/..." pack URIs in GraphicsDevToolbar.imagemanifest (otherwise icons are blank).
    [ProvideBindingPath]
    [Guid(PackageGuids.PackageString)]
    public sealed class GraphicsDevToolbarPackage : AsyncPackage
    {
        private ToolbarController _controller;

        public GeneralOptions Options => (GeneralOptions)GetDialogPage(typeof(GeneralOptions));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Background loading starts on a thread pool thread: probe for tools before switching to the UI thread.
            var tools = ToolLocator.FindAll();

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            var dte = (DTE2)await GetServiceAsync(typeof(DTE));
            var solution = (IVsSolution)await GetServiceAsync(typeof(SVsSolution));
            var monitorSelection = (IVsMonitorSelection)await GetServiceAsync(typeof(SVsShellMonitorSelection));
            var buildManager = (IVsSolutionBuildManager2)await GetServiceAsync(typeof(SVsSolutionBuildManager));
            var uiShell = (IVsUIShell)await GetServiceAsync(typeof(SVsUIShell));
            var outputWindow = (IVsOutputWindow)await GetServiceAsync(typeof(SVsOutputWindow));

            _controller = new ToolbarController(this, tools, dte, solution, monitorSelection, buildManager, uiShell, outputWindow);
            _controller.RegisterCommands(commandService);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _controller != null)
            {
#pragma warning disable VSTHRD108 // Only the disposing path touches UI-thread-bound services.
                ThreadHelper.ThrowIfNotOnUIThread();
#pragma warning restore VSTHRD108
                _controller.Dispose();
                _controller = null;
            }
            base.Dispose(disposing);
        }
    }
}
