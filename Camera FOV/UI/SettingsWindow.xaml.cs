using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
            LoadFormula();
            LoadCameraUseSettings();
        }

        // Height and tilt (issue #16), purpose (#14), rooms (#13) and moving objects (#18)
        private void LoadCameraUseSettings()
        {
            var s = SettingsManager.Settings;
            ParamMountingHeight.Text = s.ParameterName_MountingHeight;
            ParamTilt.Text = s.ParameterName_Tilt;
            ParamIntendedCategory.Text = s.ParameterName_IntendedCategory;
            ParamRiskGrade.Text = s.ParameterName_RiskGrade;
            ParamFrameRate.Text = s.ParameterName_FrameRate;
            ParamRequiredCategory.Text = s.ParameterName_RequiredCategory;
            UseMountingCheckBox.IsChecked = s.UseMountingGeometry;
            TargetHeightBox.Text = s.TargetHeightMeters.ToString("0.##", CultureInfo.InvariantCulture);
            MaxFaceAngleBox.Text = s.MaxFaceViewAngleDegrees.ToString("0.#", CultureInfo.InvariantCulture);
            WalkingBox.Text = s.WalkingSpeedKmh.ToString("0.#", CultureInfo.InvariantCulture);
            RunningBox.Text = s.RunningSpeedKmh.ToString("0.#", CultureInfo.InvariantCulture);
            VehicleBox.Text = s.VehicleSpeedKmh.ToString("0.#", CultureInfo.InvariantCulture);
            MinFramesBox.Text = s.MinFramesPerCrossing.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryParseNumber(string text, double min, double max, out double value)
        {
            text = text?.Trim();
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            return parsed && value >= min && value <= max;
        }

        // Reads them back; returns the problems, or nothing when all can be saved
        private List<MessageDialog.Item> ReadCameraUseSettings(out Action apply)
        {
            var problems = new List<MessageDialog.Item>();
            if (!TryParseNumber(TargetHeightBox.Text, 0, 20, out double target))
                problems.Add(new MessageDialog.Item("Target height", "Enter the height in metres, from 0 to 20 (for example 1.6)."));
            if (!TryParseNumber(MaxFaceAngleBox.Text, 1, 90, out double face))
                problems.Add(new MessageDialog.Item("Steepest view for faces", "Enter an angle in degrees, from 1 to 90 (for example 30)."));
            if (!TryParseNumber(WalkingBox.Text, 0.1, 500, out double walking) ||
                !TryParseNumber(RunningBox.Text, 0.1, 500, out double running) ||
                !TryParseNumber(VehicleBox.Text, 0.1, 500, out double vehicle))
            {
                problems.Add(new MessageDialog.Item("Moving objects", "Enter each speed in km/h, above 0."));
                walking = running = vehicle = 0;
            }
            if (!TryParseNumber(MinFramesBox.Text, 1, 1000, out double frames) || frames != Math.Floor(frames))
                problems.Add(new MessageDialog.Item("Frames needed per crossing", "Enter a whole number of frames, 1 or more."));

            apply = () =>
            {
                var s = SettingsManager.Settings;
                s.ParameterName_MountingHeight = ParamMountingHeight.Text.Trim();
                s.ParameterName_Tilt = ParamTilt.Text.Trim();
                s.ParameterName_IntendedCategory = ParamIntendedCategory.Text.Trim();
                s.ParameterName_RiskGrade = ParamRiskGrade.Text.Trim();
                s.ParameterName_FrameRate = ParamFrameRate.Text.Trim();
                s.ParameterName_RequiredCategory = ParamRequiredCategory.Text.Trim();
                s.UseMountingGeometry = UseMountingCheckBox.IsChecked == true;
                s.TargetHeightMeters = target;
                s.MaxFaceViewAngleDegrees = face;
                s.WalkingSpeedKmh = walking;
                s.RunningSpeedKmh = running;
                s.VehicleSpeedKmh = vehicle;
                s.MinFramesPerCrossing = (int)frames;
            };
            return problems;
        }

        private static readonly PixelDensityFormula[] Formulas = { PixelDensityFormula.Standard, PixelDensityFormula.Legacy };

        private void LoadFormula()
        {
            FormulaComboBox.ItemsSource = Formulas.Select(CameraData.FormulaName).ToList();
            FormulaComboBox.SelectedIndex = Math.Max(0, Array.IndexOf(Formulas, SettingsManager.Settings.PixelDensityFormula));
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

            List<MessageDialog.Item> problems = ReadCameraUseSettings(out Action applyCameraUse);
            if (problems.Any())
            {
                MessageDialog.ShowWarning("Some settings aren’t valid", "Fix these and save again.", problems, owner: this);
                return;
            }
            applyCameraUse();

            string dimensionType = DimensionTypeComboBox.SelectedItem as string;
            SettingsManager.Settings.FovDimensionTypeName = dimensionType == null || dimensionType == NoDimension ? string.Empty : dimensionType;
            SettingsManager.Settings.FovDimensionDistanceMeters = dimensionDistance;
            SettingsManager.Settings.AutoFlipCameraSymbol = AutoFlipCheckBox.IsChecked == true;

            PixelDensityFormula formula = Formulas[Math.Max(0, FormulaComboBox.SelectedIndex)];
            bool formulaChanged = formula != SettingsManager.Settings.PixelDensityFormula;
            SettingsManager.Settings.PixelDensityFormula = formula;

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

            // New DORI distances in the window, and the selected camera's coverage checked against the new formula
            if (formulaChanged)
                _mainWindow.RefreshDoriDistances();

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
