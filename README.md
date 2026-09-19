# Run External Tool - SimHub Plugin

Runs an external executable or script whenever it is triggered from a **Control**
(a keyboard key or joystick/wheel button) or an **Event** (any of SimHub's built-in
events, such as flag changes, session start, etc.), with configurable arguments.

## How it works

SimHub itself does not distinguish "Controls" from "Events" at the plugin level.
Both are wired up through the same mechanism: a plugin registers named **Actions**,
and the user binds any Control or Event to one of those Actions from SimHub's own
"Controls and events" screen. This plugin registers one Action per command you
configure, so once it's installed you do the actual binding inside SimHub, not in
the plugin's settings screen.

Each configured command ("slot") becomes an action named `RunExternalTool.<Name>`,
where `<Name>` is the command's own Name field with spaces turned into underscores
and any other unsupported characters stripped out (so "Start Telemetry!" becomes
`RunExternalTool.Start_Telemetry`). This means the action name changes if you
rename a command - see "Renaming a command" below.
labelled with the name you gave it wherever SimHub lists actions.

## Features

- Any number of independent commands (up to 20), each with its own:
  - Executable/script path
  - Arguments (supports `{value}` = the value passed by the trigger, and
    `{timestamp}` = current date/time)
  - Working directory (defaults to the executable's own folder)
  - Run hidden / wait for exit / use shell execute toggles
  - **Ignore re-triggers while running** - if the previous run of this command
    hasn't finished yet, a new trigger is ignored instead of starting a second
    instance. This tracks the actual external process's lifetime (via its exit
    event), not just how long the plugin's own code took to launch it.
  - **Minimum seconds between runs** - a debounce: any trigger within this
    many seconds of the previous run's start is ignored. Set to 0 to disable.
  - Enabled/disabled switch
- **Wait-for-exit timeout (ms)** - a plugin-wide setting in the settings
  screen. For any command with "Wait for exit" checked, this caps how long
  the plugin waits before moving on; 0 waits indefinitely. It only stops
  waiting, it does not kill the still-running process.
- "Test run" button in the settings UI to fire a command without leaving SimHub
- **Export**/**Import** buttons (top-right of the settings screen) to save all
  commands to a JSON file or load them back in, for backing up your setup or
  moving it to another PC
- Settings persisted through SimHub's own settings store

## Building

1. Requirements: Visual Studio 2022+ (Community is fine), .NET Desktop
   Development workload, and SimHub already installed.
2. Open `SimHub.Plugin.RunExternalTool.slnx` in Visual Studio (or open
   `SimHub.Plugin.RunExternalTool/SimHub.Plugin.RunExternalTool.csproj`
   directly if your VS version doesn't yet support `.slnx`).
3. If SimHub isn't installed at `C:\Program Files (x86)\SimHub\`, edit the
   `SimHubInstallDir` property at the top of the `.csproj` file to point at your
   actual install folder (the one containing `SimHub.Plugins.dll` and
   `GameReaderCommon.dll`).
4. Build. The post-build step copies the resulting `SimHub.Plugin.RunExternalTool.dll`
   straight into your SimHub folder.
5. Press F5 to build and launch SimHub directly for debugging (this is set up
   in the `.csproj`), or just build in Release and (re)start SimHub normally.

## Installing without building yourself

Grab `SimHub.Plugin.RunExternalTool.dll` from the
[Releases](https://github.com/jbudworth/SimHub-Plugin-RunExternalTool/releases)
page, drop it into your SimHub installation folder, then start SimHub.

Alternatively, build once as above, then copy
`SimHub.Plugin.RunExternalTool.dll` from `SimHub.Plugin.RunExternalTool\bin\Debug\net48`
(or `SimHub.Plugin.RunExternalTool\bin\Release\net48`)
into your SimHub installation folder yourself.

## Using it in SimHub

1. Start SimHub. Under the plugins list you should see **Run External Tool**;
   open it and add a command: give it a name, pick the executable/script, and
   fill in arguments if needed.
2. Click **Save**.
3. Go to **Settings > Controls and events > Add new**.
4. For the input side, either:
   - pick a **Controller/Keyboard** input and press the button/key you want, or
   - pick an **Event** from SimHub's event list (flag changes, session
     started, etc.)
5. For the output, choose **Action**, then find your command by the name you
   gave it (search "RunExternalTool").
6. Save the binding. Triggering that control or event now runs your command.

## Notes

- The settings screen uses SimHub's own `SimHub.Plugins.Styles` control library
  (`SHSection`, `SHButtonPrimary`, `SHButtonSecondary`) rather than plain WPF
  controls, so it follows the user's configured accent colour and matches the
  native look of SimHub's other settings pages.
- **Import replaces every current command** with the ones from the file you
  pick (it asks for confirmation first). Export writes all current commands
  to a JSON file. Neither touches the file paths themselves - if you move a
  config to another PC, double check each command's executable and working
  directory still exist there.
- **Renaming a command** changes its action name (since the name is now part
  of it). Clicking **Save** re-registers every command's action from scratch,
  so the new name is usable the same session - but this also drops the old
  name's registration immediately, so any Control/Event binding you already
  made to the old name stops working as soon as you click Save, not just
  after a restart. Re-bind it to the new name. Give two commands the same
  name and only one of their actions will work correctly; the settings screen
  warns you if it detects this after Save.
- These two guards work regardless of how SimHub's own control mapper handles
  a held button - whether SimHub fires the action once per press or
  repeatedly while held, "ignore re-triggers while running" and the debounce
  will filter out any re-triggers that don't meet their condition.
- "Ignore re-triggers while running" only tracks one command's own runs
  against each other; separate commands (slots) are independent.

- If you add or remove commands while SimHub is running, both take effect
  immediately (removed ones stop working right away, they don't keep running
  with stale data). New ones just may not show up in the Controls and events
  list until you restart SimHub, even though the action itself is already
  registered and usable.
- "Use shell execute" lets Windows pick the associated program for a
  non-executable file (e.g. a `.ps1` you want opened with PowerShell's default
  handler) but disables hiding the window and output redirection.
- Logging goes through SimHub's own logging (`SimHub.Logging.dll`, referenced
  in the `.csproj` alongside `SimHub.Plugins.dll` and `GameReaderCommon.dll` -
  update `SimHubInstallDir` if that file isn't found at build time). Every
  trigger, success, and failure is written to SimHub's regular log, viewable
  from **Settings > Logs** inside SimHub.

## License

MIT - see [LICENSE](LICENSE).
