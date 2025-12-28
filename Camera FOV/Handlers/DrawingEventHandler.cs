using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Camera_FOV.Utils;
using Camera_FOV.Utils;
using Camera_FOV;
using Camera_FOV.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Camera_FOV.UI; // Add UI namespace for LinkedModelsSelectionWindow
using System.Windows.Threading;

namespace Camera_FOV.Handlers
{
    public class DoriLayerConfig
    {
        public double Distance { get; set; }
        public ElementId TypeId { get; set; }
        public bool DrawDimension { get; set; }
    }

    public class DrawingEventHandler : IExternalEventHandler
{
    public enum DrawingAction
    {
        None,
        Draw,
        Update,
        Delete,
        DrawFilledRegion,
        UpdateFilledRegion,
        DeleteFilledRegion,
        UndoFilledRegion,
        UpdateCameraParameter,
        CreateBoundaryLine,
        CreateFilledRegions,
        TraceWallsAndDrawBoundary
    }

    private DrawingAction _currentAction = DrawingAction.None;
    private DrawingTools _drawingTools;
    private XYZ _position;
    private double _maxDistance;
    private double _rotationAngle;
    private double _fovAngle;
    private ElementId _filledRegionTypeId;
    private double _sliderResolution;
    private UIDocument _uiDoc; // Add a field for the UIDocument
    private MainWindow _mainWindow;
    private string _newLineStyleName; // Add this to store the new line style name
    private Element _cameraElement; // For parameter updates
    private double _parameterValue; // For parameter updates
    private Dictionary<string, ElementId> _cameraToFilledRegionMap = new Dictionary<string, ElementId>(); // Track regions per camera+type
    private List<ElementId> _lastBatchCreatedIds = new List<ElementId>(); // Undo batch tracker
    public bool DrawAngularDimension { get; set; } = false;
    private List<DoriLayerConfig> _doriLayers;

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }
    public void SetupBoundaryLineCreation(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.CreateBoundaryLine;
    }
    public void SetupCreateFilledRegions(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.CreateFilledRegions;
    }
    public void SetupTraceWallsAndDrawBoundary(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.TraceWallsAndDrawBoundary;
    }
    public void SetupChangeBoundaryLineType(UIDocument uiDoc, string newLineStyleName)
    {
        _uiDoc = uiDoc;
        _newLineStyleName = newLineStyleName; // Store the new line style name
    }
    public void Setup(
        DrawingTools drawingTools,
        DrawingAction action,
        XYZ position = null,
        double maxDistance = 0,
        double rotationAngle = 0,
        double fovAngle = 90,
        ElementId filledRegionTypeId = null,
        double sliderResolution = 1.0,
        Element cameraElement = null,
        double userRotationForParameter = 0,
        List<DoriLayerConfig> doriLayers = null)
    {
        _drawingTools = drawingTools;
        _currentAction = action;
        _position = position;
        _maxDistance = maxDistance;
        _rotationAngle = rotationAngle;
        _fovAngle = fovAngle;
        _filledRegionTypeId = filledRegionTypeId;
        _sliderResolution = sliderResolution;
        _cameraElement = cameraElement;
        _parameterValue = userRotationForParameter;
        _doriLayers = doriLayers;
    }

    public void SetupCameraParameterUpdate(Element cameraElement, double rotationValue)
    {
        _cameraElement = cameraElement;
        _parameterValue = rotationValue;
        _currentAction = DrawingAction.UpdateCameraParameter;
    }

    public void Execute(UIApplication app)
    {
        try
        {
            switch (_currentAction)
            {
                case DrawingAction.CreateBoundaryLine:
                    CreateBoundaryLine();
                    break;

                case DrawingAction.Draw:
                    _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle);
                    _drawingTools.DrawDetailLine();
                    break;

                case DrawingAction.Update:
                    _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle);
                    _drawingTools.UpdateDetailLine();
                    break;

                case DrawingAction.Delete:
                    _drawingTools.DeleteDetailLine();
                    break;

                case DrawingAction.DrawFilledRegion:
                    // Update camera parameter if element is provided
                    if (_cameraElement != null)
                    {
                        UpdateCameraParameter();
                    }

                    if (_doriLayers != null && _doriLayers.Any())
                    {
                        _lastBatchCreatedIds.Clear();
                        foreach (var layer in _doriLayers)
                        {
                            ElementId id = DrawLayer(layer.Distance, layer.TypeId, layer.DrawDimension);
                            if (id != ElementId.InvalidElementId) _lastBatchCreatedIds.Add(id);
                        }
                    }
                    else
                    {
                        // Fallback single mode
                        ElementId id = DrawLayer(_maxDistance, _filledRegionTypeId, DrawAngularDimension);
                        _lastBatchCreatedIds.Clear();
                        if (id != ElementId.InvalidElementId) _lastBatchCreatedIds.Add(id);
                    }
                    break;


                case DrawingAction.DeleteFilledRegion:
                    _drawingTools.DeleteFilledRegion();
                    break;

                case DrawingAction.UndoFilledRegion:
                     if (_lastBatchCreatedIds != null && _lastBatchCreatedIds.Any())
                    {
                        foreach (var id in _lastBatchCreatedIds)
                        {
                            _drawingTools.DeleteElement(id);
                        }
                        _lastBatchCreatedIds.Clear();
                    }
                    else
                    {
                        // Fallback to old behavior if list is empty (e.g. legacy or restart)
                        _drawingTools.DeleteFilledRegion();
                    }
                    break;

                case DrawingAction.UpdateCameraParameter:
                    UpdateCameraParameter();
                    break;

                case DrawingAction.CreateFilledRegions:
                    CreateFilledRegions();
                    _mainWindow?.NotifyFilledRegionsCreated(); // Safely invoke if _mainWindow is set
                    break;

                case DrawingAction.TraceWallsAndDrawBoundary:
                    TraceWallsAndDrawBoundary();
                    break;

                default:
                    TaskDialog.Show("Info", "No valid action was set up.");
                    break;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred during {_currentAction}: {ex.Message}");
        }
        finally
        {
            _currentAction = DrawingAction.None; // Reset action
        }
    }
    private void CreateBoundaryLine()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        try
        {
            using (Transaction transaction = new Transaction(doc, "Create Boundary Line"))
            {
                transaction.Start();

                // Access the Lines category
                Categories categories = doc.Settings.Categories;
                Category linesCategory = categories.get_Item(BuiltInCategory.OST_Lines);

                // Check if the "Boundary" subcategory already exists
                Category boundaryCategory = null;
                foreach (Category subCategory in linesCategory.SubCategories)
                {
                    if (subCategory.Name == "Boundary")
                    {
                        boundaryCategory = subCategory;
                        break;
                    }
                }

                if (boundaryCategory != null)
                {
                    MessageBox.Show("Boundary line style already exists.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    transaction.RollBack(); // Rollback since nothing is being changed
                    return;
                }

                // Create a new subcategory for "Boundary"
                boundaryCategory = categories.NewSubcategory(linesCategory, "Boundary");

                if (boundaryCategory != null)
                {
                    // Set the properties for the Boundary line style
                    boundaryCategory.LineColor = new Color(0, 255, 0); // Green color
                    boundaryCategory.SetLineWeight(1, GraphicsStyleType.Projection); // Line weight 1

                    // Assign the "Solid" line pattern
                    LinePatternElement solidPattern = new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .Cast<LinePatternElement>()
                        .FirstOrDefault(lp => lp.Name.Equals("Solid"));

                    if (solidPattern != null)
                    {
                        boundaryCategory.SetLinePatternId(solidPattern.Id, GraphicsStyleType.Projection);
                    }
                    else
                    {
                        TaskDialog.Show("Warning", "Solid line pattern not found.");
                    }

                    MessageBox.Show("Boundary line style created successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    TaskDialog.Show("Error", "Failed to create the Boundary line style.");
                }

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred during CreateBoundaryLine:\n{ex.Message}\n{ex.StackTrace}");
        }
    }
    private void CreateFilledRegions()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        var filledRegionData = new Dictionary<string, Autodesk.Revit.DB.Color>
    {
        { "dori_25px", new Autodesk.Revit.DB.Color(255, 213, 213) }, // Red
        { "dori_63px", new Autodesk.Revit.DB.Color(255, 252, 232) }, // Yellow
        { "dori_125px", new Autodesk.Revit.DB.Color(223, 239, 255) }, // Blue
        { "dori_250px", new Autodesk.Revit.DB.Color(226, 252, 231) } // Green
    };

        try
        {
            using (Transaction transaction = new Transaction(doc, "Create Filled Region Types"))
            {
                transaction.Start();

                // Get the solid fill pattern
                FillPatternElement solidFillPattern = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                if (solidFillPattern == null)
                {
                    TaskDialog.Show("Error", "Solid fill pattern not found. Cannot create filled region types.");
                    transaction.RollBack();
                    return;
                }

                // Retrieve all existing filled region types
                var existingFilledRegions = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .ToDictionary(r => r.Name, r => r);

                List<string> existingRegions = new List<string>();
                List<string> createdRegions = new List<string>();

                // Create or check each filled region type
                foreach (var entry in filledRegionData)
                {
                    string regionName = entry.Key;
                    Autodesk.Revit.DB.Color regionColor = entry.Value;

                    // Check if the filled region type already exists
                    if (existingFilledRegions.ContainsKey(regionName))
                    {
                        existingRegions.Add(regionName);
                        continue; // Skip if it already exists
                    }

                    // Duplicate an existing filled region type
                    var defaultRegionType = existingFilledRegions.Values.FirstOrDefault();

                    if (defaultRegionType == null)
                    {
                        TaskDialog.Show("Error", "No default filled region type found. Cannot create new types.");
                        transaction.RollBack();
                        return;
                    }

                    FilledRegionType newRegionType = defaultRegionType.Duplicate(regionName) as FilledRegionType;

                    if (newRegionType != null)
                    {
                        newRegionType.ForegroundPatternId = solidFillPattern.Id;
                        newRegionType.ForegroundPatternColor = regionColor;
                        newRegionType.IsMasking = false;
                        createdRegions.Add(regionName);
                    }
                }

                transaction.Commit();

                if (existingRegions.Any())
                {
                    MessageBox.Show("Filled regions already exist!", "Region types exist", MessageBoxButton.OK, MessageBoxImage.Information);

                }

                if (createdRegions.Any())
                {
                    MessageBox.Show("Filled regions created sucessfuly", "Success!", MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred while creating filled region types:\n{ex.Message}\n{ex.StackTrace}");
        }
    }
    private void TraceWallsAndDrawBoundary()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        try
        {
            using (Transaction transaction = new Transaction(doc, "Trace Walls and Columns and Draw Boundary"))
            {
                transaction.Start();

                // Define categories to trace
                List<BuiltInCategory> categories = new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_Walls,
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_Columns,
                    BuiltInCategory.OST_Doors,
                    BuiltInCategory.OST_Windows,
                    BuiltInCategory.OST_CurtainWallPanels,
                    BuiltInCategory.OST_CurtainWallMullions
                };

                ElementMulticategoryFilter filter = new ElementMulticategoryFilter(categories);

                // Collect elements visible in the current view
                var elements = new FilteredElementCollector(doc, _uiDoc.ActiveView.Id)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .ToList();

                // Check for Linked Models
                var linkInstances = new FilteredElementCollector(doc, _uiDoc.ActiveView.Id)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                List<RevitLinkInstance> selectedLinks = new List<RevitLinkInstance>();

                if (linkInstances.Any())
                {
                    // Show selection window
                    LinkedModelsSelectionWindow window = new LinkedModelsSelectionWindow(linkInstances.Select(l => l.Name).ToList());
                    bool? result = window.ShowDialog();

                    if (window.Result == LinkedModelsSelectionWindow.SelectionResult.All)
                    {
                        selectedLinks.AddRange(linkInstances);
                    }
                    else if (window.Result == LinkedModelsSelectionWindow.SelectionResult.Selected)
                    {
                        var selectedNames = window.SelectedLinks.Select(l => l.Name).ToHashSet();
                        selectedLinks.AddRange(linkInstances.Where(l => selectedNames.Contains(l.Name)));
                    }
                }

                if (!elements.Any() && !selectedLinks.Any())
                {
                    TaskDialog.Show("Info", "No walls, structural columns, or selected linked models found.");
                    transaction.RollBack();
                    return;
                }

                // Get the "Boundary" line style (Subcategory of Lines)
                Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                Category boundarySubCategory = linesCategory.SubCategories.get_Item("Boundary");
                GraphicsStyle boundaryLineStyle = boundarySubCategory?.GetGraphicsStyle(GraphicsStyleType.Projection);

                if (boundaryLineStyle == null)
                {
                    TaskDialog.Show("Error", "Line style 'Boundary' not found. Please create it first.");
                    transaction.RollBack();
                    return;
                }

                View view = _uiDoc.ActiveView;

                HashSet<string> drawnCurveHashes = new HashSet<string>();

                // Process Local Elements
                foreach (var element in elements)
                {
                    ProcessElementGeometry(element, view, boundaryLineStyle, doc, drawnCurveHashes);
                }

                // Process Linked Elements
                foreach (var link in selectedLinks)
                {
                   ProcessLinkedModel(link, view, boundaryLineStyle, doc, drawnCurveHashes);
                }

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred while tracing walls and columns:\n{ex.Message}");
        }
    }

    // Process elements from a linked Revit/IFC model
    private void ProcessLinkedModel(RevitLinkInstance link, View hostView, GraphicsStyle boundaryLineStyle, Document hostDoc, HashSet<string> drawnCurveHashes)
    {
        Document linkedDoc = link.GetLinkDocument();
        if (linkedDoc == null)
        {
            System.Diagnostics.Debug.WriteLine($"Link document is null for: {link.Name}");
            return;
        }

        // Get the transform to convert from linked doc coordinates to host doc coordinates
        Transform linkTransform = link.GetTotalTransform();

        // Define categories to trace in the linked model
        List<BuiltInCategory> categories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_CurtainWallPanels,
            BuiltInCategory.OST_CurtainWallMullions
        };

        ElementMulticategoryFilter filter = new ElementMulticategoryFilter(categories);

        // Collect elements from the linked document
        var linkedElements = new FilteredElementCollector(linkedDoc)
            .WherePasses(filter)
            .WhereElementIsNotElementType()
            .ToList();

        foreach (var element in linkedElements)
        {
            ProcessLinkedElementGeometry(element, linkTransform, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes);
        }
    }

    // Process geometry from a linked element, applying the link's transform
    private void ProcessLinkedElementGeometry(Element linkedElement, Transform linkTransform, View hostView, GraphicsStyle boundaryLineStyle, Document hostDoc, HashSet<string> drawnCurveHashes)
    {
        // Get geometry from the linked element (without View context since it's a different document)
        Options geomOptions = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        GeometryElement geometry = linkedElement.get_Geometry(geomOptions);

        if (geometry == null) return;

        // Transform the geometry to host coordinates
        GeometryElement transformedGeometry = geometry.GetTransformed(linkTransform);

        ProcessGeometryRecursive(transformedGeometry, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes, true);
    }

    // Unified method to process any element's geometry in the context of the view
    private void ProcessElementGeometry(Element element, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes)
    {
        // Get geometry specifically for this view (handles cuts, visibility, detail level)
        // Note: DetailLevel cannot be set when View is provided
        Options geomOptions = new Options { View = view, ComputeReferences = true };
        GeometryElement geometry = element.get_Geometry(geomOptions);

        if (geometry == null) return;

        if (geometry == null) return;

        ProcessGeometryRecursive(geometry, view, boundaryLineStyle, doc, drawnCurveHashes, element is RevitLinkInstance);
    }

    private void ProcessGeometryRecursive(GeometryElement geometry, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, bool isLink)
    {
        foreach (GeometryObject geomObj in geometry)
        {
            // Filter by Category if it's a Link (to avoid tracing furniture etc.)
            if (isLink && !IsValidCategory(geomObj, doc))
            {
               // If it's a GeometryInstance, we continue because the Category might be on the children
               if (!(geomObj is GeometryInstance)) 
                   continue;
            }

            if (geomObj is Solid solid)
            {
                // Process all edges of the solid
                foreach (Edge edge in solid.Edges)
                {
                    ProcessCurve(edge.AsCurve(), view, boundaryLineStyle, doc, drawnCurveHashes);
                }
            }
            else if (geomObj is Curve curve)
            {
                // Skip wall centerlines (they appear as standalone curves)
                if (isLink && IsCenterline(geomObj))
                    continue;
                    
                // Process standalone curves
                ProcessCurve(curve, view, boundaryLineStyle, doc, drawnCurveHashes);
            }
            else if (geomObj is GeometryInstance instance)
            {
                // Recursively process geometry instances
                // For Links, this is where we dive into the link content
                ProcessGeometryRecursive(instance.GetInstanceGeometry(), view, boundaryLineStyle, doc, drawnCurveHashes, isLink);
            }
        }
    }

    private bool IsValidCategory(GeometryObject geomObj, Document doc)
    {
        ElementId gsId = geomObj.GraphicsStyleId;
        if (gsId == ElementId.InvalidElementId) return true; // Default to true if no style (safe fallback)

        GraphicsStyle gs = doc.GetElement(gsId) as GraphicsStyle;
        
        // If we can't resolve the style (e.g., from a linked doc), allow it
        // This is common for linked models where IDs don't match the host
        if (gs == null) return true;
        
        if (gs.GraphicsStyleCategory != null)
        {
             // Check Parent Category (e.g. "Walls", "Doors")
             // Note: Subcategories (like "Cut") are children of the Main Category
             Category cat = gs.GraphicsStyleCategory;
             
             // Traverse up to find main category
             while (cat.Parent != null)
             {
                 cat = cat.Parent;
             }

             BuiltInCategory bic = (BuiltInCategory)(int)cat.Id.Value;
             return IsSupportedCategory(bic);
        }
        return true;
    }

    private bool IsSupportedCategory(BuiltInCategory bic)
    {
        return bic == BuiltInCategory.OST_Walls ||
               bic == BuiltInCategory.OST_StructuralColumns ||
               bic == BuiltInCategory.OST_Columns ||
               bic == BuiltInCategory.OST_Doors ||
               bic == BuiltInCategory.OST_Windows ||
               bic == BuiltInCategory.OST_CurtainWallPanels ||
               bic == BuiltInCategory.OST_CurtainWallMullions;
    }

    // Check if a geometry object represents a centerline (to be filtered out)
    private bool IsCenterline(GeometryObject geomObj)
    {
        // Centerlines are typically curves, not solids
        // For linked geometry, we skip ALL standalone curves since centerlines appear this way
        // The actual boundary geometry comes from Solid edges, not standalone curves
        return geomObj is Curve;
    }

    private void ProcessCurve(Curve curve, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes)
    {
        if (curve == null) return;

        try
        {
            Curve projectedCurve = ProjectCurveToPlane(curve, view.SketchPlane.GetPlane(), doc);

            if (projectedCurve != null)
            {
                // Generate hash from PROJECTED curve to deduplicate 2D lines
                string hash = GenerateCurveHash(projectedCurve);

                if (!drawnCurveHashes.Contains(hash))
                {
                    drawnCurveHashes.Add(hash);

                    DetailCurve detailCurve = doc.Create.NewDetailCurve(view, projectedCurve);
                    detailCurve.LineStyle = boundaryLineStyle;
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore projection errors for individual curves (e.g. degenerate curves)
             System.Diagnostics.Debug.WriteLine($"Failed to process curve: {ex.Message}");
        }
    }

    // Helper method to generate a hash for a curve based on its start and end points
    private string GenerateCurveHash(Curve curve)
    {
        XYZ p1 = curve.GetEndPoint(0);
        XYZ p2 = curve.GetEndPoint(1);

        // Format points with precision to avoid floating point issues
        string s1 = $"{Math.Round(p1.X, 4)},{Math.Round(p1.Y, 4)},{Math.Round(p1.Z, 4)}";
        string s2 = $"{Math.Round(p2.X, 4)},{Math.Round(p2.Y, 4)},{Math.Round(p2.Z, 4)}";

        // Sort to ensure direction invariance
        if (string.Compare(s1, s2) < 0)
            return $"{s1}|{s2}";
        else
            return $"{s2}|{s1}";
    }

    private Curve ProjectCurveToPlane(Curve curve, Plane plane, Document doc)
    {
        try
        {
            // Use Revit-defined short curve tolerance
            double shortCurveTolerance = doc.Application.ShortCurveTolerance*2;

            // Project each endpoint of the curve onto the plane
            XYZ start = ProjectPointToPlane(curve.GetEndPoint(0), plane);
            XYZ end = ProjectPointToPlane(curve.GetEndPoint(1), plane);

            // Validate distance between projected points
            double distance = start.DistanceTo(end);
            if (distance < shortCurveTolerance)
            {
                return null;
            }

            // Create a new line or arc based on curve type
            if (curve is Line)
            {
                return Line.CreateBound(start, end);
            }
            else if (curve is Arc arc)
            {
                XYZ mid = ProjectPointToPlane(arc.Evaluate(0.5, true), plane);
                return Arc.Create(start, end, mid);
            }
            else
            {
                TaskDialog.Show("Error", "Unsupported curve type for projection.");
                return null;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"Error during projection: {ex.Message}");
            return null;
        }
    }
    private bool IsCurvePlanarToSketchPlane(Curve curve, Plane plane)
    {
        if (curve == null || plane == null)
            return false;

        // Check each endpoint of the curve
        XYZ start = curve.GetEndPoint(0);
        XYZ end = curve.GetEndPoint(1);

        // Calculate distances from the plane for each endpoint
        double startDistance = plane.Normal.DotProduct(start - plane.Origin);
        double endDistance = plane.Normal.DotProduct(end - plane.Origin);

        // Allow for a small tolerance in alignment
        double tolerance = 1e-9;
        return Math.Abs(startDistance) < tolerance && Math.Abs(endDistance) < tolerance;
    }

    private XYZ ProjectPointToPlane(XYZ point, Plane plane)
    {
        if (plane == null || point == null)
            return null;

        XYZ planeOrigin = plane.Origin;
        XYZ planeNormal = plane.Normal;

        // Vector from the plane's origin to the point
        XYZ pointVector = point - planeOrigin;

        // Calculate the projection
        double distance = pointVector.DotProduct(planeNormal);
        return point - distance * planeNormal;
    }

    private void UpdateCameraParameter()
    {
        if (_cameraElement == null)
        {
            TaskDialog.Show("Debug", "Camera element is null");
            return;
        }

        Document doc = _cameraElement.Document;
        
        using (Transaction trans = new Transaction(doc, "Update Camera Parameters"))
        {
            trans.Start();

            // 1. Update "Pööra Kaamerat" (Rotation)
            Parameter rotationParam = _cameraElement.LookupParameter(SettingsManager.Settings.ParameterName_UserRotation);
            if (rotationParam != null && !rotationParam.IsReadOnly)
            {
                double rotationRadians = _parameterValue * (Math.PI / 180.0);
                rotationParam.Set(rotationRadians);
            }

            // 2. Update "Kaamera nurk" (FOV Override)
            Parameter fovParam = _cameraElement.LookupParameter(SettingsManager.Settings.ParameterName_FOVOverride);
            if (fovParam != null && !fovParam.IsReadOnly)
            {
                double fovRadians = _fovAngle * (Math.PI / 180.0);
                fovParam.Set(fovRadians);
            }

            trans.Commit();
        }
    }

    private ElementId DrawLayer(double distance, ElementId typeId, bool drawDimension)
    {
        string compositeKey = (_cameraElement != null) ? _cameraElement.Id.ToString() : "NoCam";

        if (typeId != null)
        {
            compositeKey += "_" + typeId.ToString();
        }

        if (_cameraToFilledRegionMap.ContainsKey(compositeKey))
        {
            ElementId oldRegionId = _cameraToFilledRegionMap[compositeKey];
            _drawingTools.DeleteElement(oldRegionId);
            _cameraToFilledRegionMap.Remove(compositeKey);
        }

        _drawingTools.SetParameters(_position, distance, _rotationAngle, _fovAngle, typeId);
        ElementId newRegionId = _drawingTools.DrawFilledRegion(_sliderResolution); // Use slider resolution

        if (newRegionId != ElementId.InvalidElementId)
        {
            if (_cameraElement != null)
                _cameraToFilledRegionMap[compositeKey] = newRegionId;

            if (drawDimension)
            {
                _drawingTools.CreateAngularDimension(newRegionId);
            }
        }
        return newRegionId;
    }

    public string GetName()
    {
        return "Drawing Event Handler";
    }
}
}
