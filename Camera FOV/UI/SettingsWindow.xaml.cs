using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Camera_FOV.Models;
using Camera_FOV.Services;
using Camera_FOV.UI;

namespace Camera_FOV
{
    public partial class SettingsWindow : Window
    {
        private MainWindow _mainWindow;
        private readonly double _initialResolution;
        private bool _saved;

        public SettingsWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;

            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

            // Sync slider value
            _initialResolution = _mainWindow.GetSliderResolution();
            ValueSlider.Value = _initialResolution;

            // Load Parameter Configuration
            ParamRotation.Text = SettingsManager.Settings.ParameterName_UserRotation;
            ParamFOVOverride.Text = SettingsManager.Settings.ParameterName_FOVOverride;
            ParamStandardFOV.Text = SettingsManager.Settings.ParameterName_StandardFOV;
            ParamResolution.Text = SettingsManager.Settings.ParameterName_Resolution;

            LoadDimensionSettings();
            LoadTracingRules();
        }

        private TracingRules _initialTracingRules;
        private readonly Dictionary<string, ComboBox> _ruleChoices = new Dictionary<string, ComboBox>();

        // One row per obstacle category: its label and the two choices, the first meaning "traced".
        private static readonly (string Key, string Label, string On, string Off)[] RuleRows =
        {
            ("walls", "Walls", "Blocks view", "Ignored"),
            ("columns", "Columns", "Blocks view", "Ignored"),
            ("panels", "Curtain panels (glazing)", "Blocks view", "See-through"),
            ("mullions", "Curtain mullions", "Blocks view", "Ignored"),
            ("doors", "Doors", "Closed", "Open"),
            ("windows", "Windows", "Closed", "Open")
        };

        private void LoadTracingRules()
        {
            _initialTracingRules = _mainWindow.GetTracingRules();
            var current = new Dictionary<string, bool>
            {
                { "walls", _initialTracingRules.Walls },
                { "columns", _initialTracingRules.Columns },
                { "panels", _initialTracingRules.CurtainPanels },
                { "mullions", _initialTracingRules.Mullions },
                { "doors", _initialTracingRules.CloseDoors },
                { "windows", _initialTracingRules.CloseWindows }
            };

            for (int i = 0; i < RuleRows.Length; i++)
            {
                var row = RuleRows[i];
                if (i > 0)
                    TracingRulesPanel.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });

                var grid = new Grid { Style = (Style)FindResource("CardRow") };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new TextBlock { Text = row.Label, Style = (Style)FindResource("Type.Body") });

                var choice = new ComboBox
                {
                    MinWidth = 120,
                    Style = (Style)FindResource("Input.Combo"),
                    ItemsSource = new[] { row.On, row.Off },
                    SelectedIndex = current[row.Key] ? 0 : 1
                };
                System.Windows.Automation.AutomationProperties.SetName(choice, $"{row.Label} tracing rule");
                Grid.SetColumn(choice, 1);
                grid.Children.Add(choice);

                TracingRulesPanel.Children.Add(grid);
                _ruleChoices[row.Key] = choice;
            }
        }

        private TracingRules ReadTracingRules()
        {
            bool On(string key) => _ruleChoices[key].SelectedIndex == 0;
            return new TracingRules(On("walls"), On("columns"), On("panels"), On("mullions"), On("doors"), On("windows"));
        }

        private const string NoDimension = "No dimension";

        // Lists this project's angular dimension types. The saved choice is kept even when this
        // project lacks it, since the setting is shared by all projects.
        private void LoadDimensionSettings()
        {
            string saved = SettingsManager.Settings.FovDimensionTypeName ?? string.Empty;

            var names = new List<string> { NoDimension };
            names.AddRange(_mainWindow.GetAngularDimensionTypeNames());
            if (saved.Length > 0 && !names.Contains(saved))
                names.Add(saved);

            DimensionTypeComboBox.ItemsSource = names;
            DimensionTypeComboBox.SelectedItem = saved.Length > 0 ? saved : NoDimension;

            DimensionDistanceTextBox.Text = SettingsManager.Settings.FovDimensionDistanceMeters.ToString("0.##", CultureInfo.InvariantCulture);
            AutoFlipCheckBox.IsChecked = SettingsManager.Settings.AutoFlipCameraSymbol;
        }

        private static bool TryParseDistance(string text, out double meters)
        {
            text = text?.Trim();
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out meters)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out meters);
            return parsed && meters > 0 && meters <= 1000;
        }

        private void ActionButton1_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.CreateBoundaryLine();
        }

        private void ActionButton2_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.CreateFilledRegionTypes();
        }

        private void ActionButton3_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.TraceWalls();
        }

        private void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mainWindow != null)
            {
                _mainWindow.UpdateSliderResolution(e.NewValue);
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseDistance(DimensionDistanceTextBox.Text, out double dimensionDistance))
            {
                MessageDialog.ShowWarning(
                    "Distance isn’t valid",
                    $"“{DimensionDistanceTextBox.Text}” can’t be used as the dimension distance. Enter the distance from the camera in metres, above 0 (for example 2 or 1.5).",
                    owner: this);
                return;
            }

            string dimensionType = DimensionTypeComboBox.SelectedItem as string;
            SettingsManager.Settings.FovDimensionTypeName = dimensionType == null || dimensionType == NoDimension ? string.Empty : dimensionType;
            SettingsManager.Settings.FovDimensionDistanceMeters = dimensionDistance;
            SettingsManager.Settings.AutoFlipCameraSymbol = AutoFlipCheckBox.IsChecked == true;

            // Only written to the project when changed, so saving settings doesn't add an undo step
            TracingRules rules = ReadTracingRules();
            if (!rules.Equals(_initialTracingRules))
                _mainWindow.SaveTracingRules(rules);

            SettingsManager.Settings.ParameterName_UserRotation = ParamRotation.Text;
            SettingsManager.Settings.ParameterName_FOVOverride = ParamFOVOverride.Text;
            SettingsManager.Settings.ParameterName_StandardFOV = ParamStandardFOV.Text;
            SettingsManager.Settings.ParameterName_Resolution = ParamResolution.Text;

            SettingsManager.SaveSettings();
            _saved = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // Closing without saving (Cancel, Esc or the close button) leaves everything as it was.
        protected override void OnClosed(EventArgs e)
        {
            if (!_saved)
                _mainWindow?.UpdateSliderResolution(_initialResolution);

            base.OnClosed(e);
        }
    }
}
