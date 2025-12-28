using System.Windows;
using System.Windows.Controls;
using Camera_FOV.Services;

namespace Camera_FOV
{
    public partial class SettingsWindow : Window
    {
        private MainWindow _mainWindow;

        public SettingsWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            
            // Sync resources (Theme) from MainWindow
            this.Resources.MergedDictionaries.Clear();
            foreach (var mergedDict in _mainWindow.Resources.MergedDictionaries)
            {
                this.Resources.MergedDictionaries.Add(mergedDict);
            }
            
            // Sync slider value
            ValueSlider.Value = _mainWindow.GetSliderResolution();

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
            this.Close();
        }
    }
}
