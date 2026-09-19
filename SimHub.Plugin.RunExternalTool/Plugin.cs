using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimHub.Plugin.RunExternalTool
{
    [PluginName("Run External Tool")]
    [PluginDescription("Runs an external executable or script when triggered from a Control (button/key) or an Event, with configurable arguments.")]
    [PluginAuthor("jbudworth")]
    public class Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string SettingsKey = "RunExternalToolPluginSettings";
        public const int MaxSlots = 20;

        public PluginManager PluginManager { get; set; }
        public PluginSettings Settings;

        /// <summary>
        /// Tracks, per slot, whether its process is currently running and when it
        /// was last started. This is deliberately kept separate from CommandSlot
        /// (which gets serialized to disk) since it's runtime-only state.
        /// </summary>
        private class SlotRuntimeState
        {
            public readonly object Lock = new object();
            public bool IsRunning;
            public DateTime LastRunUtc = DateTime.MinValue;
        }

        private readonly ConcurrentDictionary<Guid, SlotRuntimeState> _runtimeState = new ConcurrentDictionary<Guid, SlotRuntimeState>();

        // SimHub's own logging, from SimHub.Logging.dll. Writes into SimHub's
        // regular log alongside every other plugin, viewable from SimHub's
        // Settings > Logs screen.
        private static void Log(string level, string message)
        {
            switch (level)
            {
                case "WARN":
                    SimHub.Logging.Current.Warn($"[RunExternalTool] {message}");
                    break;
                case "ERROR":
                    SimHub.Logging.Current.Error($"[RunExternalTool] {message}");
                    break;
                default:
                    SimHub.Logging.Current.Info($"[RunExternalTool] {message}");
                    break;
            }
        }

        public string LeftMenuTitle => "Run External Tool";

        // Sidebar icon: a ">_" command-line glyph on a blue tile, embedded as a
        // WPF resource (icon.png) inside this plugin's own assembly.
        private static readonly ImageSource _pictureIcon = LoadPictureIcon();

        public ImageSource PictureIcon => _pictureIcon;

        private static ImageSource LoadPictureIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/SimHub.Plugin.RunExternalTool;component/icon.png", UriKind.Absolute);
                return new BitmapImage(uri);
            }
            catch
            {
                // If the resource can't be loaded for any reason, fall back to no icon
                // rather than crashing plugin initialisation.
                return null;
            }
        }

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings<PluginSettings>(SettingsKey, () => new PluginSettings());

            // Make sure there is always at least one slot to configure out of the box.
            if (Settings.Slots.Count == 0)
            {
                Settings.Slots.Add(new CommandSlot { Name = "My first command" });
            }

            ReregisterAllActions();
        }

        /// <summary>
        /// Drops every action currently registered under this plugin and registers
        /// one fresh SimHub Action per configured slot. Once registered, each
        /// action shows up in SimHub's Controls and events mapper, where the user
        /// can bind it to a joystick/keyboard button (a "Control") or to any of
        /// SimHub's built-in Events (flag changes, session start, etc).
        /// </summary>
        /// <remarks>
        /// PluginManager.AddAction silently does nothing if an action with the same
        /// name is already registered (it doesn't overwrite it), so simply looping
        /// AddAction over the current slots is not enough to pick up removals or
        /// name collisions with stale entries from a slot that no longer exists.
        /// ClearActions first guarantees every current slot gets a real, freshly
        /// bound registration.
        /// </remarks>
        public void ReregisterAllActions()
        {
            PluginManager.ClearActions(GetType());
            foreach (var slot in Settings.Slots)
            {
                RegisterActionForSlot(slot);
            }
        }

        private void RegisterActionForSlot(CommandSlot slot)
        {
            PluginManager.AddAction(slot.ActionName, GetType(), (pm, actionValue) =>
            {
                RunSlot(slot, actionValue);
            });
        }

        /// <summary>
        /// Executes the configured command for a slot. Safe to call directly
        /// (e.g. from the settings UI's "Test" button) as well as from the
        /// registered SimHub action.
        /// </summary>
        public void RunSlot(CommandSlot slot, string triggerValue)
        {
            if (slot == null) return;

            if (!slot.Enabled)
            {
                Log("INFO", $"Slot '{slot.Name}' is disabled, ignoring trigger.");
                return;
            }

            if (string.IsNullOrWhiteSpace(slot.ExecutablePath))
            {
                Log("WARN", $"Slot '{slot.Name}' triggered but no executable is configured.");
                return;
            }

            var state = _runtimeState.GetOrAdd(slot.Id, _ => new SlotRuntimeState());

            // Both guards are checked and claimed atomically under the same lock,
            // so two near-simultaneous triggers can't both slip through.
            lock (state.Lock)
            {
                if (slot.IgnoreRetriggerWhileRunning && state.IsRunning)
                {
                    Log("INFO", $"Slot '{slot.Name}' triggered while a previous run is still active; ignoring.");
                    return;
                }

                if (slot.DebounceSeconds > 0)
                {
                    var secondsSinceLastRun = (DateTime.UtcNow - state.LastRunUtc).TotalSeconds;
                    if (secondsSinceLastRun < slot.DebounceSeconds)
                    {
                        Log("INFO", $"Slot '{slot.Name}' triggered {secondsSinceLastRun:0.00}s after its last run; ignoring (minimum is {slot.DebounceSeconds}s).");
                        return;
                    }
                }

                state.IsRunning = true;
                state.LastRunUtc = DateTime.UtcNow;
            }

            try
            {
                string arguments = ExpandTokens(slot.Arguments, triggerValue);

                var psi = new ProcessStartInfo
                {
                    FileName = slot.ExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = slot.UseShellExecute,
                };

                if (!string.IsNullOrWhiteSpace(slot.WorkingDirectory))
                {
                    psi.WorkingDirectory = slot.WorkingDirectory;
                }
                else
                {
                    try { psi.WorkingDirectory = Path.GetDirectoryName(slot.ExecutablePath); }
                    catch { /* leave default */ }
                }

                if (slot.RunHidden)
                {
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }

                // CreateNoWindow/RedirectStandard* require UseShellExecute = false.
                if (slot.UseShellExecute)
                {
                    psi.CreateNoWindow = false;
                }

                Log("INFO", $"Running '{slot.Name}': {psi.FileName} {psi.Arguments}");

                Process process = Process.Start(psi);

                if (process == null)
                {
                    // Process.Start can return null if it attached to an already
                    // running instance instead of launching a new one.
                    lock (state.Lock) { state.IsRunning = false; }
                    return;
                }

                if (slot.WaitForExit)
                {
                    if (Settings.WaitForExitTimeoutMs > 0)
                    {
                        process.WaitForExit(Settings.WaitForExitTimeoutMs);
                    }
                    else
                    {
                        process.WaitForExit();
                    }

                    lock (state.Lock) { state.IsRunning = false; }
                    process.Dispose();
                }
                else
                {
                    // Fire-and-forget: keep "is running" accurate against the
                    // real external process lifetime by watching for it to exit,
                    // rather than clearing the flag as soon as it launches.
                    try
                    {
                        process.EnableRaisingEvents = true;
                        process.Exited += (s, e) =>
                        {
                            lock (state.Lock) { state.IsRunning = false; }
                            try { (s as Process)?.Dispose(); } catch { /* already disposed */ }
                        };
                    }
                    catch (Exception ex)
                    {
                        // If we can't watch for exit for any reason, don't leave the
                        // slot permanently marked as running - fail open instead.
                        Log("WARN", $"Could not track completion for slot '{slot.Name}', clearing running state immediately: {ex.Message}");
                        lock (state.Lock) { state.IsRunning = false; }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (state.Lock) { state.IsRunning = false; }
                Log("ERROR", $"Failed to run slot '{slot.Name}': {ex}");
            }
        }

        /// <summary>
        /// Replaces a couple of simple tokens inside the Arguments string:
        ///   {value}     -> the raw value SimHub passed to the action trigger
        ///   {timestamp} -> current local time, sortable format
        /// </summary>
        private string ExpandTokens(string arguments, string triggerValue)
        {
            if (string.IsNullOrEmpty(arguments)) return arguments;

            return arguments
                .Replace("{value}", triggerValue ?? "")
                .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // This plugin only reacts to triggered actions/events, it doesn't need
            // to inspect telemetry every frame.
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings(SettingsKey, Settings);
        }

        /// <summary>Lets the settings UI persist changes without waiting for SimHub to shut down.</summary>
        public void SaveSettingsNow()
        {
            this.SaveCommonSettings(SettingsKey, Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }
}
