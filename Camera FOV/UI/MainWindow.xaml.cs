using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using Newtonsoft.Json;
using Autodesk.Revit.DB;
using System.Windows.Media;
using System.Linq;
using Autodesk.Revit.UI.Selection;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows.Media.Animation;
using Camera_FOV.Utils;
using Camera_FOV.Handlers;
using Camera_FOV.Services;
using Camera_FOV.Models;

namespace Camera_FOV
{
    public partial class MainWindow : Window
    {
        private const string ConfigFilePath = @"C:\ProgramData\RK Tools\Camera FOV\config.json";
        private readonly WindowResizer _windowResizer;
        private bool _isDarkMode = true;
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        private readonly View _currentView;

        private readonly List<ElementCoordinates> _elementCoordinates = new List<ElementCoordinates>();
        private double _sliderResolution = SettingsManager.Settings.Resolution;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;
        private double presetRotationAngle = 0;
        private double userRotationAngle = 0; // User-defined rotation angle

        private DrawingTools _drawingTools;
        private XYZ _selectedCameraPosition;
        private double _baseCameraRotation = 0; // Auto-detected rotation from camera orientation
        private Element _selectedCameraElement = null; // Store reference to selected camera
        private bool _applyConditionalOffset = false; // Flag for conditional 180 correction

        private DrawingEventHandler _drawingEventHandler;
        private ExternalEvent _externalEvent;
        private Dictionary<CheckBox, string> _doriRegionMapping;

        private bool _isProcessingUpdate = false;
        private bool _isInitialized = false;

        private void InitializeDrawingTools()
        {
            if (_doc == null || _currentView == null)
            {
                MessageBox.Show("Document or View is not properly initialized.", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_drawingTools == null)
                _drawingTools = new DrawingTools(_doc, _currentView);

            if (_drawingEventHandler == null)
                _drawingEventHandler = new DrawingEventHandler();

            if (_externalEvent == null)
                _externalEvent = ExternalEvent.Create(_drawingEventHandler);
        }


        private double ParseMaxDistance()
        {
            if (CheckboxDetection.IsChecked == true ||
                CheckboxObservation.IsChecked == true ||
                CheckboxRecognition.IsChecked == true ||
                CheckboxIdentification.IsChecked == true)
            {
                if (double.TryParse(MaxDistanceTextBox.Text.Replace(" m", ""), out double maxDistance))
                {
                    return maxDistance;
                }
                else
                {
                    MessageBox.Show("Invalid Max Distance value.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 0;
                }
            }
            else
            {
                // Default value when no checkboxes are selected
                return 10.0; // Default 10 meters
            }
        }
        private void AttachUpdateHandlers()
        {
            FOVAngleTextBox.TextChanged += (s, e) => UpdateDetailLine(showMessage: false);
            MaxDistanceTextBox.TextChanged += (s, e) => UpdateDetailLine(showMessage: false);
            Angle0.Checked += (s, e) => UpdateDetailLine(showMessage: false);
            Angle90.Checked += (s, e) => UpdateDetailLine(showMessage: false);
            Angle180.Checked += (s, e) => UpdateDetailLine(showMessage: false);
            Angle270.Checked += (s, e) => UpdateDetailLine(showMessage: false);
        }

        private void UpdateDetailLine(bool showMessage = true)
        {

            double maxDistance = ParseMaxDistance();
            double rotationAngle = GetFinalRotationAngle();

            _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Update, _selectedCameraPosition, maxDistance, rotationAngle);
            _externalEvent.Raise();
        }
        public MainWindow(UIDocument uiDoc, Document doc, View currentView)
        {
            InitializeComponent();

            _uiDoc = uiDoc;
            _doc = doc;
            _currentView = currentView;
            // Parse the saved resolution with invariant culture
            _sliderResolution = SettingsManager.Settings.Resolution;

            // ValueSlider moved to Settings Window
            // _sliderResolution already loaded above

            InitializeDrawingTools();
            AttachUpdateHandlers();

            // Attach the TextChanged event for RotationAngleTextBox
            RotationAngleTextBox.TextChanged += (s, e) =>
            {
                if (double.TryParse(RotationAngleTextBox.Text, out double value))
                {
                    userRotationAngle = value;

                    // Trigger detail line update
                    if (_isInitialized && _selectedCameraPosition != null && _externalEvent != null)
                    {
                        double maxDistance = ParseMaxDistance();
                        double rotationAngle = GetFinalRotationAngle();

                        _drawingTools.SetParameters(_selectedCameraPosition, maxDistance, rotationAngle);

                        // Trigger the external event
                        _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Update, _selectedCameraPosition, maxDistance, rotationAngle);
                        _externalEvent.Raise();
                    }
                }
                else
                {
                    userRotationAngle = 0;
                }
            };

            Topmost = true;
            this.Closed += MainWindow_Closed;

            _windowResizer = new WindowResizer(this);
            this.MouseMove += Window_MouseMove;
            this.MouseLeftButtonUp += Window_MouseLeftButtonUp;

            if (Application.ResourceAssembly == null)
            {
                Application.ResourceAssembly = Assembly.GetExecutingAssembly();
            }

            LoadThemeState();
            LoadTheme();

            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FOVAngleTextBox.TextChanged += (s, e) => UpdateMaxDistance();
            ResolutionComboBox.SelectionChanged += (s, e) => UpdateMaxDistance();

            CheckboxDetection.Checked += (s, e) => UpdateMaxDistance();
            CheckboxObservation.Checked += (s, e) => UpdateMaxDistance();
            CheckboxRecognition.Checked += (s, e) => UpdateMaxDistance();
            CheckboxIdentification.Checked += (s, e) => UpdateMaxDistance();

            CheckboxDetection.Unchecked += (s, e) => UpdateMaxDistance();
            CheckboxObservation.Unchecked += (s, e) => UpdateMaxDistance();
            CheckboxRecognition.Unchecked += (s, e) => UpdateMaxDistance();
            CheckboxIdentification.Unchecked += (s, e) => UpdateMaxDistance();

            InitializeDrawingTools();
            InitializeDoriRegionMapping();

            _drawingEventHandler = new DrawingEventHandler();
            _drawingEventHandler.SetMainWindow(this);
            _externalEvent = ExternalEvent.Create(_drawingEventHandler);

            PopulateFilledRegionComboBox();

            LoadSettings();

            // Set initialization complete
            _isInitialized = true;
        }


        // ------------------------------
        // SETTINGS MANAGEMENT
        // ------------------------------
        private void SaveSettings()
        {
            try
            {
                var settings = SettingsManager.Settings;

                settings.FOVAngle = double.TryParse(FOVAngleTextBox.Text, out double fov) ? fov : 93.0;
                settings.Resolution = _sliderResolution;

                if (ResolutionComboBox.SelectedItem is ComboBoxItem selectedItem &&
                    int.TryParse(selectedItem.Content.ToString(), out int selectedResolution))
                {
                    settings.LastSelectedResolution = selectedResolution;
                }

                settings.IsDarkMode = ThemeToggleButton.IsChecked == true;

                SettingsManager.SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSettings()
        {
            try
            {
                var settings = SettingsManager.Settings;

                FOVAngleTextBox.Text = settings.FOVAngle.ToString(CultureInfo.InvariantCulture);
                // ValueSlider moved to SettingsWindow

                foreach (ComboBoxItem item in ResolutionComboBox.Items)
                {
                    if (item.Content.ToString() == settings.LastSelectedResolution.ToString())
                    {
                        ResolutionComboBox.SelectedItem = item;
                        break;
                    }
                }

                ThemeToggleButton.IsChecked = settings.IsDarkMode;
                LoadTheme();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

                private void MainWindow_Closed(object sender, EventArgs e)
        {
            SaveSettings();

            _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Delete);
            _externalEvent.Raise();
        }


        // ------------------------------
        // THEME MANAGEMENT
        // ------------------------------
        private void LoadThemeState()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                    if (config != null && config.ContainsKey("IsDarkMode"))
                    {
                        _isDarkMode = Convert.ToBoolean(config["IsDarkMode"]);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load theme state: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void SaveThemeState()
        {
            try
            {
                var config = new { IsDarkMode = _isDarkMode };
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
                File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(config));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save theme state: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void LoadTheme()
        {
            var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            var themeUri = _isDarkMode
                ? $"pack://application:,,,/{assemblyName};component/UI/Themes/DarkTheme.xaml"
                : $"pack://application:,,,/{assemblyName};component/UI/Themes/LightTheme.xaml";

            try
            {
                var resourceDict = new ResourceDictionary
                {
                    Source = new Uri(themeUri, UriKind.Absolute)
                };

                // Clear existing resource dictionaries except Material Design resources
                var materialDesignResources = this.Resources.MergedDictionaries
                    .Where(rd => rd.Source != null && rd.Source.ToString().Contains("MaterialDesign"))
                    .ToList();

                this.Resources.MergedDictionaries.Clear();
                foreach (var rd in materialDesignResources)
                {
                    this.Resources.MergedDictionaries.Add(rd);
                }

                // Add the selected theme resource dictionary
                this.Resources.MergedDictionaries.Add(resourceDict);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load theme: {ex.Message}", "Theme Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            _isDarkMode = ThemeToggleButton.IsChecked == true;
            LoadTheme();
            SaveThemeState();
        }

        // ------------------------------
        // CAMERA SETTINGS
        // ------------------------------
        private void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMaxDistance();
        }

        private void FOVAngleTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMaxDistance();
        }
        private decimal CalculateDORIDistance(int resolution, decimal fov, decimal ppm)
        {
            decimal A = resolution / ppm;
            decimal B = 360 / fov;
            decimal C = A * B;
            decimal D = 2 * (decimal)Math.PI;
            decimal E = C / D;
            return Math.Round(E * 0.3048m, 1); // Convert to meters and round to one decimal place
        }
        private decimal? GetSelectedDORIDistance(int resolution, decimal fov)
        {
            decimal d = 7.62m; // Detection
            decimal o = 19.2024m; // Observation
            decimal r = 38.1m; // Recognition
            decimal i = 76.2m; // Identification

            if (CheckboxDetection.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, d);
            if (CheckboxObservation.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, o);
            if (CheckboxRecognition.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, r);
            if (CheckboxIdentification.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, i);

            return null; // No checkbox selected
        }
        private void InitializeDoriRegionMapping()
        {
            _doriRegionMapping = new Dictionary<CheckBox, string>
    {
        { CheckboxDetection, "dori_25px" },
        { CheckboxObservation, "dori_63px" },
        { CheckboxRecognition, "dori_125px" },
        { CheckboxIdentification, "dori_250px" }
    };
        }
        private void UpdateMaxDistance()
        {
            try
            {
                if (!(ResolutionComboBox.SelectedItem is ComboBoxItem selectedItem &&
                      int.TryParse(selectedItem.Content.ToString(), out int resolution)))
                {
                    MaxDistanceTextBox.Text = "Invalid Resolution";
                    return;
                }

                if (!decimal.TryParse(FOVAngleTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal fov) || fov <= 0)
                {
                    MaxDistanceTextBox.Text = "Invalid FOV";
                    return;
                }

                decimal? distance = GetSelectedDORIDistance(resolution, fov);
                if (distance.HasValue)
                {
                    MaxDistanceTextBox.Text = $"{distance.Value} m";
                }
                else
                {
                    MaxDistanceTextBox.Text = "Select a DORI";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating max distance: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private double GetFinalRotationAngle()
        {
            // Parse current user value from textbox
            double currentTextboxValue = 0;
            if (double.TryParse(RotationAngleTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedVal))
            {
                currentTextboxValue = parsedVal;
            }

            // Combine base camera rotation with user rotation + preset
            double combinedAngle = _baseCameraRotation + currentTextboxValue + presetRotationAngle;
            
            // Apply conditional 180° correction based on selection state
            if (_applyConditionalOffset)
            {
                combinedAngle += 180;
            }
            
            // Normalize to 0-360 range
            while (combinedAngle < 0)
                combinedAngle += 360;
            while (combinedAngle >= 360)
                combinedAngle -= 360;
            
            return combinedAngle;
        }

        private class ElementCoordinates
        {
            public int ElementId { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        // ------------------------------
        // WINDOW EDGE HANDLERS
        // ------------------------------
        // Handles mouse movement during window resizing
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _windowResizer.ResizeWindow(e);
            }
        }

        // Stops resizing when the mouse button is released
        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _windowResizer.StopResizing();
        }
        // Handle cursor change when hovering over edges
        private void RotationAngle_Checked(object sender, RoutedEventArgs e)
        {
            if (Angle0.IsChecked == true)
            {
                presetRotationAngle = 0;
            }
            else if (Angle90.IsChecked == true)
            {
                presetRotationAngle = 90;
            }
            else if (Angle180.IsChecked == true)
            {
                presetRotationAngle = 180;
            }
            else if (Angle270.IsChecked == true)
            {
                presetRotationAngle = 270;
            }

            TriggerDetailLineUpdate(); // Update detail line with new preset rotation
        }
        public class SecurityDeviceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem.Category != null && elem.Category.Id.Value == (int)BuiltInCategory.OST_SecurityDevices;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false; // We are only selecting elements, not references
            }
        }
        private void SelectElementsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Hide the current window but keep Revit window intact
                this.Hide();
                this.Topmost = false;

                // Bring Revit to the foreground without changing its size or state
                IntPtr revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                {
                    SetForegroundWindow(revitHandle); // Bring Revit to the foreground
                }

                // Allow the user to select a single Security Device element
                Reference selectedReference = _uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new SecurityDeviceSelectionFilter(), // Use the custom filter
                    "Select a camera element");

                if (selectedReference != null)
                {
                    Element element = _doc.GetElement(selectedReference);
                    if (element?.Location is LocationPoint locationPoint)
                    {
                        // Get the camera's position
                        _selectedCameraPosition = locationPoint.Point;

                        // Attempt to auto-detect camera facing direction
                        double autoDetectedAngle = 0;
                        bool angleDetected = false;

                        // Method 1: Try LocationPoint.Rotation (most reliable for plan view rotation)
                        try
                        {
                            double rotation = locationPoint.Rotation;
                            double rotationDegrees = rotation * (180.0 / Math.PI);
                            
                            // Correct for camera family orientation (family's 0° = down, Revit's 0° = right)
                            // Subtract 90° to align with actual facing direction
                            autoDetectedAngle = rotationDegrees - 90.0;
                            
                            // Normalize to 0-360 range
                            while (autoDetectedAngle < 0)
                                autoDetectedAngle += 360;
                            while (autoDetectedAngle >= 360)
                                autoDetectedAngle -= 360;
                            
                            angleDetected = true;
                        }
                        catch
                        {
                            // LocationPoint.Rotation failed, try transform-based method
                            if (element is FamilyInstance familyInstance)
                            {
                                try
                                {
                                    Autodesk.Revit.DB.Transform transform = familyInstance.GetTransform();
                                    XYZ facingDirection = transform.BasisY; // BasisY typically represents the forward direction for many families
                                    
                                    double angleRadians = Math.Atan2(facingDirection.Y, facingDirection.X);
                                    autoDetectedAngle = (angleRadians * (180.0 / Math.PI)) - 90.0; // Adjust for family orientation
                                    
                                    // Normalize to 0-360 range
                                    while (autoDetectedAngle < 0)
                                        autoDetectedAngle += 360;
                                    while (autoDetectedAngle >= 360)
                                        autoDetectedAngle -= 360;
                                    
                                    angleDetected = true;
                                }
                                catch
                                {
                                    angleDetected = false;
                                }
                            }
                        }

                        // Store the auto-detected base rotation silently
                        if (angleDetected)
                        {
                            _baseCameraRotation = autoDetectedAngle;
                        }
                        else
                        {
                            _baseCameraRotation = 0;
                        }

                        // Read "Pööra Kaamerat" parameter (User rotation adjustment)
                        double userRotation = 0;
                        bool foundParam = false;
                        
                        // 1. Try "Pööra Kaamerat" (Instance)
                        Parameter p1 = element.LookupParameter(SettingsManager.Settings.ParameterName_UserRotation);
                        if (p1 != null)
                        {
                            double val = p1.AsDouble();
                            userRotation = val * (180.0 / Math.PI);
                            foundParam = true;
                        }

                        // Determine if we need the conditional 180 offset based on initial value
                        // FIX: Only apply if value is strictly positive (checking against small epsilon)
                        _applyConditionalOffset = foundParam && (userRotation > 0.001);

                        // Store reference to element for parameter write-back
                        _selectedCameraElement = element;

                        // Display only the user rotation in UI (base rotation is applied silently)
                        RotationAngleTextBox.Text = userRotation.ToString("F0", CultureInfo.InvariantCulture);

                        // FOV Logic
                        // Priority 1: "Kaamera nurk" (Instance) - Manual Override if > 0
                        // Priority 2: "Vaatenurk" (Instance) - Standard
                        // Priority 3: "Vaatenurk" (Type) - Standard Fallback

                        double finalFovDegrees = 0;
                        bool fovFound = false;

                        // 1. Check "Kaamera nurk" (Override)
                        Parameter knInst = element.LookupParameter(SettingsManager.Settings.ParameterName_FOVOverride);
                        if (knInst != null)
                        {
                            double val = knInst.AsDouble();
                            if (Math.Abs(val) > 0.001)
                            {
                                finalFovDegrees = val * (180.0 / Math.PI);
                                fovFound = true;
                            }
                        }

                        // 2. Check "Vaatenurk" (Instance)
                        if (!fovFound)
                        {
                             Parameter vnInst = element.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV);
                             if (vnInst != null)
                             {
                                 finalFovDegrees = vnInst.AsDouble() * (180.0 / Math.PI);
                                 fovFound = true;
                             }
                        }

                        // 3. Check "Vaatenurk" (Type)
                        if (!fovFound)
                        {
                            ElementId typeId = element.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId)
                            {
                                if (_doc.GetElement(typeId) is ElementType elementType)
                                {
                                    Parameter vnType = elementType.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV);
                                    if (vnType != null)
                                    {
                                        finalFovDegrees = vnType.AsDouble() * (180.0 / Math.PI);
                                        fovFound = true;
                                    }
                                }
                            }
                        }

                        if (fovFound)
                        {
                            FOVAngleTextBox.Text = finalFovDegrees.ToString("F0", CultureInfo.InvariantCulture);
                        }

                        // Read "Horisontaalne Resolutsioon" parameter (Resolution)
                        Parameter resolutionParameter = element.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);

                        // If not found on instance, try on type
                        if (resolutionParameter == null)
                        {
                            ElementId typeId = element.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId)
                            {
                                if (_doc.GetElement(typeId) is ElementType elementType)
                                {
                                    resolutionParameter = elementType.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
                                }
                            }
                        }

                        // Read "Horisontaalne Resolutsioon" parameter (Resolution) with robust fallback
                        int resolvedValue;
                        if (TryGetResolutionFromInstanceOrType(element, out resolvedValue))
                        {
                            SelectResolutionInComboOrFallback(resolvedValue);
                        }
                        else
                        {
                            // Parameter missing on both instance and type: use saved fallback without interrupting the user
                            SelectResolutionInComboOrFallback();
                        }

                        // Continue with drawing updates if needed
                        if (_externalEvent != null)
                        {
                            double maxDistance = ParseMaxDistance();
                            double rotationAngle = GetFinalRotationAngle();

                            _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Update, _selectedCameraPosition, maxDistance, rotationAngle);
                            _externalEvent.Raise();
                        }
                    }
                }

                // Restore the plugin window without resizing Revit
                this.Show();
                this.Topmost = true;
                this.Activate();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Restore the plugin window without resizing Revit
                this.Show();
                this.Topmost = true;
                this.Activate();
                MessageBox.Show("Selection canceled by user.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                // Restore the plugin window without resizing Revit
                this.Show();
                this.Topmost = true;
                this.Activate();
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Populate Filled Region ComboBox on window load
        private void PopulateFilledRegionComboBox()
        {
            try
            {
                // Get all Filled Region Types
                var filledRegionTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .Where(regionType => regionType.Name.Contains("dori_")) // Filter names containing "dori_"
                    .ToList();

                // Clear ComboBox and add filtered types
                FilledRegionComboBox.Items.Clear();
                foreach (var regionType in filledRegionTypes)
                {
                    FilledRegionComboBox.Items.Add(regionType.Name);
                }

                // Set default selection if available
                if (FilledRegionComboBox.Items.Count > 0)
                    FilledRegionComboBox.SelectedIndex = 0;
                else
                    MessageBox.Show("No Filled Regions found with 'dori_' in their names.\nThese regions are needed for the drawing process.\nYou can create them manualy or use the &quot;Create filled region types&quot; button in the setup section.", "No Matches", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to populate Filled Regions: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void FilledRegionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FilledRegionComboBox.SelectedItem == null)
                {
                    MessageBox.Show("Please select a filled region type.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string selectedRegion = FilledRegionComboBox.SelectedItem.ToString();
                var filledRegionType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(r => r.Name == selectedRegion);

                if (filledRegionType == null)
                {
                    MessageBox.Show("The selected filled region type could not be found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (_selectedCameraPosition == null)
                {
                    MessageBox.Show("No camera selected. Please select a camera position.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double maxDistance = ParseMaxDistance();
                double rotationAngle = GetFinalRotationAngle();
                double fovAngle = double.TryParse(FOVAngleTextBox.Text, out double value) ? value : 90.0;
                double resolution = _sliderResolution; // Get slider value

                _drawingEventHandler.Setup(
                    _drawingTools,
                    DrawingEventHandler.DrawingAction.DrawFilledRegion,
                    _selectedCameraPosition,
                    maxDistance,
                    rotationAngle,
                    fovAngle,
                    filledRegionType.Id,
                    resolution, // Pass resolution to the event
                    _selectedCameraElement, // Pass camera element for parameter update
                    double.TryParse(RotationAngleTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0 // Pass user rotation
                );

                _drawingEventHandler.DrawAngularDimension = (CheckboxIdentification.IsChecked == true);
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.UndoFilledRegion);
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while undoing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RotationAngleTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Auto-sync disabled. Value is now updated when "Joonista" (Draw) button is clicked.
        }

        private void Checkbox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            TriggerDetailLineUpdate();
        }
        // ------------------------------
        // SETTINGS WINDOW ACTIONS
        // ------------------------------
        private SettingsWindow _settingsWindow;

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Owner = this;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        }

        private void TriggerDetailLineUpdate()
        {
            if (_isProcessingUpdate) return;

            _isProcessingUpdate = true;

            try
            {
                if (_selectedCameraPosition == null)
                {
                    // Silently exit if no camera is selected
                    return;
                }

                double maxDistance = ParseMaxDistance();
                double rotationAngle = GetFinalRotationAngle();

                _drawingTools.SetParameters(_selectedCameraPosition, maxDistance, rotationAngle);

                if (_externalEvent != null)
                {
                    _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Update, _selectedCameraPosition, maxDistance, rotationAngle);
                    _externalEvent.Raise();
                }
            }
            finally
            {
                _isProcessingUpdate = false;
            }
        }
        public void CreateBoundaryLine()
        {
            try
            {
                _drawingEventHandler.SetupBoundaryLineCreation(_uiDoc);
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CreateFilledRegionTypes()
        {
            try
            {
                _drawingEventHandler.SetupCreateFilledRegions(_uiDoc);
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void TraceWalls()
        {
            try
            {
                _drawingEventHandler.SetupTraceWallsAndDrawBoundary(_uiDoc);
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UpdateSliderResolution(double value)
        {
            _sliderResolution = value;
        }

        public double GetSliderResolution()
        {
            return _sliderResolution;
        }
        private void DORIOption_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox currentCheckbox)
            {
                // Uncheck all other DORI checkboxes
                foreach (var checkbox in _doriRegionMapping.Keys)
                {
                    if (checkbox != currentCheckbox)
                    {
                        checkbox.IsChecked = false;
                    }
                }

                // Get the associated filled region name
                if (_doriRegionMapping.TryGetValue(currentCheckbox, out string regionName))
                {
                    // Find and select the corresponding filled region in the ComboBox
                    foreach (var item in FilledRegionComboBox.Items)
                    {
                        if (item.ToString().Contains(regionName))
                        {
                            FilledRegionComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }

        private void UpdateRotationAngle(double delta)
        {
            try
            {
                if (double.TryParse(RotationAngleTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double currentAngle))
                {
                    double newAngle = currentAngle + delta;
                    RotationAngleTextBox.Text = newAngle.ToString(CultureInfo.InvariantCulture);

                    if (_isInitialized && _selectedCameraPosition != null && _externalEvent != null)
                    {
                        double maxDistance = ParseMaxDistance();
                        double rotationAngle = GetFinalRotationAngle();

                        _drawingTools.SetParameters(_selectedCameraPosition, maxDistance, rotationAngle);
                        _drawingEventHandler.Setup(_drawingTools, DrawingEventHandler.DrawingAction.Update, _selectedCameraPosition, maxDistance, rotationAngle);
                        _externalEvent.Raise();
                    }
                }
                else
                {
                    MessageBox.Show("Invalid rotation angle. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while updating the rotation angle: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        public void NotifyFilledRegionsCreated()
        {
            Dispatcher.Invoke(() => PopulateFilledRegionComboBox());
        }

        private void PlusFourFiveDegree_Click(object sender, RoutedEventArgs e) => UpdateRotationAngle(45);
        private void PlusOneDegree_Click(object sender, RoutedEventArgs e) => UpdateRotationAngle(1);
        private void MinusOneDegree_Click(object sender, RoutedEventArgs e) => UpdateRotationAngle(-1);

        // Helper: read resolution from instance or type; returns false if not found/parsable
        private bool TryGetResolutionFromInstanceOrType(Element element, out int resolutionValue)
        {
            resolutionValue = 0;
            if (element == null) return false;

            // 1) Try instance parameter
            Parameter p = element.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);

            // 2) Try type via FamilyInstance.Symbol first (most reliable for family types)
            if (p == null && element is FamilyInstance fi && fi.Symbol != null)
            {
                p = fi.Symbol.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
            }

            // 3) Try generic ElementType lookup as a fallback
            if (p == null)
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    if (_doc.GetElement(typeId) is ElementType elementType)
                    {
                        p = elementType.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
                    }
                }
            }

            if (p == null) return false;

            // Prefer AsString/AsValueString when available
            string text = p.AsString()?.Trim();
            if (string.IsNullOrEmpty(text))
                text = p.AsValueString()?.Trim();

            // Clean any non-digit characters (e.g., "3840 px")
            if (!string.IsNullOrEmpty(text))
                text = new string(text.Where(char.IsDigit).ToArray());

            if (string.IsNullOrEmpty(text))
            {
                if (p.StorageType == StorageType.Integer)
                {
                    resolutionValue = p.AsInteger();
                    return resolutionValue > 0;
                }
                else if (p.StorageType == StorageType.Double)
                {
                    // Treat as unitless, round to nearest int
                    resolutionValue = (int)Math.Round(p.AsDouble());
                    return resolutionValue > 0;
                }
                else
                {
                    return false;
                }
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolutionValue);
        }

        // Helper: select resolution in ComboBox or fall back; robust across item types and ItemsSource
        private void SelectResolutionInComboOrFallback(int? value = null)
        {
            int target = value ?? SettingsManager.Settings.LastSelectedResolution;

            // Try to match across common item types
            foreach (var item in ResolutionComboBox.Items)
            {
                if (item is ComboBoxItem cbi)
                {
                    var content = (cbi.Content ?? "").ToString().Trim();
                    if (int.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v == target)
                    {
                        ResolutionComboBox.SelectedItem = cbi;
                        return;
                    }
                }
                else if (item is int vi && vi == target)
                {
                    ResolutionComboBox.SelectedItem = item;
                    return;
                }
                else if (item is string s && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vs) && vs == target)
                {
                    ResolutionComboBox.SelectedItem = item;
                    return;
                }
            }

            // If ItemsSource is set, don't add to Items; try selecting by text/value
            if (ResolutionComboBox.ItemsSource != null)
            {
                ResolutionComboBox.SelectedItem = target; // works if items are ints
                if (!Equals(ResolutionComboBox.SelectedItem, target))
                {
                    ResolutionComboBox.SelectedValue = target; // works if SelectedValuePath maps to the int
                    if (!Equals(ResolutionComboBox.SelectedValue, target))
                    {
                        ResolutionComboBox.Text = target.ToString(CultureInfo.InvariantCulture); // last resort
                    }
                }
            }
            else
            {
                // No ItemsSource: add missing value and select it
                var newItem = new ComboBoxItem { Content = target.ToString(CultureInfo.InvariantCulture) };
                ResolutionComboBox.Items.Add(newItem);
                ResolutionComboBox.SelectedItem = newItem;
            }

            // Keep setting in sync
            SettingsManager.Settings.LastSelectedResolution = target;
            SettingsManager.SaveSettings();
        }
    }
}
