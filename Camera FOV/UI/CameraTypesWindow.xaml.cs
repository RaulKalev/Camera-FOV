using Document = Autodesk.Revit.DB.Document;
using ElementId = Autodesk.Revit.DB.ElementId;
using Family = Autodesk.Revit.DB.Family;
using FamilySymbol = Autodesk.Revit.DB.FamilySymbol;
using Transaction = Autodesk.Revit.DB.Transaction;
using Camera_FOV.Handlers;
using Camera_FOV.Models;
using Camera_FOV.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Camera_FOV.UI
{
    /// <summary>
    /// The camera type configurator: a library of camera models (sensor, lens, field of view ranges
    /// and custom parameters) kept in a folder that several people can share, and the Revit family
    /// types made from them. Library edits happen here; changes to the model go through Revit's
    /// external event, one transaction each.
    /// </summary>
    public partial class CameraTypesWindow : Window
    {
        private sealed class Row
        {
            public CameraType Type;
            public string Title => string.IsNullOrWhiteSpace(Type.Name) ? "Untitled camera" : Type.Name;
            public string Summary => Type.Version == 0 ? "Not saved yet" : Type.Summary;
        }

        private readonly Document _doc;
        private readonly RevitActionHandler _actions;
        private readonly Action _onRevitTypesChanged;
        private readonly ElementId _preferredFamilyId;

        private List<CameraType> _library = new List<CameraType>();
        private readonly List<CameraType> _unsaved = new List<CameraType>(); // New types, until saved
        private List<Family> _families = new List<Family>();

        private CameraType _current;   // As loaded or last saved; the form holds the edits
        private bool _dirty;
        private bool _loading;         // Filling the form: changes aren't edits
        private bool _restoringSelection;
        private readonly List<(Grid Row, TextBox Name, TextBox Value)> _parameterRows = new List<(Grid, TextBox, TextBox)>();

        private const string CustomSensor = "Custom";

        public CameraTypesWindow(Document doc, RevitActionHandler actions, ElementId preferredFamilyId, Action onRevitTypesChanged)
        {
            InitializeComponent();
            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

            _doc = doc;
            _actions = actions;
            _preferredFamilyId = preferredFamilyId;
            _onRevitTypesChanged = onRevitTypesChanged;

            SensorFormatCombo.ItemsSource = new[] { CustomSensor }.Concat(SensorFormat.Standard.Select(f => f.Name)).ToList();
            foreach (TextBox box in FindTextBoxes(EditorPanel))
                box.TextChanged += Field_Changed;
            SensorWidthBox.TextChanged += (s, e) => MatchSensorFormat();
            NameBox.TextChanged += (s, e) => UpdateApplyButton();

            PreviewKeyDown += Window_PreviewKeyDown;
            Closing += Window_Closing;

            LoadFamilies();
            Reload(null);
        }

        // ------------------------------------------------------------------
        // The library
        // ------------------------------------------------------------------

        private void Reload(string selectId)
        {
            try
            {
                _library = CameraTypeLibrary.LoadAll(out List<string> unreadable);
                if (unreadable.Any())
                    MessageDialog.ShowWarning(
                        "Some camera types couldn’t be read",
                        $"These files in {CameraTypeLibrary.Folder} aren’t valid camera types and are left out: {string.Join(", ", unreadable)}.",
                        owner: this);
            }
            catch (Exception ex)
            {
                _library = new List<CameraType>();
                MessageDialog.ShowError("Couldn’t open the camera type library", $"Check that {CameraTypeLibrary.Folder} can be reached.", ex, this);
            }

            ShowLibraryLocation();
            RefreshList(selectId ?? _current?.Id);
        }

        private void ShowLibraryLocation()
        {
            LibraryText.Text = CameraTypeLibrary.IsShared
                ? $"Shared library: {CameraTypeLibrary.Folder}"
                : "Library on this PC only. Choose a shared folder so others can use these camera types.";
            LibraryText.ToolTip = CameraTypeLibrary.Folder;
            LibraryIcon.Kind = CameraTypeLibrary.IsShared ? MaterialDesignThemes.Wpf.PackIconKind.FolderNetworkOutline : MaterialDesignThemes.Wpf.PackIconKind.FolderOutline;
        }

        private IEnumerable<CameraType> AllTypes => _unsaved.Concat(_library);

        private void RefreshList(string selectId)
        {
            string search = SearchBox.Text?.Trim() ?? string.Empty;
            SearchHint.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            List<Row> rows = AllTypes
                .Where(t => search.Length == 0 || t.Id == _current?.Id ||
                            $"{t.Name} {t.Manufacturer} {t.Model}".IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(t => t.Version == 0 ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new Row { Type = t })
                .ToList();

            _restoringSelection = true;
            TypeList.ItemsSource = rows;
            TypeList.SelectedItem = rows.FirstOrDefault(r => r.Type.Id == selectId);
            _restoringSelection = false;

            EmptyListText.Text = AllTypes.Any() ? "No camera type matches the search." : "The library is empty. Add a camera type with New.";
            EmptyListText.Visibility = rows.Any() ? Visibility.Collapsed : Visibility.Visible;

            // The open type stays as it is (with its edits) unless another one, or a reloaded copy, is selected
            if (TypeList.SelectedItem is Row selected) { if (!ReferenceEquals(selected.Type, _current)) ShowType(selected.Type); }
            else if (_current == null || !AllTypes.Any(t => t.Id == _current.Id)) ShowType(null);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList(_current?.Id);

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmLeave()) return;
            Reload(_current?.Id);
        }

        private void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection) return;

            var next = (TypeList.SelectedItem as Row)?.Type;
            if (next?.Id == _current?.Id) return;

            if (!ConfirmLeave())
            {
                // Stay on the type being edited
                _restoringSelection = true;
                TypeList.SelectedItem = TypeList.Items.OfType<Row>().FirstOrDefault(r => r.Type.Id == _current?.Id);
                _restoringSelection = false;
                return;
            }

            // Rebuilt, since leaving may have saved the type or dropped a new one
            RefreshList(next?.Id);
        }

        // Unsaved edits: save them, drop them, or stay. Returns whether it's fine to move on.
        private bool ConfirmLeave()
        {
            if (_current == null || !_dirty) return true;

            int choice = MessageDialog.Ask(MessageDialog.Kind.Warning,
                $"Save the changes to “{NameBox.Text.Trim()}”?",
                "They are lost otherwise.",
                new[] { "Save", "Don’t save", "Cancel" }, this);

            if (choice == 0) return Save();
            if (choice == 1)
            {
                if (_current.Version == 0) _unsaved.RemoveAll(t => t.Id == _current.Id);
                _dirty = false;
                return true;
            }
            return false;
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmLeave()) return;
            var type = new CameraType { Name = UniqueName("New camera") };
            _unsaved.Add(type);
            SearchBox.Text = string.Empty;
            RefreshList(type.Id);
            _dirty = true;
            UpdateButtons();
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || !TryReadForm(out CameraType edited, out _, lenient: true)) return;
            if (!ConfirmLeave()) return;

            CameraType copy = edited.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = UniqueName($"{edited.Name.Trim()} copy");
            copy.Version = 0;
            copy.ModifiedBy = null;
            copy.ModifiedUtc = null;
            _unsaved.Add(copy);
            RefreshList(copy.Id);
            _dirty = true;
            UpdateButtons();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;

            if (_current.Version == 0)
            {
                _unsaved.RemoveAll(t => t.Id == _current.Id);
                _dirty = false;
                _current = null;
                RefreshList(null);
                return;
            }

            string shared = CameraTypeLibrary.IsShared ? " It is removed for everyone using the shared library." : string.Empty;
            int choice = MessageDialog.Ask(MessageDialog.Kind.Warning,
                $"Delete “{_current.Name}”?",
                $"Revit types already made from it keep their values.{shared} It is moved to the library’s “deleted” folder, where it can be restored.",
                new[] { "Delete", "Cancel" }, this);
            if (choice != 0) return;

            try
            {
                CameraTypeLibrary.Delete(_current);
                _dirty = false;
                _current = null;
                Reload(null);
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t delete the camera type", $"Check that you can write to {CameraTypeLibrary.Folder}.", ex, this);
            }
        }

        private string UniqueName(string name)
        {
            string candidate = name;
            for (int i = 2; AllTypes.Any(t => string.Equals(t.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)); i++)
                candidate = $"{name} {i}";
            return candidate;
        }

        // ------------------------------------------------------------------
        // Library folder
        // ------------------------------------------------------------------

        private void ChangeLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            LibraryFolderBox.Text = SettingsManager.Settings.CameraTypesFolder ?? string.Empty;
            LibraryRow.Visibility = Visibility.Collapsed;
            LibraryEditRow.Visibility = Visibility.Visible;
            LibraryFolderBox.Focus();
        }

        private void CancelLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            LibraryEditRow.Visibility = Visibility.Collapsed;
            LibraryRow.Visibility = Visibility.Visible;
        }

        private void UseLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = LibraryFolderBox.Text.Trim().Trim('"');
            if (folder.Length > 0 && !System.IO.Directory.Exists(folder))
            {
                MessageDialog.ShowWarning("Folder not found",
                    $"“{folder}” doesn’t exist or can’t be reached from this PC. Create it first, or check the network path.", owner: this);
                return;
            }
            if (!ConfirmLeave()) return;

            SettingsManager.Settings.CameraTypesFolder = folder;
            SettingsManager.SaveSettings();
            _unsaved.Clear();
            _current = null;
            CancelLibraryButton_Click(sender, e);
            Reload(null);
        }

        // ------------------------------------------------------------------
        // The form
        // ------------------------------------------------------------------

        private void ShowType(CameraType type)
        {
            _current = type;
            _loading = true;

            NoSelectionText.Visibility = type == null ? Visibility.Visible : Visibility.Collapsed;
            EditorScroll.Visibility = type == null ? Visibility.Collapsed : Visibility.Visible;

            if (type != null)
            {
                NameBox.Text = type.Name;
                ManufacturerBox.Text = type.Manufacturer;
                ModelBox.Text = type.Model;
                ResolutionHBox.Text = type.HorizontalResolution > 0 ? type.HorizontalResolution.ToString(CultureInfo.InvariantCulture) : string.Empty;
                ResolutionVBox.Text = Format(type.VerticalResolution);
                SensorWidthBox.Text = Format(type.SensorWidthMm);
                SensorHeightBox.Text = Format(type.SensorHeightMm);
                SensorFormatCombo.SelectedItem = SensorFormat.Standard.Any(f => f.Name == type.SensorFormat) ? type.SensorFormat : CustomSensor;
                FocalMinBox.Text = Format(type.FocalLengthMinMm);
                FocalMaxBox.Text = Format(type.FocalLengthMaxMm);
                HFovMinBox.Text = type.HorizontalFovMax > 0 ? Format(type.HorizontalFovMin) : string.Empty;
                HFovMaxBox.Text = type.HorizontalFovMax > 0 ? Format(type.HorizontalFovMax) : string.Empty;
                VFovMinBox.Text = Format(type.VerticalFovMin);
                VFovMaxBox.Text = Format(type.VerticalFovMax);

                CustomParametersPanel.Children.Clear();
                _parameterRows.Clear();
                foreach (CustomParameter parameter in type.CustomParameters)
                    AddParameterRow(parameter.Name, parameter.Value);
                UpdateParameterHint();

                VersionText.Text = type.Version == 0
                    ? "Not saved to the library yet."
                    : $"Version {type.Version}, saved by {type.ModifiedBy ?? "unknown"}" +
                      (type.ModifiedUtc.HasValue ? $" on {type.ModifiedUtc.Value.ToLocalTime():g}." : ".");
            }

            _loading = false;
            _dirty = type != null && type.Version == 0;
            UpdateRevitCard();
            UpdateButtons();
        }

        private void Field_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _dirty = true;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            SaveButton.IsEnabled = _current != null && _dirty;
            RevertButton.IsEnabled = _current != null && _dirty && _current.Version > 0;
            DuplicateButton.IsEnabled = _current != null;
            DeleteButton.IsEnabled = _current != null;
            Title = _dirty && _current != null ? "Camera types · unsaved changes" : "Camera types";
        }

        private void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current != null) ShowType(_current);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (SaveButton.IsEnabled) Save();
                e.Handled = true;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmLeave()) e.Cancel = true;
        }

        // Reads the form into a copy of the open type. Numbers that can't be read are reported; lenient
        // skips the checks that only matter for saving.
        private bool TryReadForm(out CameraType type, out List<(string Field, string Problem)> problems, bool lenient = false)
        {
            problems = new List<(string, string)>();
            type = _current?.Clone();
            if (type == null) return false;

            var p = problems;
            double? Number(TextBox box, string field)
            {
                string text = box.Text?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                if (TryParse(text, out double value)) return value;
                p.Add((field, $"“{text}” isn’t a number."));
                return null;
            }

            type.Name = NameBox.Text?.Trim() ?? string.Empty;
            type.Manufacturer = ManufacturerBox.Text?.Trim() ?? string.Empty;
            type.Model = ModelBox.Text?.Trim() ?? string.Empty;

            double? resolutionH = Number(ResolutionHBox, "Horizontal resolution");
            double? resolutionV = Number(ResolutionVBox, "Vertical resolution");
            type.HorizontalResolution = resolutionH.HasValue ? (int)Math.Round(resolutionH.Value) : 0;
            type.VerticalResolution = resolutionV.HasValue ? (int?)Math.Round(resolutionV.Value) : null;

            string format = SensorFormatCombo.SelectedItem as string;
            type.SensorFormat = format == CustomSensor ? null : format;
            type.SensorWidthMm = Number(SensorWidthBox, "Sensor width");
            type.SensorHeightMm = Number(SensorHeightBox, "Sensor height");
            type.FocalLengthMinMm = Number(FocalMinBox, "Focal length");
            type.FocalLengthMaxMm = Number(FocalMaxBox, "Focal length") ?? type.FocalLengthMinMm;

            double? hMin = Number(HFovMinBox, "Horizontal field of view"), hMax = Number(HFovMaxBox, "Horizontal field of view");
            type.HorizontalFovMin = hMin ?? hMax ?? 0;
            type.HorizontalFovMax = hMax ?? hMin ?? 0;
            double? vMin = Number(VFovMinBox, "Vertical field of view"), vMax = Number(VFovMaxBox, "Vertical field of view");
            type.VerticalFovMin = vMin ?? vMax;
            type.VerticalFovMax = vMax ?? vMin;

            type.CustomParameters = _parameterRows
                .Select(r => new CustomParameter { Name = r.Name.Text?.Trim() ?? string.Empty, Value = r.Value.Text ?? string.Empty })
                .Where(c => c.Name.Length > 0 || c.Value.Trim().Length > 0)
                .ToList();

            if (!lenient)
                problems.AddRange(type.Validate(AllTypes));
            return !problems.Any();
        }

        private static bool TryParse(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static string Format(double? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

        private static IEnumerable<TextBox> FindTextBoxes(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is TextBox box) yield return box;
                if (child is DependencyObject node)
                    foreach (TextBox nested in FindTextBoxes(node)) yield return nested;
            }
        }

        // ------------------------------------------------------------------
        // Saving, with a check that nobody else changed the type meanwhile
        // ------------------------------------------------------------------

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

        private bool Save()
        {
            TryReadForm(out CameraType edited, out var problems);
            if (problems.Any())
            {
                MessageDialog.ShowWarning("Can’t save the camera type yet",
                    problems.Count == 1 ? "Fix this and save again." : "Fix these and save again.",
                    problems.Select(p => new MessageDialog.Item(p.Field, p.Problem)).ToList(), this);
                return false;
            }

            try
            {
                CameraType saved;
                try
                {
                    saved = CameraTypeLibrary.Save(edited);
                }
                catch (CameraTypeConflictException conflict)
                {
                    CameraType newer = conflict.Newer;
                    int choice = MessageDialog.Ask(MessageDialog.Kind.Warning,
                        $"“{newer.Name}” was changed by someone else",
                        $"{newer.ModifiedBy ?? "Someone"} saved version {newer.Version}" +
                        (newer.ModifiedUtc.HasValue ? $" on {newer.ModifiedUtc.Value.ToLocalTime():g}" : string.Empty) +
                        " after you opened it. Keep your changes and replace theirs, or load their version and lose yours?",
                        new[] { "Replace with mine", "Load theirs", "Cancel" }, this);

                    if (choice == 1)
                    {
                        _dirty = false;
                        Reload(newer.Id);
                        return false;
                    }
                    if (choice != 0) return false;

                    edited.Version = newer.Version;
                    saved = CameraTypeLibrary.Save(edited);
                }

                _unsaved.RemoveAll(t => t.Id == saved.Id);
                _library.RemoveAll(t => t.Id == saved.Id);
                _library.Add(saved);
                _dirty = false;
                _current = saved;
                RefreshList(saved.Id);
                ShowType(saved);
                return true;
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t save the camera type", $"Check that you can write to {CameraTypeLibrary.Folder}.", ex, this);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Sensor and lens
        // ------------------------------------------------------------------

        // Picking a format fills the width from the standard and the height from the resolution's aspect ratio
        private void SensorFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            SensorFormat format = SensorFormat.Standard.FirstOrDefault(f => f.Name == SensorFormatCombo.SelectedItem as string);
            if (format == null) { _dirty = true; UpdateButtons(); return; }

            _loading = true;
            SensorWidthBox.Text = Format(format.WidthMm);
            if (TryParse(ResolutionHBox.Text, out double h) && TryParse(ResolutionVBox.Text, out double v) && h > 0 && v > 0)
                SensorHeightBox.Text = Format(Math.Round(format.WidthMm * v / h, 2));
            _loading = false;
            _dirty = true;
            UpdateButtons();
        }

        // A width typed by hand that isn't a standard format's makes the format Custom
        private void MatchSensorFormat()
        {
            if (_loading || _current == null) return;
            string match = TryParse(SensorWidthBox.Text, out double width)
                ? SensorFormat.Standard.FirstOrDefault(f => Math.Abs(f.WidthMm - width) < 0.005)?.Name
                : null;

            _loading = true;
            SensorFormatCombo.SelectedItem = match ?? CustomSensor;
            _loading = false;
        }

        private void DeriveFovButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadForm(out CameraType type, out var problems, lenient: true) && problems.Any())
            {
                MessageDialog.ShowWarning("Can’t work out the field of view",
                    null, problems.Select(p => new MessageDialog.Item(p.Field, p.Problem)).ToList(), this);
                return;
            }

            string missing = type.DeriveFovFromLens();
            if (type.HorizontalFovMax > 0 && type.SensorWidthMm > 0 && type.FocalLengthMinMm > 0)
            {
                HFovMinBox.Text = Format(type.HorizontalFovMin);
                HFovMaxBox.Text = Format(type.HorizontalFovMax);
                if (type.SensorHeightMm > 0)
                {
                    VFovMinBox.Text = Format(type.VerticalFovMin);
                    VFovMaxBox.Text = Format(type.VerticalFovMax);
                }
            }

            if (missing != null)
                MessageDialog.ShowInfo(type.SensorWidthMm > 0 && type.FocalLengthMinMm > 0 ? "Horizontal field of view filled in" : "Can’t work out the field of view", missing, owner: this);
        }

        // ------------------------------------------------------------------
        // Custom parameters
        // ------------------------------------------------------------------

        private void AddParameterButton_Click(object sender, RoutedEventArgs e)
        {
            TextBox name = AddParameterRow(string.Empty, string.Empty);
            UpdateParameterHint();
            _dirty = true;
            UpdateButtons();
            name.Focus();
        }

        private TextBox AddParameterRow(string name, string value)
        {
            var row = new Grid { Style = (Style)FindResource("CardRow") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new TextBox { Text = name, Height = (double)FindResource("Size.ControlSmall"), Style = (Style)FindResource("Input.Text"), ToolTip = "Name of the family’s type parameter" };
            var valueBox = new TextBox { Text = value, Height = (double)FindResource("Size.ControlSmall"), Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("Input.Text"), ToolTip = "Value to write" };
            System.Windows.Automation.AutomationProperties.SetName(nameBox, "Custom parameter name");
            System.Windows.Automation.AutomationProperties.SetName(valueBox, "Custom parameter value");
            nameBox.TextChanged += Field_Changed;
            valueBox.TextChanged += Field_Changed;

            var remove = new Button
            {
                Margin = new Thickness(4, 0, 0, 0),
                Style = (Style)FindResource("Button.Icon"),
                ToolTip = "Remove this parameter",
                Content = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Close, Width = 14, Height = 14 }
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Remove custom parameter");

            Grid.SetColumn(valueBox, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(nameBox);
            row.Children.Add(valueBox);
            row.Children.Add(remove);

            var entry = (row, nameBox, valueBox);
            remove.Click += (s, e) =>
            {
                _parameterRows.Remove(entry);
                CustomParametersPanel.Children.Remove(row);
                UpdateParameterHint();
                _dirty = true;
                UpdateButtons();
            };

            _parameterRows.Add(entry);
            CustomParametersPanel.Children.Add(row);
            return nameBox;
        }

        private void UpdateParameterHint()
        {
            NoCustomParametersText.Visibility = _parameterRows.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Revit
        // ------------------------------------------------------------------

        private void LoadFamilies()
        {
            try
            {
                _families = _doc != null && _doc.IsValidObject ? CameraTypeLink.CameraFamilies(_doc) : new List<Family>();
            }
            catch (Exception)
            {
                _families = new List<Family>();
            }

            FamilyCombo.ItemsSource = _families.Select(f => f.Name).ToList();
            int preferred = _families.FindIndex(f => f.Id == _preferredFamilyId);
            FamilyCombo.SelectedIndex = _families.Any() ? Math.Max(0, preferred) : -1;
        }

        private Family SelectedFamily => FamilyCombo.SelectedIndex >= 0 && FamilyCombo.SelectedIndex < _families.Count ? _families[FamilyCombo.SelectedIndex] : null;

        private void FamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApplyButton();

        private void UpdateRevitCard()
        {
            WrittenParametersText.Text =
                $"Writes the horizontal resolution to “{SettingsManager.Settings.ParameterName_Resolution}” and the widest horizontal field of view to " +
                $"“{SettingsManager.Settings.ParameterName_StandardFOV}”, then each custom parameter by name. Parameter names can be changed in Settings.";

            if (_current == null) return;

            if (_doc == null || !_doc.IsValidObject)
            {
                LinkedTypesText.Text = "The project this window was opened from has been closed.";
                FamilyCombo.IsEnabled = ApplyButton.IsEnabled = false;
                return;
            }
            if (!_families.Any())
            {
                LinkedTypesText.Text = "No camera families (Security Devices) are loaded in this project. Load one, then reopen this window.";
                FamilyCombo.IsEnabled = ApplyButton.IsEnabled = false;
                return;
            }

            List<(FamilySymbol Symbol, CameraType Snapshot)> linked;
            try
            {
                linked = CameraTypeLink.FindLinkedTypes(_doc).Where(l => l.Snapshot.Id == _current.Id).ToList();
            }
            catch (Exception)
            {
                linked = new List<(FamilySymbol, CameraType)>();
            }

            // Open on the family this camera type is already used with
            if (linked.Any())
            {
                int index = _families.FindIndex(f => f.Id == linked[0].Symbol.Family.Id);
                if (index >= 0) FamilyCombo.SelectedIndex = index;
            }

            if (!linked.Any())
            {
                LinkedTypesText.Text = _current.Version == 0
                    ? "Save the camera type, then create a Revit type from it."
                    : "Not used in this project yet.";
            }
            else
            {
                LinkedTypesText.Text = "In this project:\n" + string.Join("\n", linked.Select(l =>
                {
                    string state = l.Snapshot.Version >= _current.Version
                        ? "up to date"
                        : $"made from version {l.Snapshot.Version}; update it to apply version {_current.Version}";
                    return $"{l.Symbol.Family.Name} : {l.Symbol.Name}, {state}";
                }));
            }

            FamilyCombo.IsEnabled = true;
            UpdateApplyButton();
        }

        private void UpdateApplyButton()
        {
            Family family = SelectedFamily;
            if (family == null || _current == null || _doc == null || !_doc.IsValidObject)
            {
                ApplyButton.IsEnabled = false;
                return;
            }

            string name = NameBox.Text?.Trim() ?? string.Empty;
            bool exists = family.GetFamilySymbolIds().Select(_doc.GetElement).Any(s => string.Equals(s?.Name, name, StringComparison.OrdinalIgnoreCase));
            ApplyButton.Content = exists ? "Update Revit type" : "Create Revit type";
            ApplyButton.ToolTip = exists
                ? $"Write this camera type’s values to the type “{name}” of {family.Name}"
                : $"Duplicate a type of {family.Name} as “{name}” and write this camera type’s values to it";
            ApplyButton.IsEnabled = name.Length > 0;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            Family family = SelectedFamily;
            if (family == null || _current == null) return;

            // The Revit type gets what is in the library, so unsaved edits are saved first
            if (_dirty)
            {
                int choice = MessageDialog.Ask(MessageDialog.Kind.Info,
                    "Save the camera type first?",
                    "The Revit type is made from the saved camera type.",
                    new[] { "Save and continue", "Cancel" }, this);
                if (choice != 0 || !Save()) return;
            }

            CameraType type = _current.Clone();
            ElementId familyId = family.Id;
            Document doc = _doc;

            bool queued = _actions.Enqueue($"apply “{type.Name}” to {family.Name}", app =>
            {
                if (!doc.IsValidObject || !(doc.GetElement(familyId) is Family target))
                {
                    MessageDialog.ShowWarning("The camera type wasn’t applied", "The project or the family is no longer open.", owner: this);
                    return;
                }

                CameraTypeApplyResult result;
                using (var transaction = new Transaction(doc, $"Camera type: {type.Name}"))
                {
                    transaction.Start();
                    result = CameraTypeLink.Apply(doc, target, type);
                    transaction.Commit();
                }

                ReportApply(result, target, type);
                UpdateRevitCard();
                _onRevitTypesChanged?.Invoke();
            }, out string error);

            if (!queued)
                MessageDialog.ShowWarning("Revit didn’t accept the request", error, owner: this);
        }

        private void ReportApply(CameraTypeApplyResult result, Family family, CameraType type)
        {
            var items = new List<MessageDialog.Item>();
            if (result.Written.Any())
                items.Add(new MessageDialog.Item("Written", string.Join(", ", result.Written)));
            if (result.Missing.Any())
                items.Add(new MessageDialog.Item("Not in the family",
                    $"{string.Join(", ", result.Missing)}. Add them to {family.Name} as type parameters, or check the names."));
            if (result.Failed.Any())
                items.Add(new MessageDialog.Item("Couldn’t be set",
                    $"{string.Join(", ", result.Failed)}. They may be read-only, formula-driven, or the value doesn’t suit the parameter’s type."));

            string heading = result.Created ? $"Revit type “{result.Symbol.Name}” created" : $"Revit type “{result.Symbol.Name}” updated";
            string message = $"In {family.Name}, from version {type.Version} of the camera type. Cameras of this type are limited to {CameraType.FormatRange(type.HorizontalFovMin, type.HorizontalFovMax, "°")} in the Camera FOV window.";

            if (result.Missing.Any() || result.Failed.Any())
                MessageDialog.ShowWarning(heading, message, items, this);
            else
                MessageDialog.ShowSuccess(heading, message, items, this);
        }
    }
}
