# AGENTS.md

Visual Studio 2022+ extension (VSIX, .NET Framework 4.8, `AsyncPackage`) that adds the **Graphics Dev** toolbar. Its
buttons act on the current C++ startup project: Arguments, CMake (builds `ZERO_CHECK`), D3D Override, Debug Layer, GBV,
Vulkan Configurator, RenderDoc, PIX. See `README.md` for user-facing behavior.

## Layout

- `src/GraphicsDevToolbarPackage.cs` loads in the background, probes for tools (`ToolLocator.FindAll`) before switching
  to the UI thread, gets VS services and creates the controller.
- `src/ToolbarController.cs` owns all commands, the cached state they display (startup project, exe path, ZERO_CHECK,
  d3dconfig state) and the solution, selection and build events that refresh it.
- `src/Services/` holds stateless helpers:
  - `ToolLocator`: finds d3dconfig, RenderDoc, PIX and Vulkan Configurator.
  - `D3DConfigService` + `ProcessRunner`: wrap the d3dconfig.exe command line tool.
  - `BuildHelper`: builds a single project and reports active-configuration changes.
  - `VcProjectHelper`: reads launch settings and reads/writes debugger arguments through `VCDebugSettings`.
  - `ArgumentStore`: holds the saved argument lines.
  - `RenderDocLauncher`, `PixLauncher`: open the tools with launch settings filled in.
- `src/UI/ArgumentsWindow.cs`: the arguments editor, a themed WPF `DialogWindow` built in code (no XAML).
- `src/Options/GeneralOptions.cs`: Tools > Options page.

## Build and test

- `./scripts/package.ps1` finds MSBuild via vswhere, runs restore + Rebuild (Release) and puts the output in
  `artifacts/GraphicsDevToolbar.vsix`.
- To debug, F5 the project. It starts `devenv /rootsuffix Exp`.
- There are no automated tests. Verify changes by hand in the experimental instance, ideally with a CMake-generated C++
  solution. Activity is logged to the **Graphics Dev Toolbar** output pane.
- CI: `.github/workflows/build.yml` packages on every push. `package.yml` packages, signs (Azure Artifact Signing) and
  publishes to the Marketplace (only when the `VS_MARKETPLACE_PAT` secret is set) on `vX.Y.Z` tags.

## Keep in sync

- Command IDs: `src/Commands.vsct` (`guidGraphicsDevToolbarCmdSet`) and `src/PackageIds.cs`.
- Image IDs and GUID: `src/Commands.vsct` (`guidGraphicsDevImages`) and `src/GraphicsDevToolbar.imagemanifest`. Each
  icon needs 16/24/32 PNGs in `src/Resources/Icons`.
- When adding a button, update the vsct, `PackageIds`, the image manifest and icons, `ToolbarController.RegisterCommands`
  and the button tables in `README.md` and `overview.md`.
- Version: `src/source.extension.vsixmanifest` (three parts, `X.Y.Z`), `src/Properties/AssemblyInfo.cs` and the
  `vX.Y.Z` release tag. The Marketplace rejects a version that isn't higher than the published one.
- Marketplace: the page is `overview.md` (a user-facing copy of the README without the build section) and
  `publishManifest.json` (categories, publisher, repo). Update `overview.md` when the README's button table or notes
  change. The manifest's `License` file must be a `Content` item with `IncludeInVSIX`.
  Never change the manifest `Identity Id`.
- New source files must be added to `src/GraphicsDevToolbar.csproj` (old-style project with explicit `Compile` items).

## Conventions

- Threading:
  - Every member that touches VS or DTE starts with `ThreadHelper.ThrowIfNotOnUIThread()`.
  - Async work goes through `JoinableTaskFactory`.
  - d3dconfig *reads* run on `Task.Run`. Toggles run synchronously on the UI thread on purpose, so clicks are applied in
    order.
- DTE/COM access to projects can throw (unloaded or reloaded projects). Wrap it in the `Safe*` helpers or try/catch.
  Don't hold `Project`/`VCProject` objects across a build or any other await. Look them up again by unique name.
- Buttons are icon only. The command text is the tooltip, and it carries the reason a button is disabled (the
  `*Problem` properties). Command handlers re-check the same property before acting.
- Errors are logged to the output pane (`Log`). `ShowError` (message box) is only for actions the user started.
  Refreshes triggered by events only log.
- Prefer imperative loops with early return over LINQ chains and lambda wrappers.
- Use proper VS APIs and events: no timed waits or delays, and don't write VS-owned files by hand. For example,
  arguments are written through `VCDebugSettings` + `SaveUserFile`, never by editing the `.user` file. Refreshes are
  coalesced with `StartOnIdle` + a version counter.
- Comments explain *why*, not what. Match the existing density.

## Persistence and external files

- `%LOCALAPPDATA%\GraphicsDevToolbar\arguments.json`:
  - The format is documented on `ArgumentStore`. Bump `FormatVersion` only for changes older builds would misread.
  - Saves take the named mutex `Local\GraphicsDevToolbar.Arguments` and replace the file atomically.
  - Saved lines are re-applied when a project loads, only if the option is on.
- PIX: settings are injected into its `SessionState-Win.xml`, found the way PIX itself computes the folder
  (`%LOCALAPPDATA%\Microsoft\PIX on Windows\3.1\3`; only Program Files installs are supported, see `PixLauncher.FindSessionStateFile`).
  WinPix.exe only accepts document paths on its command line, and `pixtool launch` has no UI, so this is the only way to
  pre-fill the launch dialog. This happens only while PIX is not running, and a `.bak` copy is kept.
- RenderDoc: settings are written to `%TEMP%\GraphicsDevToolbar\RenderDocLaunchSettings.cap` and passed on the command
  line.
- d3dconfig: Debug Layer and GBV settings are global (they apply to every app in the d3dconfig list).
