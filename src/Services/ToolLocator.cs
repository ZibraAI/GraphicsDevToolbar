using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace GraphicsDevToolbar.Services
{
    /// <summary>Paths of the installed tools; null for each one that was not found.</summary>
    internal sealed class InstalledTools
    {
        public string D3DConfig { get; set; }
        public string RenderDoc { get; set; }
        public string Pix { get; set; }
        public string VulkanConfigurator { get; set; }
    }

    /// <summary>Finds installed tools. Every method returns null instead of throwing, and may be called from any thread.</summary>
    internal static class ToolLocator
    {
        /// <summary>Probes the registry and file system, so it is called off the UI thread.</summary>
        public static InstalledTools FindAll()
        {
            return new InstalledTools
            {
                D3DConfig = FindD3DConfig(),
                RenderDoc = FindRenderDoc(),
                Pix = FindPix(),
                VulkanConfigurator = FindVulkanConfigurator(),
            };
        }

        public static string FindD3DConfig()
        {
            var exe = Path.Combine(Environment.SystemDirectory, "d3dconfig.exe");
            return File.Exists(exe) ? exe : null;
        }

        // RenderDoc registers its Vulkan layer manifest (renderdoc.json) next to qrenderdoc.exe, which also finds
        // portable and custom-location installs.
        public static string FindRenderDoc()
        {
            try
            {
                foreach (var dir in VulkanImplicitLayerDirs("renderdoc.json"))
                {
                    var exe = Path.Combine(dir, "qrenderdoc.exe");
                    if (File.Exists(exe))
                        return exe;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string FindPix()
        {
            try
            {
                foreach (var version in RegisteredPixVersions())
                {
                    var exe = PixOpenCommandExe(version);
                    if (exe != null)
                        return exe;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string FindVulkanConfigurator()
        {
            try
            {
                var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
                if (string.IsNullOrWhiteSpace(sdk))
                    return null;

                // Since 1.4.304 the GUI is vkconfig-gui.exe and vkconfig.exe is a command line tool;
                // older SDKs only have vkconfig.exe (the GUI).
                var newGUIExe = Path.Combine(sdk.Trim(), "Bin", "vkconfig-gui.exe");
                if (File.Exists(newGUIExe))
                    return newGUIExe;

                var oldGUIExe = Path.Combine(sdk.Trim(), "Bin", "vkconfig.exe");
                return File.Exists(oldGUIExe) ? oldGUIExe : null;
            }
            catch (Exception)
            {
                // Malformed VULKAN_SDK value.
                return null;
            }
        }

        /// <summary>
        /// Returns the installed PIX versions, newest first.
        /// </summary>
        private static List<string> RegisteredPixVersions()
        {
            var result = new List<string>();
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var versions = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\PIX\InstalledVersions"))
                {
                    if (versions == null)
                        return result;
                    foreach (var name in versions.GetSubKeyNames())
                    {
                        using (var sub = versions.OpenSubKey(name))
                        {
                            if (sub?.GetValue("installed") is int installed && installed == 1 && Version.TryParse(name, out _))
                                result.Add(name);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Registry access problems just mean PIX is reported as not found.
            }

            // Newest first.
            result.Sort((a, b) => Version.Parse(b).CompareTo(Version.Parse(a)));
            return result;
        }

        /// <summary>
        /// Returns the WinPix.exe path from the HKCR\PIX-&lt;version&gt; protocol handler, e.g.
        /// `"C:\Program Files\Microsoft PIX\2603.25\WinPix.exe" "%1"`. The exe path is not always quoted.
        /// </summary>
        private static string PixOpenCommandExe(string version)
        {
            using (var command = Registry.ClassesRoot.OpenSubKey($@"PIX-{version}\shell\Open\command"))
            {
                var commandLine = command?.GetValue(null) as string;
                if (commandLine == null)
                    return null;

                // Remove the "%1" argument, then the quotes around the path, if any.
                var exe = commandLine.Trim();
                if (exe.EndsWith("\"%1\""))
                    exe = exe.Substring(0, exe.Length - "\"%1\"".Length);
                exe = exe.Trim().Trim('"');

                return File.Exists(exe) ? exe : null;
            }
        }

        /// <summary>
        /// Returns the folders of the registered Vulkan implicit layer manifests named <paramref name="manifestFileName"/>.
        /// Each value name under the ImplicitLayers key is the full path of a layer manifest.
        /// </summary>
        private static IEnumerable<string> VulkanImplicitLayerDirs(string manifestFileName)
        {
            var result = new List<string>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    using (var layers = baseKey.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\ImplicitLayers"))
                    {
                        if (layers == null)
                            continue;
                        foreach (var manifestPath in layers.GetValueNames())
                        {
                            try
                            {
                                var path = manifestPath.Trim().Trim('"');
                                if (string.Equals(Path.GetFileName(path), manifestFileName, StringComparison.OrdinalIgnoreCase))
                                    result.Add(Path.GetDirectoryName(path));
                            }
                            catch (ArgumentException)
                            {
                                // Malformed path in someone else's registration; skip it.
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Registry access problems in one hive shouldn't prevent checking the other.
                }
            }
            return result;
        }
    }
}
