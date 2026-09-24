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
using Camera_FOV.UI;

namespace Camera_FOV
{
    public partial class MainWindow : Window
    {
        private readonly WindowResizer _windowResizer;
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
        private string _selectedCameraState; // Camera state when selected (see CoverageSource)
        private CoverageStatus _coverageStatus; // Last reported coverage status of the selected camera
        private bool _applyConditionalOffset = false; // Flag for conditional 180 correction

        private DrawingEventHandler _drawingEventHandler;
        private Dictionary<CheckBox, string> _doriRegionMapping;

        private bool _isProcessingUpdate = false;
        private bool _isInitialized = false;

        private void InitializeDrawingTools()
        {
            if (_doc == null || _currentView == null)
            {
                MessageDialog.ShowError("Camera FOV couldn’t start", "The active project or view could not be read. Open a plan view and start Camera FOV again.");
                return;
            }

            if (_drawingTools == null)
                _drawingTools = new DrawingTools(_doc, _currentView);

            if (_drawingEventHandler == null)
            {
                _drawingEventHandler = new DrawingEventHandler();
                _drawingEventHandler.SetMainWindow(this);
            }

            // Created here, while the command runs: an external event can only be made in a Revit API context
            if (_revitActions == null)
                _revitActions = new RevitActionHandler();
        }

        // ------------------------------
        // CAMERA TYPES
        // ------------------------------
        private RevitActionHandler _revitActions;
        private CameraTypesWindow _cameraTypesWindow;
        private CameraType _cameraType; // The library camera type the selected camera's family type was made from

        private void CameraTypesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraTypesWindow != null && _cameraTypesWindow.IsLoaded)
            {
                _cameraTypesWindow.Activate();
                return;
            }

            ElementId family = (_selectedCameraElement as FamilyInstance)?.Symbol?.Family?.Id;
            _cameraTypesWindow = new CameraTypesWindow(_doc, _revitActions, family, RefreshCameraTypeLimits) { Owner = this };
            _cameraTypesWindow.Show();
        }

        // The selected camera's type may have been created or updated from the library
        private void RefreshCameraTypeLimits()
        {
            if (_selectedCameraElement != null && _selectedCameraElement.IsValidObject)
                ApplyCameraTypeLimits(_selectedCameraElement);
        }

        // A camera whose family type comes from the library can only be set within that camera type's
        // horizontal field of view; a fixed lens can't be changed at all.
        private void ApplyCameraTypeLimits(Element camera)
        {
            try { _cameraType = CameraTypeLink.GetLinked(camera); }
            catch (Exception) { _cameraType = null; }

            if (_cameraType == null)
            {
                FovLimitText.Visibility = System.Windows.Visibility.Collapsed;
                FOVAngleTextBox.IsReadOnly = false;
                FOVAngleTextBox.ToolTip = null;
                return;
            }

            string range = CameraType.FormatRange(_cameraType.HorizontalFovMin, _cameraType.HorizontalFovMax, "°");
            FOVAngleTextBox.IsReadOnly = !_cameraType.IsVarifocal;
            FOVAngleTextBox.ToolTip = _cameraType.IsVarifocal
                ? $"{_cameraType.Name} can be set from {range}"
                : $"{_cameraType.Name} has a fixed {range} lens";
            FovLimitText.Text = _cameraType.IsVarifocal
                ? $"{_cameraType.Name}: {range}"
                : $"{_cameraType.Name}: fixed lens, {range}";
            FovLimitText.SetResourceReference(ForegroundProperty, "Text.Tertiary");
            FovLimitText.Visibility = System.Windows.Visibility.Visible;

            if (!_cameraType.IsVarifocal)
                FOVAngleTextBox.Text = _cameraType.HorizontalFovMax.ToString("0.#", CultureInfo.InvariantCulture);
            else
                ClampFovToCameraType();

            if (_cameraType.HorizontalResolution > 0)
                SelectResolutionInComboOrFallback(_cameraType.HorizontalResolution);
        }

        // Pulls a field of view outside the camera type's range back to its nearest end
        private void ClampFovToCameraType()
        {
            if (_cameraType == null || !TryParseFov(FOVAngleTextBox.Text, out double fov) || _cameraType.AllowsHorizontalFov(fov)) return;

            double limited = _cameraType.ClampHorizontalFov(fov);
            FOVAngleTextBox.Text = limited.ToString("0.#", CultureInfo.InvariantCulture);
            FovLimitText.Text = $"{_cameraType.Name}: limited to {limited:0.#}°, its range is {CameraType.FormatRange(_cameraType.HorizontalFovMin, _cameraType.HorizontalFovMax, "°")}";
            FovLimitText.SetResourceReference(ForegroundProperty, "Status.Warning");
        }

        private void FOVAngleTextBox_LostFocus(object sender, RoutedEventArgs e) => ClampFovToCameraType();

        // ------------------------------
        // PURPOSE (issue #14)
        // ------------------------------
        private const string NotSet = "Not set";
        private bool _loadingPurpose;

        private void InitializePurpose()
        {
            PurposeCategoryCombo.ItemsSource = new[] { NotSet }.Concat(CameraData.Categories.Select(c => $"{c.Name} ({c.PixelsPerMeter:0} px/m)")).ToList();
            RiskGradeCombo.ItemsSource = new[] { NotSet }.Concat(CameraPurpose.RiskGrades).ToList();
            FOVAngleTextBox.TextChanged += (s, e) => UpdatePurposeReach();
            ResolutionComboBox.SelectionChanged += (s, e) => UpdatePurposeReach();
            LoadPurpose(null);
        }

        private void LoadPurpose(Element camera)
        {
            _loadingPurpose = true;
            try
            {
                var (category, grade) = camera != null ? CameraPurpose.Read(camera) : (null, null);
                PurposeCategoryCombo.SelectedIndex = category != null ? category.Index + 1 : 0;
                RiskGradeCombo.SelectedIndex = grade != null ? CameraPurpose.RiskGrades.ToList().IndexOf(grade) + 1 : 0;
            }
            catch (Exception)
            {
                PurposeCategoryCombo.SelectedIndex = RiskGradeCombo.SelectedIndex = 0;
            }
            finally
            {
                _loadingPurpose = false;
            }

            PurposeCategoryCombo.IsEnabled = RiskGradeCombo.IsEnabled = camera != null;
            UpdatePurposeReach();
        }

        private ObservationCategory SelectedPurposeCategory =>
            PurposeCategoryCombo.SelectedIndex > 0 ? CameraData.Categories[PurposeCategoryCombo.SelectedIndex - 1] : null;

        private string SelectedRiskGrade =>
            RiskGradeCombo.SelectedIndex > 0 ? CameraPurpose.RiskGrades[RiskGradeCombo.SelectedIndex - 1] : null;

        // Stores the choice on the camera straight away, like the rotation
        private void Purpose_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingPurpose || _selectedCameraElement == null || _revitActions == null) return;
            UpdatePurposeReach();

            Element camera = _selectedCameraElement;
            ObservationCategory category = SelectedPurposeCategory;
            string grade = SelectedRiskGrade;

            bool queued = _revitActions.Enqueue("save the camera’s purpose", app =>
            {
                if (!camera.IsValidObject) return;
                using (var transaction = new Transaction(camera.Document, "Camera purpose"))
                {
                    transaction.Start();
                    CameraPurpose.Write(camera, category, grade);
                    transaction.Commit();
                }
                UpdatePurposeReach();
            }, out string error);

            if (!queued)
                MessageDialog.ShowWarning("Revit didn’t accept the request", error, owner: this);
        }

        // Whether the camera, as set in the window, reaches its intended category
        private void UpdatePurposeReach()
        {
            if (PurposeReachText == null) return;
            PurposeReachText.SetResourceReference(ForegroundProperty, "Text.Tertiary");

            if (_selectedCameraElement == null || !_selectedCameraElement.IsValidObject)
            {
                PurposeReachText.Text = "Select a camera to set what it is for.";
                return;
            }

            string storage = CameraPurpose.HasParameters(_selectedCameraElement)
                ? string.Empty
                : $" Kept by the plugin; add text parameters “{SettingsManager.Settings.ParameterName_IntendedCategory}” and “{SettingsManager.Settings.ParameterName_RiskGrade}” to the family to show them in schedules.";

            ObservationCategory category = SelectedPurposeCategory;
            if (category == null)
            {
                PurposeReachText.Text = "Set the category this camera must reach to check it." + storage;
                return;
            }

            if (!TryGetSelectedResolution(out int resolution) || !TryParseFov(FOVAngleTextBox.Text, out double fov))
            {
                PurposeReachText.Text = "Enter the field of view and resolution to check the category.";
                return;
            }

            CameraMount mount = CameraMount.Read(_selectedCameraElement, fov);
            var reach = CameraPurpose.Reach(resolution, fov, mount, category);
            PurposeReachText.Text = reach.Text + PointCoverage.MountNote(mount, reach.ToMeters, category.Index >= 5) + storage;
            if (!reach.Met) PurposeReachText.SetResourceReference(ForegroundProperty, "Status.Warning");
        }

        // Hands the request to Revit. Failures of explicit actions are reported; a preview that
        // could not be sent is simply superseded by the next one.
        private void SendRequest(DrawingRequest request)
        {
            if (_drawingEventHandler == null || _drawingTools == null) return;

            if (!_drawingEventHandler.Enqueue(request, out string error) && !request.IsPreview)
            {
                MessageDialog.ShowWarning("Revit didn’t accept the request", error, owner: this);
            }
        }

        private void SendPreviewUpdate()
        {
            if (_drawingTools == null) return;

            SendRequest(new DrawingRequest(
                DrawingEventHandler.DrawingAction.Update,
                _drawingTools,
                position: _selectedCameraPosition,
                maxDistance: ParseMaxDistance(),
                rotationAngle: GetFinalRotationAngle()));
        }


        private double ParseMaxDistance()
        {
            if (CheckboxDetection.IsChecked == true ||
                CheckboxObservation.IsChecked == true ||
                CheckboxRecognition.IsChecked == true ||
                CheckboxIdentification.IsChecked == true)
            {
                // Invalid input is reported when the user draws, not on every keystroke of the preview
                return double.TryParse(MaxDistanceTextBox.Text.Replace(" m", ""), out double maxDistance) ? maxDistance : 0;
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
            SendPreviewUpdate();
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
                    if (_isInitialized && _selectedCameraPosition != null)
                    {
                        SendPreviewUpdate();
                    }
                }
                else
                {
                    userRotationAngle = 0;
                }
            };

            Topmost = true;
            this.Closed += MainWindow_Closed;
            this.Activated += (s, e) => RequestCoverageCheck(); // The camera may have changed in Revit meanwhile
            MessageDialog.DefaultOwner = this; // Messages from Revit-side actions centre on this window

            _windowResizer = new WindowResizer(this);
            this.MouseMove += Window_MouseMove;
            this.MouseLeftButtonUp += Window_MouseLeftButtonUp;

            if (Application.ResourceAssembly == null)
            {
                Application.ResourceAssembly = Assembly.GetExecutingAssembly();
            }

            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

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

            InitializeDoriRegionMapping();
            InitializePurpose();

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
                MessageDialog.ShowError("Couldn’t save your settings", "Your field of view, resolution and theme choices won’t be remembered next time.", ex);
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

                ThemeToggleButton.IsChecked = ThemeManager.IsDarkMode;
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t load your settings", "The window opened with default values instead.", ex, this);
            }
        }

                private void MainWindow_Closed(object sender, EventArgs e)
        {
            SaveSettings();

            if (MessageDialog.DefaultOwner == this)
                MessageDialog.DefaultOwner = null;

            // Removes the preview line after any actions still waiting, then stops accepting requests
            if (_drawingEventHandler != null && _drawingTools != null)
                _drawingEventHandler.Close(new DrawingRequest(DrawingEventHandler.DrawingAction.Delete, _drawingTools));
        }


        // ------------------------------
        // THEME MANAGEMENT
        // ------------------------------
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetDarkMode(ThemeToggleButton.IsChecked == true);
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
        // Accepts "93.5" and, for comma-decimal locales, "93,5". Valid range is (0, 360] degrees.
        private static bool TryParseFov(string text, out double fov)
        {
            text = text?.Trim();
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out fov)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out fov);
            return parsed && fov > 0 && fov <= 360;
        }

        private bool TryGetSelectedResolution(out int resolution)
        {
            resolution = 0;
            object selected = ResolutionComboBox.SelectedItem is ComboBoxItem item ? item.Content : ResolutionComboBox.SelectedItem;
            return selected != null
                && int.TryParse(selected.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out resolution)
                && resolution > 0;
        }

        // The distance in metres, to one decimal, at which the density falls to the level's px/m, with
        // the formula chosen in Settings (issue #11). The point check and the audit use the same relation.
        private static decimal CalculateDORIDistance(int resolution, double fov, DoriLevel level)
        {
            double meters = CameraData.DistanceMeters(resolution, fov, level.PixelsPerMeter, CameraData.CurrentFormula);
            return Math.Round((decimal)meters, 1);
        }
        private decimal? GetSelectedDORIDistance(int resolution, double fov)
        {
            if (CheckboxDetection.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, CameraData.Levels[0]);
            if (CheckboxObservation.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, CameraData.Levels[1]);
            if (CheckboxRecognition.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, CameraData.Levels[2]);
            if (CheckboxIdentification.IsChecked == true)
                return CalculateDORIDistance(resolution, fov, CameraData.Levels[3]);

            return null; // No checkbox selected
        }

        // Called after Settings changes the pixel density formula: new distances, and a new status
        // for the selected camera, whose coverage may have been drawn with the other formula
        public void RefreshDoriDistances()
        {
            UpdateMaxDistance();
            RequestCoverageCheck();
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

                if (!TryParseFov(FOVAngleTextBox.Text, out double fov))
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
                MessageDialog.ShowError("Couldn’t calculate the max distance", "Check the field of view and resolution values.", ex, this);
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
                    LoadCamera(element);
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
                // Pressing Esc is a deliberate choice, so no message
            }
            catch (Exception ex)
            {
                // Restore the plugin window without resizing Revit
                this.Show();
                this.Topmost = true;
                this.Activate();
                MessageDialog.ShowError("Couldn’t select the camera", "Something went wrong while reading the selected camera. Try selecting it again.", ex, this);
            }
        }


        // Reads the camera's position, orientation, rotation, FOV and resolution into the window.
        // Used when a camera is picked and when its coverage is updated from the model.
        private bool LoadCamera(Element element)
        {
            if (!(element?.Location is LocationPoint locationPoint)) return false;

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
            ShowSelectedCamera(element);

            // Display only the user rotation in UI (base rotation is applied silently)
            RotationAngleTextBox.Text = userRotation.ToString("F0", CultureInfo.InvariantCulture);

            // Start from where the camera's 2D symbol actually points, so the direction line lies along
            // it whatever the family's orientation or flips. The rotation typed in the window then turns
            // the coverage from there. The orientation-based guess above is kept only as a fallback for
            // symbols without a clear direction.
            double? symbolAngle = CameraSymbol.GetAngleDegrees(element, _currentView);
            if (symbolAngle.HasValue)
            {
                double shownRotation = double.TryParse(RotationAngleTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double shown) ? shown : 0;
                _applyConditionalOffset = false;
                _baseCameraRotation = symbolAngle.Value - shownRotation - presetRotationAngle;
            }

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

            // A camera type from the library limits the field of view (and sets the resolution)
            ApplyCameraTypeLimits(element);
            LoadPurpose(element);

            // Show the direction line for the loaded camera straight away
            SendPreviewUpdate();

            // What the camera looks like now, recorded with the coverage drawn for it (issue #9)
            _selectedCameraState = CoverageSource.CaptureCameraState(element);
            RequestCoverageCheck();
            return true;
        }

        // ------------------------------
        // COVERAGE STATUS (issue #9)
        // ------------------------------
        private void RequestCoverageCheck()
        {
            if (_selectedCameraElement == null || !_selectedCameraElement.IsValidObject) return;
            SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CheckCoverage, _drawingTools, _selectedCameraElement));
        }

        // Called by the handler on the Revit thread with the selected camera's coverage status.
        public void ShowCoverageStatus(CoverageStatus status)
        {
            _coverageStatus = status;

            if (status == null || status.State == CoverageState.None)
            {
                CoverageStatusRow.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            switch (status.State)
            {
                case CoverageState.Current:
                    CoverageStatusIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.CheckCircleOutline;
                    CoverageStatusIcon.SetResourceReference(ForegroundProperty, "Status.Success");
                    break;
                case CoverageState.Stale:
                    CoverageStatusIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.AlertOutline;
                    CoverageStatusIcon.SetResourceReference(ForegroundProperty, "Status.Warning");
                    break;
                default:
                    CoverageStatusIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.InformationOutline;
                    CoverageStatusIcon.SetResourceReference(ForegroundProperty, "Accent.Text");
                    break;
            }

            CoverageStatusText.Text = DrawingEventHandler.DescribeState(status);
            CoverageStatusText.ToolTip = CoverageStatusText.Text;
            UpdateCoverageButton.Visibility = status.State == CoverageState.Current
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
            CoverageStatusRow.Visibility = System.Windows.Visibility.Visible;
        }

        // Redraws the selected camera's coverage from the camera as it is now in the model, for the
        // same DORI levels it already has. Rotation and FOV typed in the window are replaced by the
        // camera's values, because updating means matching the model.
        private void UpdateCoverageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCameraElement == null || !_selectedCameraElement.IsValidObject)
            {
                MessageDialog.ShowWarning("No camera to update", "Select the camera again and press Update.", owner: this);
                return;
            }

            if (!LoadCamera(_selectedCameraElement))
            {
                MessageDialog.ShowWarning("Can’t read the camera", "The camera has no location point, so its coverage can’t be redrawn.", owner: this);
                return;
            }

            // Tick exactly the DORI levels the existing coverage has
            if (_coverageStatus != null && _coverageStatus.RegionTypeIds.Any())
            {
                var existingNames = _coverageStatus.RegionTypeIds
                    .Select(id => _doc.GetElement(id)?.Name ?? string.Empty)
                    .ToList();

                foreach (var pair in _doriRegionMapping)
                    pair.Key.IsChecked = existingNames.Any(name => name.Contains(pair.Value));
            }

            FilledRegionButton_Click(sender, e);
        }

        private void CheckViewCoverageButton_Click(object sender, RoutedEventArgs e)
        {
            SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CheckViewCoverage, _drawingTools));
        }

        private void CoverageAuditButton_Click(object sender, RoutedEventArgs e)
        {
            SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CoverageAudit, _drawingTools));
        }

        // Pick a spot in the plan and list which cameras see it, and how well (issue #7)
        private void CheckPointButton_Click(object sender, RoutedEventArgs e)
        {
            XYZ point = null;
            try
            {
                this.Hide();
                this.Topmost = false;

                IntPtr revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                    SetForegroundWindow(revitHandle);

                point = _uiDoc.Selection.PickPoint("Pick a point to check which cameras cover it");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Esc: nothing to check
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                MessageDialog.ShowWarning(
                    "Can’t pick a point here",
                    "Revit needs a work plane to pick a point. Open a floor plan, or set a work plane for this view, then try again.");
            }
            finally
            {
                this.Show();
                this.Topmost = true;
                this.Activate();
            }

            if (point != null)
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CheckPoint, _drawingTools, position: point));
        }

        // Shows which camera the panel is working on, so the current state is always visible.
        private void ShowSelectedCamera(Element camera)
        {
            string typeName = _doc.GetElement(camera.GetTypeId())?.Name;
            CameraStatusTitle.Text = string.IsNullOrWhiteSpace(typeName) ? camera.Name : typeName;
            CameraStatusTitle.ToolTip = CameraStatusTitle.Text;
            string familyName = (camera as FamilyInstance)?.Symbol?.FamilyName;
            CameraStatusDetail.Text = familyName ?? string.Empty;
            CameraStatusDetail.Visibility = string.IsNullOrWhiteSpace(familyName) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            CameraStatusIcon.SetResourceReference(ForegroundProperty, "Accent.Text");
            SelectElementsButton.Content = "Change";
        }

        private void FilledRegionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check every input first and report all problems together, so one fix-up round is enough
                var problems = new List<MessageDialog.Item>();

                if (_selectedCameraPosition == null)
                {
                    problems.Add(new MessageDialog.Item("Camera",
                        "No camera is selected. Click Select and pick a camera (security device) in the view."));
                }

                if (!TryGetSelectedResolution(out int resolution))
                {
                    problems.Add(new MessageDialog.Item("Horizontal resolution",
                        "Choose the camera's horizontal resolution in pixels from the list, for example 1920 or 3840."));
                }

                if (!TryParseFov(FOVAngleTextBox.Text, out double fovAngle))
                {
                    problems.Add(new MessageDialog.Item("Field of view",
                        $"“{FOVAngleTextBox.Text}” is not a valid angle. Enter the horizontal field of view in degrees, above 0 and up to 360 (for example 93)."));
                }
                else if (_cameraType != null && !_cameraType.AllowsHorizontalFov(fovAngle))
                {
                    problems.Add(new MessageDialog.Item("Field of view",
                        $"{fovAngle:0.#}° is outside what {_cameraType.Name} can do ({CameraType.FormatRange(_cameraType.HorizontalFovMin, _cameraType.HorizontalFovMax, "°")})."));
                }

                if (!double.TryParse(RotationAngleTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double userRotation))
                {
                    problems.Add(new MessageDialog.Item("Rotation",
                        $"“{RotationAngleTextBox.Text}” is not a number. Enter the rotation in degrees, for example 0 or -45."));
                }

                // (checkbox, region type name, DORI level, draws the angular dimension)
                var selectedLevels = new[]
                {
                    (Box: CheckboxDetection, TypeName: "dori_25px", Level: CameraData.Levels[0], Dimension: false),
                    (Box: CheckboxObservation, TypeName: "dori_63px", Level: CameraData.Levels[1], Dimension: false),
                    (Box: CheckboxRecognition, TypeName: "dori_125px", Level: CameraData.Levels[2], Dimension: false),
                    (Box: CheckboxIdentification, TypeName: "dori_250px", Level: CameraData.Levels[3], Dimension: true)
                }.Where(l => l.Box.IsChecked == true).ToList();

                if (selectedLevels.Count == 0)
                {
                    problems.Add(new MessageDialog.Item("DORI coverage",
                        "Tick at least one level: Detection, Observation, Recognition or Identification."));
                }

                // Function to get FilledRegionType ID by partial name
                ElementId GetTypeId(string namePart)
                {
                    var type = new FilteredElementCollector(_doc)
                        .OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>()
                        .FirstOrDefault(x => x.Name.Contains(namePart));
                    return type?.Id;
                }

                List<string> missingTypes = selectedLevels.Where(l => GetTypeId(l.TypeName) == null).Select(l => l.TypeName).ToList();
                if (missingTypes.Any())
                {
                    problems.Add(new MessageDialog.Item("Region types",
                        $"This project has no filled region type for {string.Join(", ", missingTypes)}. Open Settings and click Create DORI region types."));
                }

                if (problems.Any())
                {
                    MessageDialog.ShowWarning(
                        "Can’t draw the coverage yet",
                        problems.Count == 1 ? "Fix this and press Draw coverage again." : "Fix these and press Draw coverage again.",
                        problems,
                        this);
                    return;
                }

                var doriLayers = selectedLevels
                    .Select(l => new DoriLayerConfig
                    {
                        Distance = (double)CalculateDORIDistance(resolution, fovAngle, l.Level),
                        TypeId = GetTypeId(l.TypeName),
                        DrawDimension = l.Dimension
                    })
                    .ToList();

                // Sort layers by Distance Descending (Largest to Smallest)
                // This ensures Detection (Large) is drawn first (at bottom), Identification (Small) last (top).
                doriLayers = doriLayers.OrderByDescending(x => x.Distance).ToList();

                double rotationAngle = GetFinalRotationAngle();
                // Pass the implementation list 

                SendRequest(new DrawingRequest(
                    DrawingEventHandler.DrawingAction.DrawFilledRegion,
                    _drawingTools,
                    _selectedCameraElement,
                    _selectedCameraPosition,
                    doriLayers.First().Distance, // Max distance for legacy use
                    rotationAngle,
                    fovAngle,
                    doriLayers.First().TypeId, // Legacy type
                    _sliderResolution, // Corrected: Use slider value (degrees), not camera pixels
                    userRotation,
                    doriLayers,
                    CheckboxIdentification.IsChecked == true,
                    _selectedCameraState,
                    cameraResolution: resolution));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t draw the coverage", "Something went wrong while preparing the drawing, so nothing was changed.", ex, this);
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.UndoFilledRegion, _drawingTools, _selectedCameraElement));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t undo the coverage", "The undo request couldn’t be sent to Revit.", ex, this);
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

                SendPreviewUpdate();
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
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CreateBoundaryLine, _drawingTools));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t create the Boundary line style", "The request couldn’t be sent to Revit.", ex);
            }
        }

        public void CreateFilledRegionTypes()
        {
            try
            {
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.CreateFilledRegions, _drawingTools));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t create the DORI region types", "The request couldn’t be sent to Revit.", ex);
            }
        }

        public void TraceWalls()
        {
            try
            {
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.TraceWallsAndDrawBoundary, _drawingTools));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t start tracing", "The trace request couldn’t be sent to Revit.", ex);
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

        public TracingRules GetTracingRules()
        {
            return ElementTagStorage.LoadTracingRules(_doc);
        }

        // Stored in the project, so it goes through Revit like any other change
        public void SaveTracingRules(TracingRules rules)
        {
            SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.SaveTracingRules, _drawingTools, tracingRules: rules));
        }

        public List<string> GetAngularDimensionTypeNames()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Angular)
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }
        private void DORIOption_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox currentCheckbox)
            {
                // Multi-selection is supported now.
                // ComboBox synchronization logic removed as ComboBox is deleted.
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

                    if (_isInitialized && _selectedCameraPosition != null)
                    {
                        SendPreviewUpdate();
                    }
                }
                else
                {
                    MessageDialog.ShowWarning("Rotation isn’t a number", $"“{RotationAngleTextBox.Text}” can’t be nudged. Enter the rotation in degrees, for example 0 or -45, then use the buttons again.", owner: this);
                }
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Couldn’t rotate the camera", "The preview couldn’t be updated with the new angle.", ex, this);
            }
        }
        public void NotifyFilledRegionsCreated()
        {
            // FilledRegionComboBox logic removed.
        }

        // Flips the sign of the angle as typed; changing the text updates the preview like typing does.
        private void ToggleRotationSign_Click(object sender, RoutedEventArgs e)
        {
            string text = RotationAngleTextBox.Text.Trim();

            if (text.StartsWith("-") || text.StartsWith("−"))
                RotationAngleTextBox.Text = text.Substring(1);
            else if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double value) && value != 0)
                RotationAngleTextBox.Text = "-" + text;
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
