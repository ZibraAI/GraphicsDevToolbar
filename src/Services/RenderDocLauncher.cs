using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GraphicsDevToolbar.Services
{
    /// <summary>Opens RenderDoc's UI with launch settings pre-filled, by handing it a capture settings (.cap) file.</summary>
    internal static class RenderDocLauncher
    {
        /// <summary>Writes the capture settings file and starts RenderDoc with it. Returns the path of the written file.</summary>
        public static string Launch(string renderDocPath, LaunchSettings settings)
        {
            var environment = new JArray();
            foreach (var kv in settings.Environment)
            {
                environment.Add(new JObject
                {
                    ["separator"] = "Platform style",
                    ["type"] = "Set",
                    ["value"] = kv.Value,
                    ["variable"] = kv.Key,
                });
            }

            var cap = new JObject
            {
                ["rdocCaptureSettings"] = 1,
                ["settings"] = new JObject
                {
                    ["autoStart"] = false,
                    ["commandLine"] = settings.Arguments,
                    ["environment"] = environment,
                    ["executable"] = settings.Executable,
                    ["inject"] = false,
                    ["numQueuedFrames"] = 0,
                    ["options"] = new JObject
                    {
                        ["allowFullscreen"] = true,
                        ["allowVSync"] = true,
                        ["apiValidation"] = false,
                        ["captureAllCmdLists"] = false,
                        ["captureCallstacks"] = false,
                        ["captureCallstacksOnlyDraws"] = false,
                        ["debugOutputMute"] = true,
                        ["delayForDebugger"] = 0,
                        ["hookIntoChildren"] = false,
                        ["refAllResources"] = false,
                        ["softMemoryLimit"] = 0,
                        ["verifyBufferAccess"] = false,
                    },
                    ["queuedFrameCap"] = 0,
                    ["workingDir"] = settings.WorkingDirectory,
                },
            };

            var dir = Path.Combine(Path.GetTempPath(), "GraphicsDevToolbar");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "RenderDocLaunchSettings.cap");
            File.WriteAllText(path, cap.ToString(), new UTF8Encoding(false));
            ProcessRunner.Launch(renderDocPath, ProcessRunner.Quote(path));
            return path;
        }
    }
}
