# Graphics Dev Toolbar

A Visual Studio 2022+ extension that adds a **Graphics Dev** toolbar (View > Toolbars > Graphics Dev). The buttons act on the current C++ startup project. A button is grayed out when it can't be used, and its tooltip says why.

| Button | What it does |
|---|---|
| **Arguments** | Edits the debugger command line arguments as a list of lines, each of which can be turned on or off, for one configuration or all of them. |
| **CMake** | Re-runs the CMake configure step by building `ZERO_CHECK`. |
| **D3D Override** | Adds the startup executable to the d3dconfig app list, or removes it. |
| **Debug Layer** | Forces the D3D12 debug layer on, or returns it to app-controlled. |
| **GBV** | Forces D3D12 GPU-based validation on, or returns it to app-controlled. |
| **Vulkan Configurator** | Opens Vulkan Configurator from the newest installed Vulkan SDK. |
| **RenderDoc** | Builds the project and opens RenderDoc with its executable, working directory, arguments and environment filled in. |
| **PIX** | Builds the project and opens PIX with the same launch settings. |

Notes:
- Argument lines are saved per project and configuration in `%LOCALAPPDATA%\GraphicsDevToolbar\arguments.json`, so they survive regenerating the project. Tools > Options > Graphics Dev Toolbar can re-apply them whenever a project loads. Off by default, because it overwrites the project's current arguments. The previous arguments are logged to the output pane. Arguments changed while the project stays loaded, e.g. in the Property Pages, are kept. The arguments window shows them and offers to restore the saved ones.
- The d3dconfig debug layer and GBV settings are global: they apply to every app in the d3dconfig list. The toolbar reads d3dconfig state when the solution, startup project or configuration changes.
- PIX reads its launch settings only when it starts, so the toolbar asks you to close a running PIX first.
- Tools are detected when Visual Studio starts.
- Activity is logged to the **Graphics Dev Toolbar** pane of the Output window.

## Building

Requirements: Visual Studio 2022 (17.0+) with the **Visual Studio extension development** workload, or Build Tools with MSBuild and the Visual Studio SDK.

To build, run the packaging script:

```
./scripts/package.ps1
```

This places the resulting .vsix into `artifacts/GraphicsDevToolbar.vsix`.

Alternatively, build `GraphicsDevToolbar.sln` with MSBuild.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
