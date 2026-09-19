using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text;

namespace SimHub.Plugin.RunExternalTool
{
    /// <summary>
    /// A single configurable "slot". Each slot is registered as one SimHub Action,
    /// so it can be bound to any Controller button, keyboard key, or Event from
    /// SimHub's "Controls and events" mapper.
    /// </summary>
    public class CommandSlot : INotifyPropertyChanged
    {
        private string _name = "New command";
        private string _executablePath = "";
        private string _arguments = "";
        private string _workingDirectory = "";
        private bool _waitForExit = false;
        private bool _runHidden = true;
        private bool _useShellExecute = false;
        private bool _enabled = true;
        private bool _ignoreRetriggerWhileRunning = false;
        private double _debounceSeconds = 0;

        /// <summary>
        /// Retained purely as a stable per-slot key for runtime state (tracking
        /// "is running" / debounce timing) - it no longer has any bearing on the
        /// SimHub action name, which is now derived from Name instead.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(ActionName));
            }
        }

        public string ExecutablePath
        {
            get => _executablePath;
            set { _executablePath = value; OnPropertyChanged(nameof(ExecutablePath)); }
        }

        /// <summary>
        /// Supports the token {value} which is replaced at run time with whatever
        /// value SimHub passed the action (useful for analog inputs or event payloads).
        /// </summary>
        public string Arguments
        {
            get => _arguments;
            set { _arguments = value; OnPropertyChanged(nameof(Arguments)); }
        }

        public string WorkingDirectory
        {
            get => _workingDirectory;
            set { _workingDirectory = value; OnPropertyChanged(nameof(WorkingDirectory)); }
        }

        public bool WaitForExit
        {
            get => _waitForExit;
            set { _waitForExit = value; OnPropertyChanged(nameof(WaitForExit)); }
        }

        public bool RunHidden
        {
            get => _runHidden;
            set { _runHidden = value; OnPropertyChanged(nameof(RunHidden)); }
        }

        public bool UseShellExecute
        {
            get => _useShellExecute;
            set { _useShellExecute = value; OnPropertyChanged(nameof(UseShellExecute)); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(nameof(Enabled)); }
        }

        public bool IgnoreRetriggerWhileRunning
        {
            get => _ignoreRetriggerWhileRunning;
            set { _ignoreRetriggerWhileRunning = value; OnPropertyChanged(nameof(IgnoreRetriggerWhileRunning)); }
        }

        /// <summary>Minimum time between the start of one run and the start of the next. 0 = no debounce.</summary>
        public double DebounceSeconds
        {
            get => _debounceSeconds;
            set { _debounceSeconds = value; OnPropertyChanged(nameof(DebounceSeconds)); }
        }

        /// <summary>
        /// Name shown in SimHub's control/event mapper action list. Derived from
        /// the user-entered Name, sanitized down to characters that make a safe
        /// action identifier. Renaming a command therefore changes its action
        /// name - any existing Control/Event binding pointing at the old name
        /// will need to be re-bound to the new one.
        /// </summary>
        public string ActionName => $"RunExternalTool.{SanitizeForActionName(Name)}";

        private static string SanitizeForActionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unnamed";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
                else if (char.IsWhiteSpace(c))
                {
                    sb.Append('_');
                }
                // Any other character (punctuation, symbols, etc.) is dropped.
            }

            return sb.Length == 0 ? "Unnamed" : sb.ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PluginSettings
    {
        public List<CommandSlot> Slots { get; set; } = new List<CommandSlot>();

        /// <summary>How long to wait (ms) before killing a process when WaitForExit is set, 0 = no timeout.</summary>
        public int WaitForExitTimeoutMs { get; set; } = 0;
    }

    // --- Export/import file format ------------------------------------------------
    // Deliberately kept as its own plain data-contract shape rather than reusing
    // CommandSlot/PluginSettings directly: those carry runtime-only concerns
    // (INotifyPropertyChanged, the computed ActionName, the internal Id used only
    // for in-memory state tracking) that have no business being in a config file
    // people might hand-edit or share.

    [DataContract]
    public class ExportedSlot
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string ExecutablePath { get; set; }
        [DataMember] public string Arguments { get; set; }
        [DataMember] public string WorkingDirectory { get; set; }
        [DataMember] public bool WaitForExit { get; set; }
        [DataMember] public bool RunHidden { get; set; }
        [DataMember] public bool UseShellExecute { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool IgnoreRetriggerWhileRunning { get; set; }
        [DataMember] public double DebounceSeconds { get; set; }

        public static ExportedSlot FromCommandSlot(CommandSlot slot) => new ExportedSlot
        {
            Name = slot.Name,
            ExecutablePath = slot.ExecutablePath,
            Arguments = slot.Arguments,
            WorkingDirectory = slot.WorkingDirectory,
            WaitForExit = slot.WaitForExit,
            RunHidden = slot.RunHidden,
            UseShellExecute = slot.UseShellExecute,
            Enabled = slot.Enabled,
            IgnoreRetriggerWhileRunning = slot.IgnoreRetriggerWhileRunning,
            DebounceSeconds = slot.DebounceSeconds,
        };

        /// <summary>A fresh CommandSlot gets its own new Id - that's fine, Id is never part of the exported format.</summary>
        public CommandSlot ToCommandSlot() => new CommandSlot
        {
            Name = Name,
            ExecutablePath = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            WaitForExit = WaitForExit,
            RunHidden = RunHidden,
            UseShellExecute = UseShellExecute,
            Enabled = Enabled,
            IgnoreRetriggerWhileRunning = IgnoreRetriggerWhileRunning,
            DebounceSeconds = DebounceSeconds,
        };
    }

    [DataContract]
    public class ExportedConfig
    {
        /// <summary>Bumped only if the file shape changes in a way older imports can't handle.</summary>
        [DataMember] public int FormatVersion { get; set; } = 1;
        [DataMember] public int WaitForExitTimeoutMs { get; set; }
        [DataMember] public List<ExportedSlot> Slots { get; set; } = new List<ExportedSlot>();
    }
}
