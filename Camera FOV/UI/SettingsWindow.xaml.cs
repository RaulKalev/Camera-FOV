using System;
using System.Windows;
using System.Windows.Controls;
using Camera_FOV.Services;

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
