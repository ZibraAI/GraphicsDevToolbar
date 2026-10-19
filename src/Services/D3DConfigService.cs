using System;
using System.Collections.Generic;
using System.IO;

namespace GraphicsDevToolbar.Services
{
    /// <summary>Snapshot of d3dconfig state relevant to one executable.</summary>
    internal sealed class D3DConfigState
    {
        public static readonly D3DConfigState Empty = new D3DConfigState();

        /// <summary>The d3dconfig "apps" entry that matches the executable, or null.</summary>
        public string MatchedAppEntry { get; set; }
        public string DebugLayerMode { get; set; } = "";
        public string GBVMode { get; set; } = "";
        public string Error { get; set; }

        public bool IsOverridden => MatchedAppEntry != null;
        public bool DebugLayerOn => IsForcedOn(DebugLayerMode);
        public bool GBVOn => IsForcedOn(GBVMode);

        private static bool IsForcedOn(string mode) =>
            string.Equals(mode, "force-on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "forced-on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Thin wrapper over the d3dconfig.exe command line tool. Methods may be called from any thread.</summary>
    internal sealed class D3DConfigService
    {
        public const string ForceOn = "force-on";
        public const string AppControlled = "app-controlled";

        public string ExePath { get; }
        public bool IsAvailable => ExePath != null;

        public D3DConfigService(string exePath)
        {
            ExePath = exePath;
        }

        public D3DConfigState ReadState(string exePath)
        {
            if (!IsAvailable || string.IsNullOrEmpty(exePath))
                return D3DConfigState.Empty;

            var state = new D3DConfigState();

            var apps = ProcessRunner.Run(ExePath, "apps");
            if (!apps.Succeeded)
            {
                state.Error = FormatError("apps", apps);
                return state;
            }
            state.MatchedAppEntry = FindMatchingEntry(ParseApps(apps.StdOut), exePath);

            var debugLayer = ProcessRunner.Run(ExePath, "debug-layer");
            if (!debugLayer.Succeeded)
            {
                state.Error = FormatError("debug-layer", debugLayer);
                return state;
            }
            var values = ParseKeyValues(debugLayer.StdOut);
            state.DebugLayerMode = values.TryGetValue("debug-layer-mode", out var dl) ? dl : "";
            state.GBVMode = values.TryGetValue("gbv-mode", out var gbv) ? gbv : "";
            return state;
        }

        public ProcessResult AddApp(string exePath) => ProcessRunner.Run(ExePath, "apps --add " + ProcessRunner.Quote(exePath));

        public ProcessResult RemoveApp(string entry) => ProcessRunner.Run(ExePath, "apps --remove " + ProcessRunner.Quote(entry));

        public ProcessResult SetDebugLayerMode(string mode) => ProcessRunner.Run(ExePath, "debug-layer debug-layer-mode=" + mode);

        public ProcessResult SetGBVMode(string mode) => ProcessRunner.Run(ExePath, "debug-layer gbv-mode=" + mode);

        public static string FormatError(string command, ProcessResult result)
        {
            var details = (result.StdErr + Environment.NewLine + result.StdOut).Trim();
            return $"d3dconfig {command} failed (exit code {result.ExitCode}).{Environment.NewLine}{details}";
        }

        internal static List<string> ParseApps(string output)
        {
            var result = new List<string>();
            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line == "apps" || line.Trim('-').Length == 0 || line.StartsWith("<", StringComparison.Ordinal))
                    continue;
                result.Add(line);
            }
            return result;
        }

        internal static Dictionary<string, string> ParseKeyValues(string output)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;
                result[raw.Substring(0, idx).Trim()] = raw.Substring(idx + 1).Trim();
            }
            return result;
        }

        internal static string FindMatchingEntry(IEnumerable<string> entries, string exePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(exePath);
            }
            catch (Exception)
            {
                fullPath = exePath;
            }
            var fileName = Path.GetFileName(fullPath);

            // A full path match wins over a file name match.
            string fileNameMatch = null;
            foreach (var entry in entries)
            {
                if (string.Equals(entry, fullPath, StringComparison.OrdinalIgnoreCase))
                    return entry;
                if (fileNameMatch == null && string.Equals(entry, fileName, StringComparison.OrdinalIgnoreCase))
                    fileNameMatch = entry;
            }
            return fileNameMatch;
        }
    }
}
