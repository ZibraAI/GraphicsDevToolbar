using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GraphicsDevToolbar.Services
{
    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public bool Succeeded => ExitCode == 0;
    }

    internal static class ProcessRunner
    {
        /// <summary>Runs a console tool and captures its output. Failing to start it is reported as exit code -1.</summary>
        public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 2000)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var p = new Process { StartInfo = psi })
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    return new ProcessResult { ExitCode = -1, StdErr = $"Failed to start '{fileName}': {ex.Message}" };
                }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return new ProcessResult { ExitCode = -1, StdErr = $"'{fileName} {arguments}' timed out." };
                }
                p.WaitForExit(); // flush async output
                return new ProcessResult { ExitCode = p.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
            }
        }

        /// <summary>Starts a GUI tool from its own directory. Shell execute lets tools that need elevation prompt for it.</summary>
        public static void Launch(string fileName, string arguments = "")
        {
            using (Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fileName),
            }))
            {
            }
        }

        /// <summary>Quotes a path for a command line. Paths can't contain quotes, so no escaping is needed.</summary>
        public static string Quote(string path) => "\"" + path + "\"";
    }
}
