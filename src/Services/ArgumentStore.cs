using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace GraphicsDevToolbar.Services
{
    internal sealed class ArgumentLine
    {
        [JsonProperty("text")]
        public string Text { get; set; } = "";

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Persists per-project, per-configuration argument lines outside of the project directory, keyed by the full
    /// project path, so they survive the project being deleted and regenerated. File format:
    /// <code>
    /// { "version": 1, "projects": { "&lt;.vcxproj path&gt;": { "configurations": { "&lt;Config|Platform&gt;": { "arguments": [ { "text": "...", "enabled": true } ] } } } } }
    /// </code>
    /// Project paths and configuration names are matched case-insensitively. Compatibility: readers ignore unknown
    /// fields, so optional fields can be added without changing the version; only a change that older builds would
    /// misread increases it. A file that can't be used (invalid JSON, missing version, or a newer version) is treated
    /// as empty and silently overwritten on the next save.
    /// </summary>
    internal sealed class ArgumentStore
    {
        private const int FormatVersion = 1;

        private static readonly string FilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GraphicsDevToolbar", "arguments.json");

        // Serializes saves across Visual Studio instances, so concurrent read-modify-writes don't lose each other's lines.
        private const string SaveMutexName = @"Local\GraphicsDevToolbar.Arguments";
        // Saves take milliseconds; this only bounds the wait if another instance hangs while holding the mutex.
        private const int SaveMutexTimeoutMs = 5000;

        private sealed class StoreFile
        {
            [JsonProperty("version", Required = Required.Always)]
            public int Version { get; set; } = FormatVersion;

            [JsonProperty("projects")]
            public Dictionary<string, ProjectEntry> Projects { get; } = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ProjectEntry
        {
            [JsonProperty("configurations")]
            public Dictionary<string, ConfigEntry> Configurations { get; } = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ConfigEntry
        {
            [JsonProperty("arguments")]
            public List<ArgumentLine> Arguments { get; set; } = new List<ArgumentLine>();
        }

        public static string BuildCommandLine(IEnumerable<ArgumentLine> lines)
        {
            // Lines are trimmed, and lines that are then empty (whitespace-only ones) are skipped entirely, 
            // so they never introduce extra separators.
            var parts = new List<string>();
            foreach (var line in lines)
            {
                if (!line.Enabled)
                    continue;
                var text = (line.Text ?? "").Trim();
                if (text.Length > 0)
                    parts.Add(text);
            }
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Splits a command line into enabled lines before every "-" or "/" that follows whitespace outside of double
        /// quotes, e.g. "-d3ddebug yes -gbv no" -> "-d3ddebug yes", "-gbv no", while "-path "C:\a -b"" stays one line.
        /// As in the MSVC runtime, a quote preceded by an odd number of backslashes is literal. Returns no lines for a
        /// blank command line.
        /// </summary>
        public static List<ArgumentLine> SplitCommandLine(string commandLine)
        {
            var result = new List<ArgumentLine>();
            if (string.IsNullOrWhiteSpace(commandLine))
                return result;

            var text = commandLine.Trim();
            var inQuotes = false;
            var backslashes = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && backslashes % 2 == 0)
                    inQuotes = !inQuotes;
                backslashes = c == '\\' ? backslashes + 1 : 0;
                if (inQuotes || !char.IsWhiteSpace(c))
                    continue;

                var next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                    next++;
                // The text is trimmed, so whitespace is always followed by a non-whitespace character.
                if (text[next] == '-' || text[next] == '/')
                {
                    result.Add(new ArgumentLine { Enabled = true, Text = text.Substring(start, i - start) });
                    start = next;
                }
                i = next - 1;
            }
            result.Add(new ArgumentLine { Enabled = true, Text = text.Substring(start) });
            return result;
        }

        /// <summary>Returns all saved lines: project path -> configuration -> lines. Returns an empty result on any error.</summary>
        public Dictionary<string, Dictionary<string, List<ArgumentLine>>> LoadAll()
        {
            StoreFile file;
            try
            {
                file = Read();
            }
            catch (Exception)
            {
                file = null;
            }

            var result = new Dictionary<string, Dictionary<string, List<ArgumentLine>>>(StringComparer.OrdinalIgnoreCase);
            if (file == null)
                return result;
            foreach (var project in file.Projects)
            {
                var configs = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
                if (project.Value != null)
                {
                    foreach (var config in project.Value.Configurations)
                    {
                        if (config.Value == null)
                            continue;
                        var lines = new List<ArgumentLine>();
                        if (config.Value.Arguments != null)
                        {
                            foreach (var line in config.Value.Arguments)
                            {
                                if (line != null)
                                    lines.Add(line);
                            }
                        }
                        configs[config.Key] = lines;
                    }
                }
                result[project.Key] = configs;
            }
            return result;
        }

        public static bool SameLines(List<ArgumentLine> a, List<ArgumentLine> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].Enabled != b[i].Enabled || a[i].Text != b[i].Text)
                    return false;
            }
            return true;
        }

        /// <summary>Stores lines for the given configurations of a project; other configurations are kept.</summary>
        public void SaveProjectLines(string projectPath, Dictionary<string, List<ArgumentLine>> linesPerConfig)
        {
            using (var mutex = new Mutex(false, SaveMutexName))
            {
                try
                {
                    if (!mutex.WaitOne(SaveMutexTimeoutMs))
                        throw new IOException("arguments.json is locked by another Visual Studio instance.");
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner exited mid-save; the file itself is always replaced atomically, so it is intact.
                }
                try
                {
                    MergeIntoFile(projectPath, linesPerConfig);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static void MergeIntoFile(string projectPath, Dictionary<string, List<ArgumentLine>> linesPerConfig)
        {
            // I/O errors propagate: overwriting a file we merely couldn't read would lose every other project's lines.
            var file = Read() ?? new StoreFile();

            var key = Path.GetFullPath(projectPath);
            if (!file.Projects.TryGetValue(key, out var project) || project == null)
                file.Projects[key] = project = new ProjectEntry();
            foreach (var kv in linesPerConfig)
                project.Configurations[kv.Key] = new ConfigEntry { Arguments = kv.Value };

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(file, Formatting.Indented));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }

        /// <summary>
        /// Reads the file. Returns an empty file if there is none, and null if its content can't be used.
        /// I/O errors are thrown.
        /// </summary>
        private static StoreFile Read()
        {
            if (!File.Exists(FilePath))
                return new StoreFile();
            var text = File.ReadAllText(FilePath);
            try
            {
                var file = JsonConvert.DeserializeObject<StoreFile>(text);
                return file != null && file.Version >= 1 && file.Version <= FormatVersion ? file : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
