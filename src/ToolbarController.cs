using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using GraphicsDevToolbar.Services;
using GraphicsDevToolbar.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using Task = System.Threading.Tasks.Task;

namespace GraphicsDevToolbar
{
    /// <summary>
    /// Owns the toolbar commands and the cached state they display. State is refreshed when the solution, the startup
    /// project or the active configuration changes; d3dconfig state is not tracked at runtime beyond that.
    /// </summary>
    internal sealed class ToolbarController : IVsSolutionEvents, IVsSelectionEvents, IDisposable
    {
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        private const string NoStartupProject = "No C++ startup project";
        private const string RestartHint = "(Restart Visual Studio to retry search)";
        private static readonly Guid OutputPaneGuid = new Guid("6f1d7c2e-93a4-4b8e-b5f1-2e7d0a9c4b31");

        private readonly GraphicsDevToolbarPackage _package;
        private readonly DTE2 _dte;
        private readonly IVsSolution _solution;
        private readonly IVsMonitorSelection _monitorSelection;
        private readonly IVsUIShell _uiShell;
        private readonly IVsOutputWindow _outputWindow;
        private readonly BuildHelper _buildHelper;
        private readonly ArgumentStore _argumentStore = new ArgumentStore();
        private readonly D3DConfigService _d3dConfig;
        private readonly string _renderDocPath;
        private readonly string _pixPath;
        private readonly string _vkConfigPath;

        private uint _solutionEventsCookie;
        private uint _selectionEventsCookie;
        private IVsOutputWindowPane _outputPane;

        // Cached state.
        private Project _startupProject;
        private VCProject _startupVcProject;
        private string _startupExePath;
        private Project _zeroCheckProject;
        private D3DConfigState _d3dState = D3DConfigState.Empty;
        private bool _d3dReading;
        private bool _operationRunning;
        private int _refreshVersion;

        // Projects (by unique name) whose saved arguments are re-applied on the next refresh. Everything is re-applied
        // after package load, because the package may load after the solution was opened. Only (re)loaded projects are
        // re-applied, e.g. after CMake regenerated them: arguments changed while a project stays loaded, e.g. in the
        // Property Pages, are kept, and the arguments window offers to restore the saved ones.
        private readonly HashSet<string> _projectsToReapply = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _reapplyAll = true;

        // Configuration the arguments window opens with: the one selected when it was last closed, for this VS session.
        private ConfigSelection _argumentsWindowSelection = ConfigSelection.Active;

        public ToolbarController(GraphicsDevToolbarPackage package, InstalledTools tools, DTE2 dte, IVsSolution solution,
            IVsMonitorSelection monitorSelection, IVsSolutionBuildManager2 buildManager, IVsUIShell uiShell, IVsOutputWindow outputWindow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            _d3dConfig = new D3DConfigService(tools.D3DConfig);
            _renderDocPath = tools.RenderDoc;
            _pixPath = tools.Pix;
            _vkConfigPath = tools.VulkanConfigurator;
            _dte = dte;
            _solution = solution;
            _monitorSelection = monitorSelection;
            _uiShell = uiShell;
            _outputWindow = outputWindow;
            _buildHelper = new BuildHelper(buildManager, package.JoinableTaskFactory);
            _buildHelper.ActiveConfigurationChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ScheduleRefresh();
            };

            _solution.AdviseSolutionEvents(this, out _solutionEventsCookie);
            _monitorSelection.AdviseSelectionEvents(this, out _selectionEventsCookie);
            Log($"d3dconfig: {_d3dConfig.ExePath ?? "not found"}");
            Log($"RenderDoc: {_renderDocPath ?? "not found"}");
            Log($"PIX: {_pixPath ?? "not found"}");
            Log($"Vulkan Configurator: {_vkConfigPath ?? "not found"}");

            // In case a solution is already open.
            ScheduleRefresh();
        }

        public void RegisterCommands(OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Add(commandService, PackageIds.Arguments, OnEditArguments,
                "Edit command line arguments of the startup project",
                () => _startupVcProject == null ? NoStartupProject : null);
            Add(commandService, PackageIds.CMake, (s, e) => RunInBackground(RerunCMakeAsync),
                "Re-run CMake configuration (builds ZERO_CHECK)",
                () => _zeroCheckProject == null ? "No ZERO_CHECK project in the solution (not a CMake-generated solution?)" : BuildProblem);
            Add(commandService, PackageIds.D3DOverride, (s, e) => ToggleOverride(),
                "Add/remove the startup project's executable to the d3dconfig application list",
                () => OverrideProblem, () => _d3dState.IsOverridden);
            Add(commandService, PackageIds.DebugLayer, (s, e) => ToggleDebugLayer(),
                "Force the D3D12 debug layer on (d3dconfig; applies to all apps in the d3dconfig list)",
                () => DebugLayerProblem, () => _d3dState.IsOverridden && _d3dState.DebugLayerOn);
            Add(commandService, PackageIds.GBV, (s, e) => ToggleGBV(),
                "Force D3D12 GPU-based validation on (d3dconfig; applies to all apps in the d3dconfig list)",
                () => GBVProblem, () => _d3dState.IsOverridden && _d3dState.DebugLayerOn && _d3dState.GBVOn);
            Add(commandService, PackageIds.VulkanConfigurator, (s, e) => LaunchVulkanConfigurator(),
                "Open Vulkan Configurator (latest installed Vulkan SDK)",
                () => _vkConfigPath == null ? "Vulkan SDK not found. " + RestartHint : null);
            Add(commandService, PackageIds.RenderDoc, (s, e) => RunInBackground(() => BuildAndLaunchAsync(pix: false)),
                "Build the startup project and open RenderDoc with its launch settings",
                () => _renderDocPath == null ? "RenderDoc not found. " + RestartHint : LaunchProblem);
            Add(commandService, PackageIds.Pix, (s, e) => RunInBackground(() => BuildAndLaunchAsync(pix: true)),
                "Build the startup project and open PIX with its launch settings",
                () => _pixPath == null ? "PIX not found. " + RestartHint : LaunchProblem);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_solutionEventsCookie != 0)
                _solution.UnadviseSolutionEvents(_solutionEventsCookie);
            if (_selectionEventsCookie != 0)
                _monitorSelection.UnadviseSelectionEvents(_selectionEventsCookie);
            _solutionEventsCookie = _selectionEventsCookie = 0;
            _buildHelper.Dispose();
        }

        /// <summary>
        /// Registers a command. <paramref name="problem"/> returns why the command is disabled, or null if it is enabled.
        /// Toolbar buttons are icon only, so the command text is what VS shows as the tooltip (the buttons have the
        /// TextChanges flag and no static ToolTipText); it carries the reason a button is disabled.
        /// </summary>
        private static void Add(OleMenuCommandService service, int id, EventHandler invoke, string description,
            Func<string> problem, Func<bool> isChecked = null)
        {
            var command = new OleMenuCommand(invoke, new CommandID(PackageGuids.CmdSet, id));
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var cmd = (OleMenuCommand)s;
                cmd.Visible = true;
                try
                {
                    var reason = problem();
                    cmd.Enabled = reason == null;
                    cmd.Text = reason == null ? description : $"{description}\n{reason}";
                    cmd.Checked = isChecked?.Invoke() ?? false;
                }
                catch (Exception ex)
                {
                    cmd.Enabled = false;
                    cmd.Text = $"{description}\nUnexpected error: {ex.Message}";
                }
            };
            service.AddCommand(command);
        }

        #region Query status

        private bool IsDebugging
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    return _dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Toggles check the same properties as their buttons, so a click queued before a button was grayed out is ignored.
        // Grayed out while the state is being (re)read, so a toggle can never race with a read.
        private string OverrideProblem
        {
            get
            {
                if (!_d3dConfig.IsAvailable)
                    return "d3dconfig.exe not found: enable the Windows optional feature 'Graphics Tools' and restart Visual Studio";
                if (_startupVcProject == null)
                    return NoStartupProject;
                if (string.IsNullOrEmpty(_startupExePath))
                    return "Could not determine the startup project's executable path";
                if (_d3dReading)
                    return "Reading d3dconfig state...";
                if (_d3dState == D3DConfigState.Empty)
                    return "Unexpected failure: d3dconfig state was not read";
                if (_d3dState.Error != null)
                    return FirstLine(_d3dState.Error) + " (see the Graphics Dev Toolbar output pane)";
                return null;
            }
        }

        private string DebugLayerProblem => OverrideProblem ?? (_d3dState.IsOverridden ? null : "Turn on D3D Override first");

        // The d3dconfig settings are independent, but the buttons show each one as depending on the previous one: a button
        // is disabled and shown as off while a previous one is off, even if its own setting is still on.
        private string GBVProblem => DebugLayerProblem ?? (_d3dState.DebugLayerOn ? null : "Turn on Debug Layer first");

        private string BuildProblem
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_operationRunning || _buildHelper.IsBusy)
                    return "A build is in progress";
                if (IsDebugging)
                    return "Stop debugging first";
                return null;
            }
        }

        private string LaunchProblem
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _startupVcProject == null ? NoStartupProject : BuildProblem;
            }
        }

        private static string FirstLine(string text)
        {
            var idx = text.IndexOfAny(new[] { '\r', '\n' });
            return idx < 0 ? text : text.Substring(0, idx);
        }

        #endregion

        #region State refresh

        private void ScheduleRefresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var version = ++_refreshVersion;
            // Runs once the UI thread is idle, so a burst of events (e.g. many projects loading at once) results in a
            // single refresh: every event bumps the version, and only the latest one refreshes.
            _ = _package.JoinableTaskFactory.StartOnIdle(async () =>
            {
                if (version != _refreshVersion)
                    return;
                try
                {
                    await RefreshAsync(version);
                }
                catch (Exception ex)
                {
                    // Refreshes are triggered by solution events rather than by the user, so they don't show message boxes.
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Log("Failed to refresh toolbar state: " + ex);
                }
            });
        }

        private async Task RefreshAsync(int version)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var projects = GetAllProjects();
            _zeroCheckProject = FindProjectByName(projects, "ZERO_CHECK");

            if (_package.Options.AutoReapplyArguments)
                ReapplyStoredArguments(projects);
            _reapplyAll = false;
            _projectsToReapply.Clear();

            ResolveStartupProject(projects);
            _d3dReading = true;
            UpdateCommandUI();

            var exe = _startupExePath;
            var state = await Task.Run(() => _d3dConfig.ReadState(exe));
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (version != _refreshVersion)
                return; // A newer refresh is running and owns _d3dReading.
            _d3dState = state;
            _d3dReading = false;
            if (state.Error != null)
                Log(state.Error);
            UpdateCommandUI();
        }

        private void ResolveStartupProject(List<Project> projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _startupProject = null;
            _startupVcProject = null;
            _startupExePath = null;

            string uniqueName = null;
            try
            {
                if (_dte.Solution?.SolutionBuild?.StartupProjects is object[] names && names.Length > 0)
                    uniqueName = names[0] as string;
            }
            catch (Exception)
            {
            }
            if (uniqueName == null)
                return;

            var project = FindProjectByUniqueName(projects, uniqueName);
            var vcProject = VcProjectHelper.GetVCProject(project);
            if (vcProject == null)
                return;

            _startupProject = project;
            _startupVcProject = vcProject;
            var cfg = VcProjectHelper.GetActiveConfiguration(project, vcProject);
            if (cfg != null)
                _startupExePath = VcProjectHelper.GetLaunchSettings(cfg).Executable;
        }

        /// <summary>
        /// Re-applies saved arguments to the projects that were loaded since the last refresh, logging the previous
        /// arguments so that they can be restored by hand.
        /// </summary>
        private void ReapplyStoredArguments(List<Project> projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var stored = _argumentStore.LoadAll();
            if (stored.Count == 0)
                return;

            foreach (var project in projects)
            {
                if (!_reapplyAll && !_projectsToReapply.Contains(SafeUniqueName(project)))
                    continue;
                var path = SafeFullName(project);
                if (string.IsNullOrEmpty(path) || !stored.TryGetValue(path, out var perConfig))
                    continue;
                var vcProject = VcProjectHelper.GetVCProject(project);
                if (vcProject == null)
                    continue;
                try
                {
                    foreach (var kv in VcProjectHelper.ApplyArguments(vcProject, perConfig))
                        LogArgumentsChange($"Re-applied saved command line arguments to {path} [{kv.Key}]", ArgumentStore.BuildCommandLine(perConfig[kv.Key]), kv.Value);
                }
                catch (Exception ex)
                {
                    Log($"Failed to re-apply arguments to {path}: {ex.Message}");
                }
            }
        }

        private void QueueReapply(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hierarchy != null && ErrorHandler.Succeeded(_solution.GetUniqueNameOfProject(hierarchy, out var uniqueName)) && uniqueName != null)
                _projectsToReapply.Add(uniqueName);
        }

        private List<Project> GetAllProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<Project>();
            try
            {
                var projects = _dte.Solution?.Projects;
                if (projects == null)
                    return result;
                foreach (Project project in projects)
                    CollectProjects(project, result);
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static void CollectProjects(Project project, List<Project> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null)
                return;
            string kind;
            try
            {
                kind = project.Kind;
            }
            catch (Exception)
            {
                return;
            }

            if (string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
            {
                var items = project.ProjectItems;
                if (items == null)
                    return;
                foreach (ProjectItem item in items)
                {
                    Project sub = null;
                    try
                    {
                        sub = item.SubProject;
                    }
                    catch (Exception)
                    {
                    }
                    CollectProjects(sub, result);
                }
            }
            else
            {
                result.Add(project);
            }
        }

        private static Project FindProjectByName(List<Project> projects, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var project in projects)
            {
                if (string.Equals(SafeName(project), name, StringComparison.OrdinalIgnoreCase))
                    return project;
            }
            return null;
        }

        private static Project FindProjectByUniqueName(List<Project> projects, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var project in projects)
            {
                if (string.Equals(SafeUniqueName(project), uniqueName, StringComparison.OrdinalIgnoreCase))
                    return project;
            }
            return null;
        }

        private static string SafeName(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.Name; } catch (Exception) { return null; }
        }

        private static string SafeUniqueName(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.UniqueName; } catch (Exception) { return null; }
        }

        private static string SafeFullName(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.FullName; } catch (Exception) { return null; }
        }

        #endregion

        #region Command handlers

        private void OnEditArguments(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = _startupProject;
            var vcProject = _startupVcProject;
            var path = SafeFullName(project);
            if (vcProject == null || string.IsNullOrEmpty(path))
                return;

            // Initial lines per configuration: saved lines if any, otherwise the project's current value split into lines.
            // The project's value also wins over saved lines that differ from it, so the window always shows the actual
            // state. This happens even with auto re-apply, which only runs on project load, e.g. after the arguments were
            // edited in the Property Pages. The differing saved lines are offered for restoring instead, which writes
            // them into the project just like re-applying does.
            if (!_argumentStore.LoadAll().TryGetValue(path, out var saved))
                saved = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            var outdated = VcProjectHelper.GetOutdatedArguments(vcProject, saved);
            var configNames = new List<string>();
            var configs = new Dictionary<string, VCConfiguration>(StringComparer.OrdinalIgnoreCase);
            var initial = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            // Configurations showing the project's value; they are saved only once their lines change, so merely
            // opening the window never overwrites saved lines.
            var unsaved = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in VcProjectHelper.AllConfigurations(vcProject))
            {
                var name = cfg.Name;
                configNames.Add(name);
                configs[name] = cfg;
                if (!saved.TryGetValue(name, out var lines) || outdated.ContainsKey(name))
                    unsaved[name] = lines = ArgumentStore.SplitCommandLine(VcProjectHelper.GetRawArguments(cfg));
                initial[name] = lines;
            }
            if (configNames.Count == 0)
                return;

            var activeConfig = VcProjectHelper.GetActiveConfiguration(project, vcProject)?.Name;
            var window = new ArgumentsWindow(SafeName(project), configNames, activeConfig, _argumentsWindowSelection, initial, outdated, (name, commandLine) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return VcProjectHelper.EvaluateText(configs[name], commandLine);
            });
            window.ShowModal();
            _argumentsWindowSelection = window.Selection;
            if (!window.HasChanges)
                return;

            var toSave = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in initial)
            {
                if (unsaved.TryGetValue(kv.Key, out var projectLines) && ArgumentStore.SameLines(kv.Value, projectLines))
                    continue;
                toSave[kv.Key] = kv.Value;
            }
            var error = SaveAndApplyArguments(path, vcProject, toSave);
            if (error != null)
                ShowError(error);
        }

        /// <summary>Saves the lines and writes them into the project. Returns an error message, or null.</summary>
        private string SaveAndApplyArguments(string path, VCProject vcProject, Dictionary<string, List<ArgumentLine>> lines)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var errors = new List<string>();
            try
            {
                _argumentStore.SaveProjectLines(path, lines);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                errors.Add("Failed to save arguments: " + ex.Message);
            }

            try
            {
                foreach (var kv in VcProjectHelper.ApplyArguments(vcProject, lines))
                    LogArgumentsChange($"Arguments for {path} [{kv.Key}]", ArgumentStore.BuildCommandLine(lines[kv.Key]), kv.Value);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                errors.Add("Failed to update project debugger arguments: " + ex.Message);
            }
            return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
        }

        /// <summary>One line per value, so an empty one is still clearly visible.</summary>
        private void LogArgumentsChange(string header, string current, string previous)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log($"{header}:{Environment.NewLine}    New:      {current}{Environment.NewLine}    Previous: {previous}");
        }

        // d3dconfig calls take ~40 ms, so toggles run synchronously on the UI thread: clicks are processed strictly in
        // order, none are dropped, and nothing (e.g. starting the debugger) can run before the setting is applied.
        private void ToggleOverride()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (OverrideProblem != null)
                return;
            var exe = _startupExePath;
            if (_d3dState.IsOverridden)
            {
                if (RunD3DConfig(_d3dConfig.RemoveApp(_d3dState.MatchedAppEntry), "apps --remove"))
                    _d3dState.MatchedAppEntry = null;
            }
            else
            {
                // d3dconfig stores entries as lower-case full paths.
                if (RunD3DConfig(_d3dConfig.AddApp(exe), "apps --add"))
                    _d3dState.MatchedAppEntry = exe.ToLowerInvariant();
            }
            UpdateCommandUI();
        }

        private void ToggleDebugLayer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (DebugLayerProblem != null)
                return;
            var mode = _d3dState.DebugLayerOn ? D3DConfigService.AppControlled : D3DConfigService.ForceOn;
            if (RunD3DConfig(_d3dConfig.SetDebugLayerMode(mode), "debug-layer-mode=" + mode))
                _d3dState.DebugLayerMode = mode;
            UpdateCommandUI();
        }

        private void ToggleGBV()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GBVProblem != null)
                return;
            var mode = _d3dState.GBVOn ? D3DConfigService.AppControlled : D3DConfigService.ForceOn;
            if (RunD3DConfig(_d3dConfig.SetGBVMode(mode), "gbv-mode=" + mode))
                _d3dState.GBVMode = mode;
            UpdateCommandUI();
        }

        private bool RunD3DConfig(ProcessResult result, string description)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (result.Succeeded)
            {
                Log($"d3dconfig {description}: ok");
                return true;
            }
            ShowError(D3DConfigService.FormatError(description, result));
            return false;
        }

        private async Task RerunCMakeAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var hierarchy = GetHierarchy(_zeroCheckProject);
            if (hierarchy == null)
                return;

            _operationRunning = true;
            UpdateCommandUI();
            try
            {
                SetStatus("Re-running CMake configuration (ZERO_CHECK)...");
                var ok = await _buildHelper.BuildAsync(hierarchy);
                SetStatus(ok ? "CMake configuration finished." : "CMake configuration failed or was cancelled.");
            }
            finally
            {
                _operationRunning = false;
                UpdateCommandUI();
            }
        }

        private async Task BuildAndLaunchAsync(bool pix)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var uniqueName = SafeUniqueName(_startupProject);
            var hierarchy = GetHierarchy(_startupProject);
            if (_startupVcProject == null || hierarchy == null)
                return;

            var toolName = pix ? "PIX" : "RenderDoc";
            _operationRunning = true;
            UpdateCommandUI();
            try
            {
                SetStatus($"Building {SafeName(_startupProject)} for {toolName}...");
                if (!await _buildHelper.BuildAsync(hierarchy))
                {
                    SetStatus($"Build failed or was cancelled; not launching {toolName}.");
                    return;
                }
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Look the project up again: in CMake solutions the build runs ZERO_CHECK, which may regenerate and
                // reload the project, leaving the objects from before the build disconnected.
                var project = FindProjectByUniqueName(GetAllProjects(), uniqueName);
                var vcProject = VcProjectHelper.GetVCProject(project);
                if (vcProject == null)
                {
                    ShowError("The startup project is no longer loaded.");
                    return;
                }

                var cfg = VcProjectHelper.GetActiveConfiguration(project, vcProject);
                if (cfg == null)
                {
                    ShowError("Could not determine the active configuration of the startup project.");
                    return;
                }
                var settings = VcProjectHelper.GetLaunchSettings(cfg);
                if (!File.Exists(settings.Executable))
                {
                    ShowError($"Executable not found:{Environment.NewLine}{settings.Executable}");
                    return;
                }

                try
                {
                    if (pix)
                        LaunchPix(settings);
                    else
                        LaunchRenderDoc(settings);
                }
                catch (Exception ex)
                {
                    ShowError($"Failed to launch {toolName}: {ex.Message}", ex);
                }
            }
            finally
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _operationRunning = false;
                UpdateCommandUI();
            }
        }

        private void LaunchRenderDoc(LaunchSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cap = RenderDocLauncher.Launch(_renderDocPath, settings);
            Log($"RenderDoc launched with capture settings {cap}");
            SetStatus("RenderDoc launched.");
        }

        private void LaunchPix(LaunchSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // PIX reads the session state only at startup and overwrites it when it exits, so the file must be updated
            // while PIX is not running.
            while (PixLauncher.IsPixRunning())
            {
                var answer = VsShellUtilities.ShowMessageBox(_package,
                    "PIX is already running.\n\n" +
                    "Please close PIX and press Retry.",
                    "Graphics Dev Toolbar", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_RETRYCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                if (answer != (int)VSConstants.MessageBoxResult.IDRETRY)
                    return;
            }

            var sessionFile = PixLauncher.FindSessionStateFile();
            if (sessionFile == null)
            {
                Log("PIX session state file not found (PIX has probably never been started); launching without pre-filled settings.");
            }
            else
            {
                PixLauncher.UpdateSessionState(sessionFile, settings);
                Log($"Updated PIX launch settings in {sessionFile}");
            }

            ProcessRunner.Launch(_pixPath);
            SetStatus("PIX launched.");
        }

        private void LaunchVulkanConfigurator()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                ProcessRunner.Launch(_vkConfigPath);
                SetStatus("Vulkan Configurator launched.");
            }
            catch (Exception ex)
            {
                ShowError($"Failed to launch Vulkan Configurator: {ex.Message}", ex);
            }
        }

        #endregion

        #region Helpers

        private IVsHierarchy GetHierarchy(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var uniqueName = project != null ? SafeUniqueName(project) : null;
            if (uniqueName == null)
                return null;
            return ErrorHandler.Succeeded(_solution.GetProjectOfUniqueName(uniqueName, out var hierarchy)) ? hierarchy : null;
        }

        private void RunInBackground(Func<Task> action)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowError(ex.Message, ex);
                }
            });
        }

        private void UpdateCommandUI()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Immediate: the toolbar must reflect new enabled/checked states before the next queued click is processed.
            _uiShell.UpdateCommandUI(1);
        }

        private void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _dte.StatusBar.Text = text;
            }
            catch (Exception)
            {
            }
            Log(text);
        }

        /// <summary>Logs the exception (or the message if there is none) and shows the message.</summary>
        private void ShowError(string message, Exception ex = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log(ex?.ToString() ?? message);
            VsShellUtilities.ShowMessageBox(_package, message, "Graphics Dev Toolbar", OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private void Log(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_outputPane == null)
            {
                var guid = OutputPaneGuid;
                _outputWindow.CreatePane(ref guid, "Graphics Dev Toolbar", 1, 1);
                _outputWindow.GetPane(ref guid, out _outputPane);
            }
            _outputPane?.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }

        #endregion

        #region IVsSolutionEvents

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ScheduleRefresh();
            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Fires for every project when a solution opens, and when a project is reloaded, e.g. after CMake regenerated it.
            QueueReapply(pHierarchy);
            ScheduleRefresh();
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _refreshVersion++;
            _startupProject = null;
            _startupVcProject = null;
            _startupExePath = null;
            _zeroCheckProject = null;
            _d3dState = D3DConfigState.Empty;
            _d3dReading = false;
            _projectsToReapply.Clear();
            _reapplyAll = false;
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ScheduleRefresh();
            return VSConstants.S_OK;
        }

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            QueueReapply(pRealHierarchy);
            ScheduleRefresh();
            return VSConstants.S_OK;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        #endregion

        #region IVsSelectionEvents

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_StartupProject)
                ScheduleRefresh();
            return VSConstants.S_OK;
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        #endregion
    }
}
