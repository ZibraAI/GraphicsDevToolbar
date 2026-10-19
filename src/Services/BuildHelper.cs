using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace GraphicsDevToolbar.Services
{
    /// <summary>
    /// Builds single projects asynchronously and reports solution-build related events (completion, active config changes).
    /// </summary>
    internal sealed class BuildHelper : IVsUpdateSolutionEvents2, IDisposable
    {
        private readonly IVsSolutionBuildManager2 _buildManager;
        private readonly JoinableTaskFactory _jtf;
        private uint _cookie;
        private TaskCompletionSource<bool> _pendingBuild;

        public event EventHandler ActiveConfigurationChanged;

        public BuildHelper(IVsSolutionBuildManager2 buildManager, JoinableTaskFactory jtf)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _buildManager = buildManager;
            _jtf = jtf;
            _buildManager.AdviseUpdateSolutionEvents(this, out _cookie);
        }

        public bool IsBusy
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _pendingBuild != null
                    || (ErrorHandler.Succeeded(_buildManager.QueryBuildManagerBusy(out var busy)) && busy != 0);
            }
        }

        /// <summary>Builds the project (and its dependencies) in the active configuration. Returns true on success.</summary>
        public async Task<bool> BuildAsync(IVsHierarchy project)
        {
            await _jtf.SwitchToMainThreadAsync();
            if (IsBusy)
                return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingBuild = tcs;
            var hr = _buildManager.StartSimpleUpdateProjectConfiguration(
                project, null, null, (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD, 0, 0);
            if (ErrorHandler.Failed(hr))
            {
                _pendingBuild = null;
                return false;
            }
            return await tcs.Task;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0)
            {
                _buildManager.UnadviseUpdateSolutionEvents(_cookie);
                _cookie = 0;
            }
        }

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            var tcs = _pendingBuild;
            _pendingBuild = null;
            tcs?.TrySetResult(fSucceeded != 0 && fCancelCommand == 0);
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Cancel()
        {
            var tcs = _pendingBuild;
            _pendingBuild = null;
            tcs?.TrySetResult(false);
            return VSConstants.S_OK;
        }

        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy)
        {
            ActiveConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel) => VSConstants.S_OK;
        public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => VSConstants.S_OK;
    }
}
