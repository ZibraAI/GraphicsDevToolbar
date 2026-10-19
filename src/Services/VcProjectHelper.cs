using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;

namespace GraphicsDevToolbar.Services
{
    /// <summary>What a debugger launch of the startup project would look like.</summary>
    internal sealed class LaunchSettings
    {
        public string Executable { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Arguments { get; set; } = "";
        public List<KeyValuePair<string, string>> Environment { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>Helpers around the C++ project system. All members must be called on the UI thread.</summary>
    internal static class VcProjectHelper
    {
        private const string ArgumentsProperty = "LocalDebuggerCommandArguments";
        private const string DebuggerRule = "WindowsLocalDebugger";

        public static VCProject GetVCProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return project?.Object as VCProject;
            }
            catch (Exception)
            {
                // Unloaded projects throw when accessing Object.
                return null;
            }
        }

        public static IEnumerable<VCConfiguration> AllConfigurations(VCProject vcProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (vcProject.Configurations is IVCCollection configs)
            {
                foreach (var item in configs)
                {
                    if (item is VCConfiguration cfg)
                        yield return cfg;
                }
            }
        }

        public static VCConfiguration GetActiveConfiguration(Project project, VCProject vcProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (vcProject.ActiveConfiguration is VCConfiguration active)
                    return active;
            }
            catch (Exception)
            {
            }

            try
            {
                var dteCfg = project.ConfigurationManager?.ActiveConfiguration;
                if (dteCfg == null)
                    return null;
                var name = dteCfg.ConfigurationName + "|" + dteCfg.PlatformName;
                foreach (var cfg in AllConfigurations(vcProject))
                {
                    if (string.Equals(cfg.Name, name, StringComparison.OrdinalIgnoreCase))
                        return cfg;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>
        /// Evaluates <paramref name="property"/> over the whole project, so values set outside the .user file (e.g. in
        /// the .vcxproj by CMake) are seen too; the .user file is imported last, so it still takes priority.
        /// </summary>
        private static string Evaluate(VCConfiguration cfg, string property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return cfg.Evaluate("$(" + property + ")") ?? "";
        }

        /// <summary>Expands the macros in <paramref name="text"/>. Returns null if the configuration can't evaluate it.</summary>
        public static string EvaluateText(VCConfiguration cfg, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return cfg.Evaluate(text);
            }
            catch (Exception)
            {
                // E.g. the project was unloaded while the arguments window is open.
                return null;
            }
        }

        /// <summary>
        /// Returns the arguments as written, with macros such as $(SolutionDir) unexpanded, so that editing and saving
        /// them keeps the macros. Like <see cref="Evaluate"/>, this sees values set in the .vcxproj too.
        /// </summary>
        public static string GetRawArguments(VCConfiguration cfg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (cfg.Rules.Item(DebuggerRule) is IVCRulePropertyStorage rule)
                    return rule.GetUnevaluatedPropertyValue(ArgumentsProperty) ?? "";
            }
            catch (Exception)
            {
            }
            // The debug settings that ApplyArguments writes; they are unevaluated as well.
            return ((VCDebugSettings)cfg.DebugSettings).CommandArguments ?? "";
        }

        public static LaunchSettings GetLaunchSettings(VCConfiguration cfg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new LaunchSettings();

            var exe = Evaluate(cfg, "LocalDebuggerCommand");
            if (string.IsNullOrWhiteSpace(exe))
                exe = Evaluate(cfg, "TargetPath");
            result.Executable = NormalizePath(exe);

            var workDir = Evaluate(cfg, "LocalDebuggerWorkingDirectory");
            if (string.IsNullOrWhiteSpace(workDir))
                workDir = SafeDirectoryName(result.Executable);
            result.WorkingDirectory = NormalizePath(workDir);

            result.Arguments = Evaluate(cfg, ArgumentsProperty);

            var env = Evaluate(cfg, "LocalDebuggerEnvironment");
            foreach (var raw in env.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;
                result.Environment.Add(new KeyValuePair<string, string>(line.Substring(0, idx), line.Substring(idx + 1)));
            }
            return result;
        }

        /// <summary>
        /// Returns the saved lines (keyed by configuration name) of the project's configurations whose current arguments
        /// differ from the command line built from them, i.e. what <see cref="ApplyArguments"/> would restore.
        /// </summary>
        public static Dictionary<string, List<ArgumentLine>> GetOutdatedArguments(VCProject vcProject, Dictionary<string, List<ArgumentLine>> savedPerConfig)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var outdated = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in AllConfigurations(vcProject))
            {
                if (!savedPerConfig.TryGetValue(cfg.Name, out var lines))
                    continue;
                if (!string.Equals(GetRawArguments(cfg), ArgumentStore.BuildCommandLine(lines), StringComparison.Ordinal))
                    outdated[cfg.Name] = lines;
            }
            return outdated;
        }

        /// <summary>
        /// Writes the command line built from <paramref name="linesPerConfig"/> (keyed by configuration name, e.g.
        /// "Debug|x64") into each configuration; configurations missing from it are left alone. Returns the configurations
        /// that changed, with their previous arguments.
        /// </summary>
        /// <remarks>
        /// Writes through the configuration's debug settings, which VS keeps and saves to the .user file itself: the
        /// debugger rule fails with E_FAIL while the project has no .user file, and writes that bypass the debug
        /// settings are overwritten by them when VS saves on exit.
        /// </remarks>
        public static Dictionary<string, string> ApplyArguments(VCProject vcProject, Dictionary<string, List<ArgumentLine>> linesPerConfig)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in AllConfigurations(vcProject))
            {
                if (!linesPerConfig.TryGetValue(cfg.Name, out var lines))
                    continue;
                // Read from the debug settings that are written below: right after a project loads, evaluating the
                // project may not see the .user file yet and return the value from the .vcxproj instead.
                var debugSettings = (VCDebugSettings)cfg.DebugSettings;
                var previous = debugSettings.CommandArguments ?? "";
                var commandLine = ArgumentStore.BuildCommandLine(lines);
                if (string.Equals(previous, commandLine, StringComparison.Ordinal))
                    continue;
                debugSettings.CommandArguments = commandLine;
                changed[cfg.Name] = previous;
            }

            if (changed.Count > 0)
            {
                try
                {
                    vcProject.SaveUserFile();
                }
                catch (Exception)
                {
                    // The values are still set in memory; VS will save the .user file later.
                }
            }
            return changed;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            path = path.Trim().Trim('"');
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string SafeDirectoryName(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? "" : Path.GetDirectoryName(path) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
