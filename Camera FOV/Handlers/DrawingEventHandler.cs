using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
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

    // Requests wait here in the order the user made them until Revit runs the external event.
    private readonly object _queueLock = new object();
    private readonly List<DrawingRequest> _pending = new List<DrawingRequest>();
    private readonly ExternalEvent _externalEvent;
    private bool _closed;

    // Context of the request being executed, loaded from that request on the Revit thread.
    private DrawingAction _currentAction = DrawingAction.None;
    private DrawingTools _drawingTools;
    private XYZ _position;
    private double _maxDistance;
    private double _rotationAngle;
    private double _fovAngle;
    private ElementId _filledRegionTypeId;
    private double _sliderResolution;
    private UIDocument _uiDoc;
    private MainWindow _mainWindow;
    private Element _cameraElement; // For parameter updates
    private double _parameterValue; // For parameter updates
    private List<ElementId> _lastBatchCreatedIds = new List<ElementId>(); // Undo batch tracker
    private bool _drawAngularDimension;
    private IReadOnlyList<DoriLayerConfig> _doriLayers;

    // Must be constructed in a Revit API context (e.g. while an external command runs).
    public DrawingEventHandler()
    {
        _externalEvent = ExternalEvent.Create(this);
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    // Queues the request and asks Revit to run it. A preview replaces a preview still waiting at
    // the end of the queue; every other action is kept and runs exactly once, in order.
    public bool Enqueue(DrawingRequest request, out string error)
    {
        error = null;
        lock (_queueLock)
        {
            if (_closed)
            {
                error = "The Camera FOV window has been closed.";
                return false;
            }

            if (request.IsPreview && _pending.Count > 0 && _pending[_pending.Count - 1].IsPreview)
                _pending[_pending.Count - 1] = request;
            else
                _pending.Add(request);
        }

        return RaiseEvent(out error);
    }

    // Called when the window closes: waiting previews are dropped, actions the user already asked
    // for still run, then the cleanup request, and nothing is accepted afterwards.
    public void Close(DrawingRequest cleanup)
    {
        lock (_queueLock)
        {
            if (_closed) return;
            _pending.RemoveAll(r => r.IsPreview);
            _pending.Add(cleanup);
            _closed = true;
        }

        RaiseEvent(out _);
    }

    private bool RaiseEvent(out string error)
    {
        error = null;
        ExternalEventRequest result = _externalEvent.Raise();

        // Pending means an earlier raise has not run yet; that run drains the whole queue.
        if (result == ExternalEventRequest.Accepted || result == ExternalEventRequest.Pending)
            return true;

        // Revit refused the event, so nothing queued would ever run. Drop it rather than let it
        // replay later against a model that may have changed.
        lock (_queueLock)
        {
            _pending.Clear();
        }

        error = $"Revit did not accept the request ({result}). Nothing was changed in the model; please try again.";
        return false;
    }

    public void Execute(UIApplication app)
    {
        while (true)
        {
            DrawingRequest request;
            lock (_queueLock)
            {
                if (_pending.Count == 0) return;
                request = _pending[0];
                _pending.RemoveAt(0);
            }

            Run(app, request);
        }
    }

    // Returns why the request can no longer run safely, or null when it can.
    private static string Validate(DrawingRequest request, UIApplication app)
    {
        Document doc = request.Document;
        if (doc == null || !doc.IsValidObject)
            return "the project it was made in has been closed";

        UIDocument activeUiDoc = app.ActiveUIDocument;
        if (request.RequiresActiveDocument && (activeUiDoc == null || !activeUiDoc.Document.Equals(doc)))
            return $"'{doc.Title}' is no longer the active project";

        if (!(doc.GetElement(request.ViewId) is View view))
            return "the view it was made in no longer exists";

        if (request.RequiresActiveView && activeUiDoc.ActiveView?.Id != request.ViewId)
            return $"'{view.Name}' is no longer the active view";

        if (request.CameraId != null && doc.GetElement(request.CameraId) == null)
            return "the selected camera no longer exists";

        return null;
    }

    private void Load(DrawingRequest request, UIApplication app)
    {
        _currentAction = request.Action;
        _drawingTools = request.DrawingTools;
        _uiDoc = app.ActiveUIDocument;
        _position = request.Position;
        _maxDistance = request.MaxDistance;
        _rotationAngle = request.RotationAngle;
        _fovAngle = request.FovAngle;
        _filledRegionTypeId = request.FilledRegionTypeId;
        _sliderResolution = request.SliderResolution;
        _cameraElement = request.CameraId != null ? request.Document.GetElement(request.CameraId) : null;
        _parameterValue = request.UserRotation;
        _doriLayers = request.DoriLayers;
        _drawAngularDimension = request.DrawAngularDimension;
    }

    private void Run(UIApplication app, DrawingRequest request)
    {
        string rejection = Validate(request, app);
        if (rejection != null)
        {
            if (request.IsPreview || request.Action == DrawingAction.Delete)
                System.Diagnostics.Debug.WriteLine($"Skipped {request.Action}: {rejection}");
            else
                MessageDialog.ShowWarning(
                    $"{request.Description} didn’t run",
                    $"It was skipped because {rejection}, so nothing was changed. Go back to the view the Camera FOV window was opened in and try again.");
            return;
        }

        Load(request, app);

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
                        ElementId id = DrawLayer(_maxDistance, _filledRegionTypeId, _drawAngularDimension);
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
                    System.Diagnostics.Debug.WriteLine($"No handler for {_currentAction}");
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(
                $"{request.Description} failed",
                "Revit reported an error and the change was not completed. If it keeps happening, show the details and send them with a description of what you were doing.",
                ex);
        }
        finally
        {
            // Don't hold on to elements or documents between requests
            _currentAction = DrawingAction.None;
            _cameraElement = null;
            _uiDoc = null;
        }
    }
    private void CreateBoundaryLine()
    {
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
                    transaction.RollBack(); // Rollback since nothing is being changed
                    MessageDialog.ShowInfo(
                        "Boundary line style already exists",
                        "This project already has a “Boundary” line style, so nothing was changed. Draw detail lines with it wherever something should block a camera’s view.");
                    return;
                }

                // Create a new subcategory for "Boundary"
                boundaryCategory = categories.NewSubcategory(linesCategory, "Boundary");

                if (boundaryCategory == null)
                {
                    transaction.RollBack();
                    MessageDialog.ShowError(
                        "Couldn’t create the Boundary line style",
                        "Revit did not create the “Boundary” subcategory under Lines. Check that the project isn’t read-only, or create a line style named “Boundary” manually in Manage → Object Styles.");
                    return;
                }

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

                transaction.Commit();

                MessageDialog.ShowSuccess(
                    "Boundary line style created",
                    "Draw detail lines with the “Boundary” style wherever walls or other objects should block a camera’s view, or use Trace walls in Settings to create them automatically.",
                    solidPattern == null
                        ? new List<MessageDialog.Item> { new MessageDialog.Item("Line pattern", "No “Solid” line pattern was found, so the style uses the project default. You can change it in Manage → Object Styles.") }
                        : null);
            }
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(
                "Couldn’t create the Boundary line style",
                "Revit reported an error, so nothing was changed.",
                ex);
        }
    }
    private void CreateFilledRegions()
    {
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
                    transaction.RollBack();
                    MessageDialog.ShowError(
                        "Couldn’t create the DORI region types",
                        "This project has no solid fill pattern, which the DORI regions use. Add a fill pattern with the “Solid fill” option in Manage → Additional Settings → Fill Patterns, then try again.");
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
                        transaction.RollBack();
                        MessageDialog.ShowError(
                            "Couldn’t create the DORI region types",
                            "The new types are copied from an existing filled region type, but this project has none. Create any filled region type (Annotate → Region → Filled Region), then try again.");
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

                // One summary listing every DORI type and whether it was created or already there
                var items = filledRegionData.Keys
                    .Select(name => new MessageDialog.Item(name, createdRegions.Contains(name)
                        ? "Created with a solid fill."
                        : "Already in the project; left unchanged."))
                    .ToList();

                if (createdRegions.Any())
                {
                    MessageDialog.ShowSuccess(
                        createdRegions.Count == filledRegionData.Count ? "DORI region types created" : "Missing DORI region types created",
                        "Each ticked DORI level is drawn with its own filled region type.",
                        items);
                }
                else
                {
                    MessageDialog.ShowInfo(
                        "DORI region types already exist",
                        "All four types are already in this project, so nothing was changed.",
                        items);
                }
            }
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(
                "Couldn’t create the DORI region types",
                "Revit reported an error, so nothing was changed.",
                ex);
        }
    }

    // Categories that act as obstructions when traced. Doors are intentionally left out: wall
    // solids already have door openings cut out of them, so skipping the door leaf keeps the
    // opening open instead of closing the wall across it.
    private static readonly List<BuiltInCategory> TracedCategories = new List<BuiltInCategory>
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions
    };

    private const double GeometryTolerance = 1e-5; // feet

    // Vertical limits of the active plan, in host (internal) coordinates.
    private class TraceRange
    {
        public double CutZ;
        public double BottomZ = double.MinValue;
        public double TopZ = double.MaxValue;
        public Plane CutPlane;
        public Plane ViewPlane;
    }

    private class TraceSummary
    {
        public int LinesCreated;
        public int LinesReplaced;
        public int ElementsTraced;
        public int ElementsBelowOrAboveCut;
        public int LinksTraced;
        public int LinksNotLoaded;
        public int SectionFailures;
    }

    // Traces the footprint of walls, columns, windows and curtain wall parts where they cross the
    // active plan's cut plane. Elements that do not reach the cut plane (e.g. low walls) and
    // geometry on other floors are not traced. Lines from a previous trace in this view are
    // replaced; user-drawn Boundary lines are kept.
    private void TraceWallsAndDrawBoundary()
    {

        Document doc = _uiDoc.Document;

        try
        {
            if (!(_uiDoc.ActiveView is ViewPlan view))
            {
                MessageDialog.ShowWarning(
                    "Tracing needs a plan view",
                    "Walls are traced where they cross the plan’s cut plane, so the active view must be a floor or ceiling plan. Open a plan view, reopen Camera FOV there and try again.");
                return;
            }

            TraceRange range = GetTraceRange(view);
            if (range == null)
            {
                MessageDialog.ShowWarning(
                    "Couldn’t find the cut plane",
                    $"The view range of “{view.Name}” has no level for its cut plane. Check View Range in the view’s properties, then try again.");
                return;
            }

            GraphicsStyle boundaryLineStyle = GetBoundaryLineStyle(doc);
            if (boundaryLineStyle == null)
            {
                MessageDialog.ShowWarning(
                    "Boundary line style is missing",
                    "Traced lines are drawn with the “Boundary” line style. Open Settings and click Create “Boundary” line style first, then trace again.");
                return;
            }

            ElementMulticategoryFilter filter = new ElementMulticategoryFilter(TracedCategories);

            // Collect elements visible in the current view
            var elements = new FilteredElementCollector(doc, view.Id)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .ToList();

            List<RevitLinkInstance> selectedLinks = SelectLinkedModels(doc, view);

            if (!elements.Any() && !selectedLinks.Any())
            {
                MessageDialog.ShowInfo(
                    "Nothing to trace",
                    $"No walls, columns, windows or curtain wall parts are visible in “{view.Name}”, and no linked models were chosen. Nothing was changed.");
                return;
            }

            var summary = new TraceSummary();

            using (Transaction transaction = new Transaction(doc, "Trace Walls and Columns and Draw Boundary"))
            {
                transaction.Start();

                HashSet<string> drawnCurveHashes = ReplacePreviouslyTracedLines(doc, view, range.ViewPlane, summary);

                foreach (var element in elements)
                {
                    TraceElement(element, Transform.Identity, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                foreach (var link in selectedLinks)
                {
                    TraceLinkedModel(link, filter, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                transaction.Commit();
            }

            ShowTraceSummary(summary);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError(
                "Tracing failed",
                "Revit reported an error while tracing, so no Boundary lines were changed.",
                ex);
        }
    }

    private List<RevitLinkInstance> SelectLinkedModels(Document doc, View view)
    {
        var linkInstances = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToList();

        List<RevitLinkInstance> selectedLinks = new List<RevitLinkInstance>();

        if (linkInstances.Any())
        {
            LinkedModelsSelectionWindow window = new LinkedModelsSelectionWindow(linkInstances.Select(l => l.Name).ToList());
            window.ShowDialog();

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

        return selectedLinks;
    }

    private static GraphicsStyle GetBoundaryLineStyle(Document doc)
    {
        Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        Category boundarySubCategory = linesCategory.SubCategories.Contains("Boundary")
            ? linesCategory.SubCategories.get_Item("Boundary")
            : null;
        return boundarySubCategory?.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    private static TraceRange GetTraceRange(ViewPlan view)
    {
        Document doc = view.Document;
        PlanViewRange viewRange = view.GetViewRange();

        Level cutLevel = doc.GetElement(viewRange.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? view.GenLevel;
        if (cutLevel == null) return null;

        var range = new TraceRange
        {
            CutZ = cutLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.CutPlane)
        };

        // Top/bottom may reference "Unlimited" or "Level Above/Below"; those leave the range open.
        if (doc.GetElement(viewRange.GetLevelId(PlanViewPlane.TopClipPlane)) is Level topLevel)
            range.TopZ = topLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.TopClipPlane);

        if (doc.GetElement(viewRange.GetLevelId(PlanViewPlane.BottomClipPlane)) is Level bottomLevel)
            range.BottomZ = bottomLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.BottomClipPlane);

        range.CutPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, range.CutZ));

        if (view.SketchPlane != null)
            range.ViewPlane = view.SketchPlane.GetPlane();
        else
            range.ViewPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, view.GenLevel?.ProjectElevation ?? 0));

        return range;
    }

    // Deletes lines generated by a previous trace in this view and returns the hashes of the
    // remaining Boundary lines, so the new trace neither duplicates them nor removes them.
    private HashSet<string> ReplacePreviouslyTracedLines(Document doc, View view, Plane viewPlane, TraceSummary summary)
    {
        var existingHashes = new HashSet<string>();
        var toDelete = new List<ElementId>();

        var curveElements = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(CurveElement))
            .WhereElementIsNotElementType()
            .Cast<CurveElement>();

        foreach (CurveElement curveElement in curveElements)
        {
            if (ElementTagStorage.IsTracedBoundaryLine(curveElement, view))
            {
                toDelete.Add(curveElement.Id);
            }
            else if (curveElement.LineStyle?.Name == "Boundary")
            {
                foreach (Curve projected in ProjectCurveToPlane(curveElement.GeometryCurve, viewPlane, doc))
                {
                    existingHashes.Add(GenerateCurveHash(projected));
                }
            }
        }

        if (toDelete.Any())
        {
            doc.Delete(toDelete);
            summary.LinesReplaced = toDelete.Count;
        }

        return existingHashes;
    }

    private void TraceLinkedModel(RevitLinkInstance link, ElementFilter filter, TraceRange range, View hostView, GraphicsStyle boundaryLineStyle, Document hostDoc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        Document linkedDoc = link.GetLinkDocument();
        if (linkedDoc == null)
        {
            summary.LinksNotLoaded++;
            return;
        }

        summary.LinksTraced++;

        // Converts linked document coordinates to host coordinates (offset, rotation, elevation)
        Transform linkTransform = link.GetTotalTransform();

        var linkedElements = new FilteredElementCollector(linkedDoc)
            .WherePasses(filter)
            .WhereElementIsNotElementType();

        foreach (var element in linkedElements)
        {
            TraceElement(element, linkTransform, range, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes, summary);
        }
    }

    // toHost maps the element's own document coordinates to host coordinates
    // (identity for host elements, the link transform for linked ones).
    private void TraceElement(Element element, Transform toHost, TraceRange range, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        // Coarse filter on the bounding box, evaluated in host coordinates
        BoundingBoxXYZ box = element.get_BoundingBox(null);
        if (box == null) return;

        GetHostZRange(box, toHost, out double minZ, out double maxZ);

        if (maxZ < range.BottomZ || minZ > range.TopZ)
            return; // Outside the view range entirely, e.g. another floor

        if (minZ > range.CutZ + GeometryTolerance || maxZ < range.CutZ - GeometryTolerance)
        {
            summary.ElementsBelowOrAboveCut++;
            return;
        }

        Options geomOptions = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        GeometryElement geometry = element.get_Geometry(geomOptions);
        if (geometry == null) return;

        // Section in the element's own coordinates, then bring the outline into the host.
        Transform toLocal = toHost.Inverse;
        Plane localCutPlane = Plane.CreateByNormalAndOrigin(
            toLocal.OfVector(range.CutPlane.Normal),
            toLocal.OfPoint(range.CutPlane.Origin));

        bool hasSolids = false;
        bool traced = false;
        foreach (Solid solid in GetSolids(geometry))
        {
            hasSolids = true;
            foreach (Curve sectionCurve in SectionSolid(solid, localCutPlane, summary))
            {
                traced = true;
                DrawTracedCurve(sectionCurve.CreateTransformed(toHost), range.ViewPlane, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
            }
        }

        if (traced)
            summary.ElementsTraced++;
        else if (hasSolids)
            summary.ElementsBelowOrAboveCut++;
    }

    private static void GetHostZRange(BoundingBoxXYZ box, Transform toHost, out double minZ, out double maxZ)
    {
        Transform boxToHost = toHost.Multiply(box.Transform);
        minZ = double.MaxValue;
        maxZ = double.MinValue;

        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
        {
            double hostZ = boxToHost.OfPoint(new XYZ(x, y, z)).Z;
            minZ = Math.Min(minZ, hostZ);
            maxZ = Math.Max(maxZ, hostZ);
        }
    }

    private static IEnumerable<Solid> GetSolids(GeometryElement geometry)
    {
        foreach (GeometryObject geomObj in geometry)
        {
            if (geomObj is Solid solid)
            {
                if (solid.Faces.Size > 0 && solid.Volume > 0)
                    yield return solid;
            }
            else if (geomObj is GeometryInstance instance)
            {
                foreach (Solid nested in GetSolids(instance.GetInstanceGeometry()))
                    yield return nested;
            }
        }
    }

    // Returns the outline of the solid on the cut plane. The part above the plane is kept and its
    // faces lying on the plane are the section; solids not crossing the plane yield nothing.
    private static List<Curve> SectionSolid(Solid solid, Plane cutPlane, TraceSummary summary)
    {
        var curves = new List<Curve>();

        Solid abovePart;
        try
        {
            abovePart = BooleanOperationsUtils.CutWithHalfSpace(solid, cutPlane);
        }
        catch (Exception ex)
        {
            summary.SectionFailures++;
            System.Diagnostics.Debug.WriteLine($"Failed to section solid: {ex.Message}");
            return curves;
        }

        if (abovePart == null || abovePart.Faces.Size == 0) return curves;

        foreach (Face face in abovePart.Faces)
        {
            if (!(face is PlanarFace planarFace)) continue;
            if (Math.Abs(planarFace.FaceNormal.DotProduct(cutPlane.Normal)) < 1 - GeometryTolerance) continue;
            if (Math.Abs(cutPlane.Normal.DotProduct(planarFace.Origin - cutPlane.Origin)) > GeometryTolerance) continue;

            foreach (EdgeArray loop in planarFace.EdgeLoops)
            {
                foreach (Edge edge in loop)
                {
                    curves.Add(edge.AsCurve());
                }
            }
        }

        return curves;
    }

    private void DrawTracedCurve(Curve curve, Plane viewPlane, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        foreach (Curve projectedCurve in ProjectCurveToPlane(curve, viewPlane, doc))
        {
            // Hash the PROJECTED curve to deduplicate 2D lines
            if (!drawnCurveHashes.Add(GenerateCurveHash(projectedCurve)))
                continue;

            try
            {
                DetailCurve detailCurve = doc.Create.NewDetailCurve(view, projectedCurve);
                detailCurve.LineStyle = boundaryLineStyle;
                ElementTagStorage.TagTracedBoundaryLine(detailCurve, view);
                summary.LinesCreated++;
            }
            catch (Exception ex)
            {
                // Ignore failures for individual curves (e.g. degenerate curves)
                System.Diagnostics.Debug.WriteLine($"Failed to create boundary line: {ex.Message}");
            }
        }
    }

    private static void ShowTraceSummary(TraceSummary summary)
    {
        var items = new List<MessageDialog.Item>();

        if (summary.LinesReplaced > 0)
            items.Add(new MessageDialog.Item("Previous trace replaced",
                $"{summary.LinesReplaced} lines from the last trace were removed. Boundary lines you drew yourself were kept."));

        if (summary.LinksTraced > 0)
            items.Add(new MessageDialog.Item("Linked models", $"{summary.LinksTraced} linked models were traced as well."));

        if (summary.ElementsBelowOrAboveCut > 0)
            items.Add(new MessageDialog.Item("Not at the cut plane",
                $"{summary.ElementsBelowOrAboveCut} elements in the view range don’t reach the cut plane (for example low walls) and were skipped. Draw Boundary lines manually for any that should block the view."));

        if (summary.LinksNotLoaded > 0)
            items.Add(new MessageDialog.Item("Links not loaded",
                $"{summary.LinksNotLoaded} linked models are unloaded and were skipped. Reload them in Manage Links to include them."));

        if (summary.SectionFailures > 0)
            items.Add(new MessageDialog.Item("Geometry skipped",
                $"{summary.SectionFailures} solids couldn’t be cut at the cut plane and were skipped. Check those areas and add Boundary lines by hand if needed."));

        string message = $"Created {summary.LinesCreated} Boundary lines from {summary.ElementsTraced} elements crossing the view’s cut plane.";
        bool needsAttention = summary.ElementsBelowOrAboveCut > 0 || summary.LinksNotLoaded > 0 || summary.SectionFailures > 0;

        MessageDialog.Show(
            needsAttention ? MessageDialog.Kind.Info : MessageDialog.Kind.Success,
            "Boundaries traced",
            message,
            items);
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

    // Projects a curve onto the plane. Lines and arcs keep their type; other curves
    // (splines, ellipses) are tessellated into lines. Segments shorter than Revit allows are dropped.
    private static List<Curve> ProjectCurveToPlane(Curve curve, Plane plane, Document doc)
    {
        var result = new List<Curve>();
        if (curve == null || !curve.IsBound) return result;

        double shortCurveTolerance = doc.Application.ShortCurveTolerance * 2;

        try
        {
            XYZ start = ProjectPointToPlane(curve.GetEndPoint(0), plane);
            XYZ end = ProjectPointToPlane(curve.GetEndPoint(1), plane);

            if (curve is Line)
            {
                if (start.DistanceTo(end) >= shortCurveTolerance)
                    result.Add(Line.CreateBound(start, end));
                return result;
            }

            if (curve is Arc arc && start.DistanceTo(end) >= shortCurveTolerance)
            {
                XYZ mid = ProjectPointToPlane(arc.Evaluate(0.5, true), plane);
                try
                {
                    result.Add(Arc.Create(start, end, mid));
                    return result;
                }
                catch
                {
                    // Arc seen edge-on projects to a straight segment; tessellate below
                }
            }

            XYZ last = null;
            foreach (XYZ point in curve.Tessellate())
            {
                XYZ projected = ProjectPointToPlane(point, plane);
                if (last == null)
                {
                    last = projected;
                }
                else if (projected.DistanceTo(last) >= shortCurveTolerance)
                {
                    result.Add(Line.CreateBound(last, projected));
                    last = projected;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to project curve: {ex.Message}");
        }

        return result;
    }

    private static XYZ ProjectPointToPlane(XYZ point, Plane plane)
    {
        XYZ pointVector = point - plane.Origin;
        double distance = pointVector.DotProduct(plane.Normal);
        return point - distance * plane.Normal;
    }

    private void UpdateCameraParameter()
    {
        if (_cameraElement == null)
        {
            MessageDialog.ShowWarning(
                "No camera to update",
                "Select a camera in the Camera FOV window before its rotation and field of view can be written back.");
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
        Document doc = _drawingTools.Document;
        View view = _drawingTools.View;

        // Replace the coverage previously drawn for this camera + DORI type in this view.
        // The association is stored on the region itself, so it survives reopening the
        // window or the project. Untagged regions from older versions are never deleted.
        foreach (ElementId oldRegionId in ElementTagStorage.FindCoverageRegions(doc, view, _cameraElement, typeId))
        {
            _drawingTools.DeleteElement(oldRegionId);
        }

        _drawingTools.SetParameters(_position, distance, _rotationAngle, _fovAngle, typeId);
        ElementId newRegionId = _drawingTools.DrawFilledRegion( // Use slider resolution
            _sliderResolution,
            region => ElementTagStorage.TagCoverageRegion(region, _cameraElement, typeId, view));

        if (newRegionId != ElementId.InvalidElementId && drawDimension)
        {
            _drawingTools.CreateAngularDimension(newRegionId);
        }
        return newRegionId;
    }

    public string GetName()
    {
        return "Drawing Event Handler";
    }
}
}
