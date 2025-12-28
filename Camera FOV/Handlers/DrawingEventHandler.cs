using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Camera_FOV.Utils;
using Camera_FOV;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Camera_FOV.Handlers
{
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
    public bool DrawAngularDimension { get; set; } = false;

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
        double userRotationForParameter = 0)
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
                        
                        // Check if we already have a region for this camera+type and delete it
                        // Composite key: CameraId + FilledRegionTypeId
                        string compositeKey = _cameraElement.Id.ToString();
                        
                        // Append RegionTypeId if available to support different DORI regions
                        if (_filledRegionTypeId != null)
                        {
                            compositeKey += "_" + _filledRegionTypeId.ToString();
                        }

                        if (_cameraToFilledRegionMap.ContainsKey(compositeKey))
                        {
                            ElementId oldRegionId = _cameraToFilledRegionMap[compositeKey];
                            _drawingTools.DeleteElement(oldRegionId);
                            _cameraToFilledRegionMap.Remove(compositeKey);
                        }
                        
                        _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle, _fovAngle, _filledRegionTypeId);
                        ElementId newRegionId = _drawingTools.DrawFilledRegion(_sliderResolution);
                        
                        // Track the new region
                        if (newRegionId != ElementId.InvalidElementId)
                        {
                            _cameraToFilledRegionMap[compositeKey] = newRegionId;
                            
                            // Create Angular Dimension if requested (e.g. for Identification DORI)
                            if (DrawAngularDimension)
                            {
                                _drawingTools.CreateAngularDimension(newRegionId);
                            }
                        }
                    }
                    else
                    {
                        // Fallback if no camera element provided (e.g. testing)
                         _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle, _fovAngle, _filledRegionTypeId);
                         ElementId newRegionId = _drawingTools.DrawFilledRegion(_sliderResolution);
                         if (DrawAngularDimension && newRegionId != ElementId.InvalidElementId)
                         {
                             _drawingTools.CreateAngularDimension(newRegionId);
                         }
                    }
                    break;


                case DrawingAction.DeleteFilledRegion:
                    _drawingTools.DeleteFilledRegion();
                    break;

                case DrawingAction.UndoFilledRegion:
                    _drawingTools.DeleteFilledRegion();
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

                // Collect walls and structural columns visible in the current view
                var elements = new FilteredElementCollector(doc, _uiDoc.ActiveView.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is Wall ||
                                (e is FamilyInstance fi && fi.Category.Id == new ElementId(BuiltInCategory.OST_StructuralColumns)))
                    .ToList();

                if (!elements.Any())
                {
                    TaskDialog.Show("Info", "No walls or structural columns found in the current view.");
                    transaction.RollBack();
                    return;
                }

                // Get the "Boundary" line style
                GraphicsStyle boundaryLineStyle = new FilteredElementCollector(doc)
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>()
                    .FirstOrDefault(gs => gs.Name.Equals("Boundary"));

                if (boundaryLineStyle == null)
                {
                    TaskDialog.Show("Error", "Line style 'Boundary' not found. Please create it first.");
                    transaction.RollBack();
                    return;
                }

                View view = _uiDoc.ActiveView;

                foreach (var element in elements)
                {
                    if (element is Wall)
                    {
                        // Use existing logic for walls
                        ProcessWall(element, view, boundaryLineStyle, doc);
                    }
                    else if (element is FamilyInstance column && column.Category.Id == new ElementId(BuiltInCategory.OST_StructuralColumns))
                    {
                        // Use bounding box for structural columns
                        ProcessColumnUsingBoundingBox(column, view, boundaryLineStyle, doc);
                    }
                }

                transaction.Commit();
                MessageBox.Show("Detail lines traced around walls and structural columns successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred while tracing walls and columns:\n{ex.Message}");
        }
    }

    // Process walls (same logic as before)
    private void ProcessWall(Element wall, View view, GraphicsStyle boundaryLineStyle, Document doc)
    {
        Options geomOptions = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
        GeometryElement geometry = wall.get_Geometry(geomOptions);

        if (geometry == null) return;

        // Use a HashSet to store unique edge references to prevent duplicates
        HashSet<string> uniqueEdges = new HashSet<string>();

        foreach (GeometryObject geomObj in geometry)
        {
            if (geomObj is Solid solid)
            {
                foreach (Face face in solid.Faces)
                {
                    PlanarFace planarFace = face as PlanarFace;

                    if (planarFace != null && Math.Abs(planarFace.FaceNormal.Z) < 0.01) // Only process vertical faces
                    {
                        foreach (EdgeArray edgeLoop in planarFace.EdgeLoops)
                        {
                            foreach (Edge edge in edgeLoop)
                            {
                                // Create a unique identifier for each edge based on its curve reference
                                string edgeIdentifier = edge.Reference.ElementId.ToString() + "-" + edge.GetHashCode();

                                if (!uniqueEdges.Contains(edgeIdentifier))
                                {
                                    uniqueEdges.Add(edgeIdentifier); // Add to the HashSet to avoid duplicates

                                    Curve edgeCurve = edge.AsCurve();
                                    Curve projectedCurve = ProjectCurveToPlane(edgeCurve, view.SketchPlane.GetPlane(), doc);

                                    if (projectedCurve != null)
                                    {
                                        DetailCurve detailCurve = doc.Create.NewDetailCurve(view, projectedCurve);
                                        detailCurve.LineStyle = boundaryLineStyle;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // Helper method to generate a hash for a curve based on its start and end points
    private string GenerateCurveHash(Curve curve)
    {
        XYZ start = curve.GetEndPoint(0);
        XYZ end = curve.GetEndPoint(1);

        // Ensure the hash is independent of the curve direction
        return start.IsAlmostEqualTo(end) ? $"{start}-{end}" : $"{end}-{start}";
    }

    // Process structural columns using bounding box
    private void ProcessColumnUsingBoundingBox(FamilyInstance column, View view, GraphicsStyle boundaryLineStyle, Document doc)
    {
        BoundingBoxXYZ bbox = column.get_BoundingBox(view);

        if (bbox == null)
        {
            TaskDialog.Show("Error", "Bounding box for the column is null.");
            return;
        }

        // Get the plane of the current view
        Plane viewPlane = view.SketchPlane?.GetPlane();
        if (viewPlane == null)
        {
            TaskDialog.Show("Error", "Active view does not have a valid sketch plane.");
            return;
        }

        // Project bounding box corners onto the sketch plane
        XYZ min = ProjectPointToPlane(bbox.Min, viewPlane);
        XYZ max = ProjectPointToPlane(bbox.Max, viewPlane);

        // Create boundary lines from the projected bounding box edges
        List<Curve> boundingBoxEdges = new List<Curve>
{
    ProjectCurveToPlane(Line.CreateBound(bbox.Min, new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z)), viewPlane, doc),
    ProjectCurveToPlane(Line.CreateBound(new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z), bbox.Max), viewPlane, doc),
    ProjectCurveToPlane(Line.CreateBound(bbox.Max, new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z)), viewPlane, doc),
    ProjectCurveToPlane(Line.CreateBound(new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z), bbox.Min), viewPlane, doc)
};


        foreach (Curve edge in boundingBoxEdges)
        {
            try
            {
                // Validate the curve is planar before creating DetailCurve
                if (!IsCurvePlanarToSketchPlane(edge, viewPlane))
                {
                    TaskDialog.Show("Warning", "Curve is not planar to the sketch plane.");
                    continue;
                }

                DetailCurve detailCurve = doc.Create.NewDetailCurve(view, edge);
                detailCurve.LineStyle = boundaryLineStyle;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to create boundary line for column: {ex.Message}");
            }
        }
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
        Parameter param = _cameraElement.LookupParameter("Pööra Kaamerat");
        
        if (param == null)
        {
            TaskDialog.Show("Debug", "Parameter 'Pööra Kaamerat' not found");
            return;
        }

        if (param.IsReadOnly)
        {
            TaskDialog.Show("Debug", "Parameter is read-only");
            return;
        }
        
        using (Transaction trans = new Transaction(doc, "Update Camera Rotation"))
        {
            trans.Start();
            double rotationRadians = _parameterValue * (Math.PI / 180.0);
            param.Set(rotationRadians);
            trans.Commit();
        }
    }

    public string GetName()
    {
        return "Drawing Event Handler";
    }
}
}
