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

        // Intentionally custom: derived from and validated against Axis Site Designer results, whose
        // distances it matches. Do not replace it with textbook lens geometry (see issue #2).
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

                if (!TryParseFov(FOVAngleTextBox.Text, out double fov))
                {
                    MaxDistanceTextBox.Text = "Invalid FOV";
                    return;
                }

                decimal? distance = GetSelectedDORIDistance(resolution, (decimal)fov);
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
                        ShowSelectedCamera(element);

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
                        SendPreviewUpdate();
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

                if (!double.TryParse(RotationAngleTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double userRotation))
                {
                    problems.Add(new MessageDialog.Item("Rotation",
                        $"“{RotationAngleTextBox.Text}” is not a number. Enter the rotation in degrees, for example 0 or -45."));
                }

                // (checkbox, region type name, pixels per foot, draws the angular dimension)
                var selectedLevels = new[]
                {
                    (Box: CheckboxDetection, TypeName: "dori_25px", Ppf: 7.62m, Dimension: false),
                    (Box: CheckboxObservation, TypeName: "dori_63px", Ppf: 19.2024m, Dimension: false),
                    (Box: CheckboxRecognition, TypeName: "dori_125px", Ppf: 38.1m, Dimension: false),
                    (Box: CheckboxIdentification, TypeName: "dori_250px", Ppf: 76.2m, Dimension: true)
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
                        Distance = (double)CalculateDORIDistance(resolution, (decimal)fovAngle, l.Ppf),
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
                    CheckboxIdentification.IsChecked == true));
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
                SendRequest(new DrawingRequest(DrawingEventHandler.DrawingAction.UndoFilledRegion, _drawingTools));
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
