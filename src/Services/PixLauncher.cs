using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace GraphicsDevToolbar.Services
{
    /// <summary>
    /// PIX has no command line for pre-filling its launch dialog, so we rewrite the most recent launch configuration
    /// in its persisted session state before starting it.
    /// </summary>
    internal static class PixLauncher
    {
        private const string SessionStateFileName = "SessionState-Win.xml";
        private const int MaxRecentWin32Configurations = 10;

        public static bool IsPixRunning()
        {
            var processes = Process.GetProcessesByName("WinPix");
            foreach (var p in processes)
                p.Dispose();
            return processes.Length > 0;
        }

        /// <summary>
        /// Returns the session state file of PIX installed under Program Files (the only installs that are detected),
        /// or null if there is none yet (PIX has never been started).
        /// </summary>
        /// <remarks>
        /// Computes the folder the way PIX does (PixApplicationModel.ComputeStateStorageDirectory, as of PIX 2603.25).
        /// "3.1\3" is PIX's settings format version, which all PIX releases share, not the PIX version.
        /// </remarks>
        public static string FindSessionStateFile()
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "PIX on Windows", "3.1", "3", SessionStateFileName);
            return File.Exists(file) ? file : null;
        }

        /// <summary>Makes the given launch settings the most recent local launch configuration in PIX.</summary>
        public static void UpdateSessionState(string sessionStateFile, LaunchSettings settings)
        {
            var doc = XDocument.Load(sessionStateFile);

            // PIX always saves an entry for the local machine, whose most recent configuration pre-fills its Launch Win32
            // tab on startup. Without one, the file is not in the format this was written for.
            var state = doc.Root == null ? null : FindByAttribute(doc.Root, "State", "Key", "PCConnections");
            var computers = state?.Element("Computers");
            var computer = computers == null ? null : FindByAttribute(computers, "Computer", "IsLocalHost", "true");
            if (computer == null)
                throw new InvalidDataException($"PIX session state file has no local machine entry: {sessionStateFile}");
            // Missing while the local machine has no recent configurations yet.
            var recent = GetOrAdd(computer, "RecentConfigurations");

            // Keeps a single entry per executable. Copied, because removing an element while enumerating its siblings stops
            // the enumeration.
            foreach (var existing in new List<XElement>(recent.Elements("Configuration")))
            {
                if (string.Equals((string)existing.Element("ExecutablePath"), settings.Executable, StringComparison.OrdinalIgnoreCase))
                    existing.Remove();
            }

            var environment = new XElement("EnvironmentVariables");
            foreach (var kv in settings.Environment)
            {
                environment.Add(new XElement("EnvironmentVariable",
                    new XElement("Variable", kv.Key),
                    new XElement("Value", kv.Value)));
            }

            var configuration = new XElement("Configuration",
                new XElement("Name", Path.GetFileName(settings.Executable)),
                new XElement("Type", "Win32"),
                new XElement("ExecutablePath", settings.Executable),
                new XElement("WorkingDir", settings.WorkingDirectory),
                new XElement("CommandLineArgs", settings.Arguments),
                new XElement("LaunchSuspended", "false"),
                new XElement("LaunchForGpuCapture", "true"),
                new XElement("ForceD3D11On12", "false"),
                new XElement("EnableDREDLogging", "false"),
                environment);
            recent.AddFirst(configuration);

            // PIX keeps at most this many (RecentConfigurationStore.MaxConfigurations), but only trims when it adds one itself.
            var kept = 0;
            foreach (var existing in new List<XElement>(recent.Elements("Configuration")))
            {
                if ((string)existing.Element("Type") != "Win32")
                    continue;
                if (++kept > MaxRecentWin32Configurations)
                    existing.Remove();
            }

            // Saved the way PIX saves it (UTF-8 with BOM, indented).
            var tmp = sessionStateFile + ".tmp";
            doc.Save(tmp);
            File.Replace(tmp, sessionStateFile, sessionStateFile + ".bak");
        }

        /// <summary>Returns the first child named <paramref name="name"/> whose attribute matches the value case-insensitively.</summary>
        private static XElement FindByAttribute(XElement parent, string name, string attribute, string value)
        {
            foreach (var element in parent.Elements(name))
            {
                if (string.Equals((string)element.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase))
                    return element;
            }
            return null;
        }

        private static XElement GetOrAdd(XElement parent, string name)
        {
            var element = parent.Element(name);
            if (element == null)
            {
                element = new XElement(name);
                parent.Add(element);
            }
            return element;
        }
    }
}
