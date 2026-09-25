using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;

namespace SimHub.Plugin.RunExternalTool
{
    public partial class SettingsControl : UserControl
    {
        private readonly Plugin _plugin;

        public SettingsControl(Plugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            // WPF bindings default to en-US regardless of OS locale, so on e.g. a
            // German system "1,5" typed into the debounce box would parse as 15.
            // Use the actual OS culture for binding conversions instead.
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(
                System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);

            SlotsItemsControl.ItemsSource = _plugin.Settings.Slots;
            WaitForExitTimeoutMsBox.Text = _plugin.Settings.WaitForExitTimeoutMs.ToString();
        }

        private void AddSlot_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.Settings.Slots.Count >= Plugin.MaxSlots)
            {
                StatusText.Text = $"Maximum of {Plugin.MaxSlots} commands reached.";
                return;
            }

            // Pick the first "Command N" whose action name isn't already taken, so a
            // remove-then-add sequence can't silently create a duplicate action name
            // (duplicates lose the race: only one of them gets registered).
            var usedActionNames = new System.Collections.Generic.HashSet<string>(_plugin.Settings.Slots.Select(s => s.ActionName));
            CommandSlot slot;
            var n = _plugin.Settings.Slots.Count + 1;
            do
            {
                slot = new CommandSlot { Name = $"Command {n}" };
                n++;
            } while (usedActionNames.Contains(slot.ActionName));

            _plugin.Settings.Slots.Add(slot);
            RefreshList();

            // Register immediately so it is usable this session. If it doesn't show
            // up yet in the Controls and events mapper, a SimHub restart will do it.
            _plugin.ReregisterAllActions();
            _plugin.SaveSettingsNow();

            StatusText.Text = "Command added. Restart SimHub if it does not appear yet in Controls and events.";
        }

        private void RemoveSlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is CommandSlot slot)
            {
                var result = MessageBox.Show(
                    $"Remove '{slot.Name}'? Any control/event bindings pointing at it will stop working.",
                    "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                _plugin.Settings.Slots.Remove(slot);
                RefreshList();

                // Drop its action registration now rather than leaving it bound to
                // this now-removed slot's (now stale) data - see ReregisterAllActions.
                _plugin.ReregisterAllActions();
                _plugin.SaveSettingsNow();

                StatusText.Text = "Command removed.";
            }
        }

        private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is CommandSlot slot)
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Executables and scripts (*.exe;*.bat;*.cmd;*.ps1;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.py|All files (*.*)|*.*",
                    Title = "Select executable or script"
                };

                if (dialog.ShowDialog() == true)
                {
                    slot.ExecutablePath = dialog.FileName;
                }
            }
        }

        private void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is CommandSlot slot)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select any file inside the desired working directory",
                    CheckFileExists = false,
                    FileName = "Select this folder"
                };

                if (dialog.ShowDialog() == true)
                {
                    slot.WorkingDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is CommandSlot slot)
            {
                _plugin.RunSlot(slot, "test");
                StatusText.Text = $"Test triggered for '{slot.Name}'. Check the SimHub log for details.";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(WaitForExitTimeoutMsBox.Text, out var timeoutMs) && timeoutMs >= 0)
            {
                _plugin.Settings.WaitForExitTimeoutMs = timeoutMs;
            }
            else
            {
                WaitForExitTimeoutMsBox.Text = _plugin.Settings.WaitForExitTimeoutMs.ToString();
                StatusText.Text = "Wait-for-exit timeout must be a whole number of milliseconds (0 or greater). Settings not saved.";
                return;
            }

            // Action names are derived from each slot's Name, so a rename needs a
            // fresh registration to make the new name usable without waiting for a
            // SimHub restart. This also drops the old name's binding immediately
            // rather than leaving it live with stale slot data - see
            // Plugin.ReregisterAllActions.
            _plugin.ReregisterAllActions();
            _plugin.SaveSettingsNow();

            var duplicate = FindDuplicateActionName();
            StatusText.Text = duplicate != null
                ? $"Settings saved, but more than one command resolves to the action '{duplicate}'. Give them distinct names or only one will work correctly."
                : "Settings saved.";
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export Run External Tool configuration",
                Filter = "Run External Tool config (*.json)|*.json|All files (*.*)|*.*",
                FileName = "SimHub.Plugin.RunExternalTool.json"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var export = new ExportedConfig
                {
                    WaitForExitTimeoutMs = _plugin.Settings.WaitForExitTimeoutMs,
                    Slots = _plugin.Settings.Slots.Select(ExportedSlot.FromCommandSlot).ToList()
                };

                var serializer = new DataContractJsonSerializer(typeof(ExportedConfig));
                using (var stream = new FileStream(dialog.FileName, FileMode.Create))
                {
                    serializer.WriteObject(stream, export);
                }

                StatusText.Text = $"Exported {export.Slots.Count} command(s) to {Path.GetFileName(dialog.FileName)}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Export failed: {ex.Message}";
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Run External Tool configuration",
                Filter = "Run External Tool config (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            var confirm = MessageBox.Show(
                "Importing will replace all current commands with the ones from this file. Continue?",
                "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                ExportedConfig imported;
                var serializer = new DataContractJsonSerializer(typeof(ExportedConfig));
                using (var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read))
                {
                    imported = (ExportedConfig)serializer.ReadObject(stream);
                }

                _plugin.Settings.Slots.Clear();
                foreach (var exportedSlot in imported.Slots)
                {
                    _plugin.Settings.Slots.Add(exportedSlot.ToCommandSlot());
                }
                _plugin.Settings.WaitForExitTimeoutMs = imported.WaitForExitTimeoutMs;
                WaitForExitTimeoutMsBox.Text = _plugin.Settings.WaitForExitTimeoutMs.ToString();

                if (_plugin.Settings.Slots.Count == 0)
                {
                    _plugin.Settings.Slots.Add(new CommandSlot { Name = "My first command" });
                }

                // Same reasoning as Save: drop every current registration and
                // register exactly the imported slots, so nothing stays bound to
                // stale pre-import slot data under a reused name - see
                // Plugin.ReregisterAllActions.
                _plugin.ReregisterAllActions();
                _plugin.SaveSettingsNow();
                RefreshList();

                var duplicate = FindDuplicateActionName();
                StatusText.Text = duplicate != null
                    ? $"Imported {imported.Slots.Count} command(s), but more than one resolves to the action '{duplicate}'. Give them distinct names."
                    : $"Imported {imported.Slots.Count} command(s). Restart SimHub if any don't show up yet in Controls and events.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Import failed: {ex.Message}";
            }
        }

        /// <summary>Returns the first action name shared by two or more slots, or null if all are unique.</summary>
        private string FindDuplicateActionName()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var slot in _plugin.Settings.Slots)
            {
                if (!seen.Add(slot.ActionName))
                {
                    return slot.ActionName;
                }
            }
            return null;
        }

        private void RefreshList()
        {
            SlotsItemsControl.ItemsSource = null;
            SlotsItemsControl.ItemsSource = _plugin.Settings.Slots;
        }
    }
}
