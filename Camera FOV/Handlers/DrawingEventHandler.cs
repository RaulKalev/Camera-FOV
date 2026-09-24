using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Camera_FOV.Utils;
using Camera_FOV;
using Camera_FOV.Services;
using Camera_FOV.Models;

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
        TraceWallsAndDrawBoundary,
        CheckCoverage,
        CheckViewCoverage,
        SaveTracingRules,
        CheckPoint,
        CoverageAudit
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
    private string _cameraStateAtSelection;
    private int _cameraResolution; // Horizontal pixels the DORI distances were calculated with

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

            if (request.IsCoalescable && _pending.Count > 0 && _pending[_pending.Count - 1].Action == request.Action)
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
            _pending.RemoveAll(r => r.IsCoalescable);
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
        _cameraStateAtSelection = request.CameraStateAtSelection;
        _cameraResolution = request.CameraResolution;
    }

    private void Run(UIApplication app, DrawingRequest request)
    {
        string rejection = Validate(request, app);
        if (rejection != null)
        {
            if (request.IsSilent)
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
                    DrawCoverage();
                    break;


                case DrawingAction.DeleteFilledRegion:
                    _drawingTools.DeleteFilledRegion();
                    break;

                case DrawingAction.UndoFilledRegion:
                    if (_lastBatchCreatedIds != null && _lastBatchCreatedIds.Any())
                    {
                        UndoLastCoverage();
                        ReportCameraCoverage();
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

                case DrawingAction.CheckCoverage:
                    ReportCameraCoverage();
                    break;

                case DrawingAction.CheckViewCoverage:
                    CheckViewCoverage();
                    break;

                case DrawingAction.CheckPoint:
                    ReportPointCoverage(request.Position);
                    break;

                case DrawingAction.CoverageAudit:
                    OpenCoverageAudit();
                    break;

                case DrawingAction.SaveTracingRules:
                    using (Transaction transaction = new Transaction(_drawingTools.Document, "Camera FOV Tracing Rules"))
                    {
                        transaction.Start();
                        ElementTagStorage.SaveTracingRules(_drawingTools.Document, request.TracingRules);
                        transaction.Commit();
                    }
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

    // Which categories are traced, and whether door and window openings are closed with a line, come
    // from the project's tracing rules (issue #8; see TracingRules).

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
        public int OpeningsCollected;
        public int OpeningsFound;
        public int OpeningsAtCut;
        public int OpeningsClosed;
        public int LayeredWalls;
        public int ElementsBelowOrAboveCut;
        public int LinksTraced;
        public int LinksNotLoaded;
        public int SectionFailures;
    }

    // Traces the footprint of walls, columns and curtain wall parts where they cross the active
    // plan's cut plane, and draws one line across each door or window opening at that height.
    // Elements that do not reach the cut plane (e.g. low walls) and geometry on other floors are
    // not traced. Lines from a previous trace in this view are replaced; user-drawn Boundary
    // lines are kept.
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

            TracingRules rules = ElementTagStorage.LoadTracingRules(doc);
            if (rules.TracesNothing)
            {
                MessageDialog.ShowWarning(
                    "Nothing is set to be traced",
                    "The tracing rules for this project ignore every obstacle category. Open Settings and set at least one category to block the view, then trace again.");
                return;
            }

            List<BuiltInCategory> tracedCategories = rules.TracedCategories();
            ElementMulticategoryFilter filter = tracedCategories.Any() ? new ElementMulticategoryFilter(tracedCategories) : null;

            // Collect elements visible in the current view
            List<Element> elements = filter == null
                ? new List<Element>()
                : new FilteredElementCollector(doc, view.Id)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .ToList();

            var summary = new TraceSummary();

            List<Element> openings = CollectOpenings(new FilteredElementCollector(doc, view.Id), rules, summary);

            List<RevitLinkInstance> selectedLinks = SelectLinkedModels(doc, view);

            if (!elements.Any() && !openings.Any() && !selectedLinks.Any())
            {
                MessageDialog.ShowInfo(
                    "Nothing to trace",
                    $"No walls, columns, doors, windows or curtain wall parts are visible in “{view.Name}”, and no linked models were chosen. Nothing was changed.");
                return;
            }

            using (Transaction transaction = new Transaction(doc, "Trace Walls and Columns and Draw Boundary"))
            {
                transaction.Start();

                HashSet<string> drawnCurveHashes = ReplacePreviouslyTracedLines(doc, view, range.ViewPlane, summary);

                foreach (var element in elements)
                {
                    TraceElement(element, Transform.Identity, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                foreach (var opening in openings)
                {
                    CloseOpening(opening, Transform.Identity, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                foreach (var link in selectedLinks)
                {
                    TraceLinkedModel(link, filter, rules, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                transaction.Commit();
            }

            ShowTraceSummary(summary, rules, CountCamerasNeedingReview(doc, view));
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

    // filter is null when the rules trace no solid categories (only door/window lines).
    private void TraceLinkedModel(RevitLinkInstance link, ElementFilter filter, TracingRules rules, TraceRange range, View hostView, GraphicsStyle boundaryLineStyle, Document hostDoc, HashSet<string> drawnCurveHashes, TraceSummary summary)
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

        if (filter != null)
        {
            var linkedElements = new FilteredElementCollector(linkedDoc)
                .WherePasses(filter)
                .WhereElementIsNotElementType();

            foreach (var element in linkedElements)
            {
                TraceElement(element, linkTransform, range, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes, summary);
            }
        }

        foreach (var opening in CollectOpenings(new FilteredElementCollector(linkedDoc), rules, summary))
        {
            CloseOpening(opening, linkTransform, range, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes, summary);
        }
    }

    // Elements in the door and window categories whose openings the rules close: loadable families
    // as well as the DirectShapes that IFC links and imports produce. Open categories stay untraced,
    // so the gap in the wall is left open.
    private static List<Element> CollectOpenings(FilteredElementCollector collector, TracingRules rules, TraceSummary summary)
    {
        List<BuiltInCategory> categories = rules.ClosedOpeningCategories();
        if (!categories.Any()) return new List<Element>();

        List<Element> openings = collector
            .WherePasses(new ElementMulticategoryFilter(categories))
            .WhereElementIsNotElementType()
            .ToList();

        summary.OpeningsCollected += openings.Count;
        return openings;
    }

    // Draws one line across a door or window opening where it crosses the cut plane. Openings
    // entirely above or below it leave the wall solid there, so the wall trace already closes them.
    private void CloseOpening(Element opening, Transform toHost, TraceRange range, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        BoundingBoxXYZ box = opening.get_BoundingBox(null);
        if (box == null) return;

        GetHostZRange(box, toHost, out double minZ, out double maxZ);
        if (maxZ < range.BottomZ || minZ > range.TopZ) return; // Another floor

        summary.OpeningsFound++;
        if (minZ > range.CutZ + GeometryTolerance || maxZ < range.CutZ - GeometryTolerance) return;
        summary.OpeningsAtCut++;

        // Both are in the opening's own document; the line is moved to the host at the end
        if (!TryGetLineAlongHostWall(opening, box, out XYZ start, out XYZ end) &&
            !TryGetLineAlongFootprint(opening, out start, out end))
            return;

        try
        {
            Line line = Line.CreateBound(start, end);
            int before = summary.LinesCreated;
            DrawTracedCurve(line.CreateTransformed(toHost), range.ViewPlane, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
            if (summary.LinesCreated > before)
                summary.OpeningsClosed++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to close opening {opening.Id}: {ex.Message}");
        }
    }

    // A door or window family hosted in a wall: a line on the wall's location line, as wide as the opening.
    private static bool TryGetLineAlongHostWall(Element opening, BoundingBoxXYZ box, out XYZ start, out XYZ end)
    {
        start = end = null;

        if (!(opening is FamilyInstance instance)) return false;
        if (!(instance.Host is Wall wall) || !(wall.Location is LocationCurve wallLocation)) return false;
        if (!(instance.Location is LocationPoint openingLocation)) return false;

        Curve wallCurve = wallLocation.Curve;
        IntersectionResult onWall = wallCurve.Project(openingLocation.Point);
        if (onWall == null) return false;

        XYZ tangent = wallCurve.ComputeDerivatives(onWall.Parameter, false).BasisX;
        XYZ direction = new XYZ(tangent.X, tangent.Y, 0);
        if (direction.IsZeroLength()) return false;
        direction = direction.Normalize();

        XYZ center = onWall.XYZPoint;
        if (!TryGetOpeningExtent(instance, box, center, direction, out double from, out double to)) return false;

        start = center + direction * from;
        end = center + direction * to;
        return true;
    }

    // Anything else (e.g. an IFC DirectShape): the line runs along the long side of the smallest
    // rectangle around the element's plan footprint, through its middle. For a door or window
    // that is the direction of the wall it sits in.
    private static bool TryGetLineAlongFootprint(Element opening, out XYZ start, out XYZ end)
    {
        start = end = null;

        GeometryElement geometry = opening.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse });
        if (geometry == null) return false;

        var points = new List<UV>();
        double z = 0;
        foreach (XYZ point in GetGeometryPoints(geometry))
        {
            points.Add(new UV(point.X, point.Y));
            z = point.Z;
        }

        List<UV> hull = ConvexHull(points);
        if (hull.Count < 2) return false;

        // Minimum-area rectangle: one of its sides lies along an edge of the convex hull
        double bestArea = double.MaxValue;
        UV bestAxis = null;
        double bestMin = 0, bestMax = 0, bestCross = 0;

        for (int i = 0; i < hull.Count; i++)
        {
            UV edge = hull[(i + 1) % hull.Count] - hull[i];
            if (edge.GetLength() < GeometryTolerance) continue;
            UV axis = edge.Normalize();
            UV normal = new UV(-axis.V, axis.U);

            double minA = double.MaxValue, maxA = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (UV p in hull)
            {
                double a = p.DotProduct(axis), n = p.DotProduct(normal);
                minA = Math.Min(minA, a); maxA = Math.Max(maxA, a);
                minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
            }

            double area = (maxA - minA) * (maxN - minN);
            if (area >= bestArea) continue;

            // Keep the long side as the axis
            bestArea = area;
            if (maxA - minA >= maxN - minN)
            {
                bestAxis = axis; bestMin = minA; bestMax = maxA; bestCross = (minN + maxN) / 2;
            }
            else
            {
                bestAxis = normal; bestMin = minN; bestMax = maxN; bestCross = -(minA + maxA) / 2;
            }
        }

        if (bestAxis == null || bestMax - bestMin < GeometryTolerance) return false;

        // Point = axis * along + perpendicular * across, with perpendicular = (-axis.V, axis.U)
        UV perpendicular = new UV(-bestAxis.V, bestAxis.U);
        UV a0 = bestAxis * bestMin + perpendicular * bestCross;
        UV a1 = bestAxis * bestMax + perpendicular * bestCross;
        start = new XYZ(a0.U, a0.V, z);
        end = new XYZ(a1.U, a1.V, z);
        return true;
    }

    private static IEnumerable<XYZ> GetGeometryPoints(GeometryElement geometry)
    {
        foreach (GeometryObject geomObj in geometry)
        {
            if (geomObj is Solid solid)
            {
                foreach (Edge edge in solid.Edges)
                foreach (XYZ point in edge.Tessellate())
                    yield return point;
            }
            else if (geomObj is Mesh mesh)
            {
                foreach (XYZ point in mesh.Vertices)
                    yield return point;
            }
            else if (geomObj is Curve curve && curve.IsBound)
            {
                foreach (XYZ point in curve.Tessellate())
                    yield return point;
            }
            else if (geomObj is GeometryInstance instance)
            {
                foreach (XYZ point in GetGeometryPoints(instance.GetInstanceGeometry()))
                    yield return point;
            }
        }
    }

    // Andrew's monotone chain; returns the hull counter-clockwise without repeating the first point.
    private static List<UV> ConvexHull(List<UV> points)
    {
        var sorted = points
            .GroupBy(p => (Math.Round(p.U, 6), Math.Round(p.V, 6)))
            .Select(g => g.First())
            .OrderBy(p => p.U).ThenBy(p => p.V)
            .ToList();
        if (sorted.Count < 3) return sorted;

        double Cross(UV o, UV a, UV b) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);

        var hull = new List<UV>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            int start = hull.Count;
            foreach (UV p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    // Offsets from the centre along the wall that span the opening. Uses the widest of the family's
    // width parameters (the rough opening is usually wider than the leaf), falling back to the
    // element's extent along the wall.
    private static bool TryGetOpeningExtent(FamilyInstance opening, BoundingBoxXYZ box, XYZ center, XYZ direction, out double from, out double to)
    {
        var widthParameters = new[]
        {
            BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM,
            BuiltInParameter.FAMILY_WIDTH_PARAM,
            BuiltInParameter.DOOR_WIDTH,
            BuiltInParameter.WINDOW_WIDTH
        };

        double width = 0;
        foreach (Element source in new Element[] { opening, opening.Symbol })
        {
            if (source == null) continue;
            foreach (BuiltInParameter id in widthParameters)
            {
                Parameter parameter = source.get_Parameter(id);
                if (parameter != null && parameter.StorageType == StorageType.Double)
                    width = Math.Max(width, parameter.AsDouble());
            }
        }

        if (width > GeometryTolerance)
        {
            from = -width / 2;
            to = width / 2;
            return true;
        }

        from = double.MaxValue;
        to = double.MinValue;
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
        {
            double along = (box.Transform.OfPoint(new XYZ(x, y, z)) - center).DotProduct(direction);
            from = Math.Min(from, along);
            to = Math.Max(to, along);
        }

        return to - from > GeometryTolerance;
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

        List<Solid> solids = GetSolids(geometry).ToList();

        // A compound wall returns one solid per layer; merge them so only its outer faces are traced
        bool isWall = element is Wall;
        if (isWall && solids.Count > 1)
        {
            solids = MergeSolids(solids);
            summary.LayeredWalls++;
        }

        List<List<Curve>> sections = solids.Select(solid => SectionSolid(solid, localCutPlane, summary)).ToList();

        // On the cut plane, an outline edge belongs to exactly one face. An edge found twice is shared
        // by two faces inside the element: layer interfaces, both when the layers stay separate solids
        // and when the merged solid keeps one cut face per layer. Those are left out.
        HashSet<string> interiorEdges = sections
            .SelectMany(section => section.Select(GenerateEdgeKey))
            .GroupBy(key => key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        bool hasSolids = solids.Any();
        bool traced = false;
        foreach (Curve sectionCurve in sections.SelectMany(section => section))
        {
            traced = true;
            if (interiorEdges.Contains(GenerateEdgeKey(sectionCurve))) continue;
            DrawTracedCurve(sectionCurve.CreateTransformed(toHost), range.ViewPlane, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
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

    // Unions the solids into as few as possible. A solid that fails to union (e.g. it only touches
    // at an edge) is kept separately, so nothing is lost from the trace.
    private static List<Solid> MergeSolids(List<Solid> solids)
    {
        if (solids.Count < 2) return solids;

        Solid merged = solids[0];
        var separate = new List<Solid>();

        for (int i = 1; i < solids.Count; i++)
        {
            try
            {
                Solid union = BooleanOperationsUtils.ExecuteBooleanOperation(merged, solids[i], BooleanOperationsType.Union);
                if (union != null && union.Volume > 0)
                    merged = union;
                else
                    separate.Add(solids[i]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to merge wall layers: {ex.Message}");
                separate.Add(solids[i]);
            }
        }

        separate.Insert(0, merged);
        return separate;
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

    // Cameras in the view whose coverage no longer matches, typically because this trace changed
    // the Boundary lines near them (see CoverageStatus). They are reported, never redrawn here.
    private static int CountCamerasNeedingReview(Document doc, View view)
    {
        return ElementTagStorage.FindCoverageByCamera(doc, view)
            .Select(group => CoverageStatus.Evaluate(doc, view, doc.GetElement(group.Key), group.Value))
            .Count(s => s.State == CoverageState.NeedsReview || s.State == CoverageState.Stale);
    }

    private static void ShowTraceSummary(TraceSummary summary, TracingRules rules, int camerasNeedingReview)
    {
        var items = new List<MessageDialog.Item>();

        items.Add(new MessageDialog.Item("Tracing rules", rules.Describe() + " Change them in Settings."));

        if (camerasNeedingReview > 0)
            items.Add(new MessageDialog.Item("Camera coverage",
                $"{camerasNeedingReview} cameras in this view have coverage drawn against the old Boundary lines. Use the coverage check in the title bar to find them, then Update each one."));

        if (summary.LinesReplaced > 0)
            items.Add(new MessageDialog.Item("Previous trace replaced",
                $"{summary.LinesReplaced} lines from the last trace were removed. Boundary lines you drew yourself were kept."));

        if (!rules.ClosedOpeningCategories().Any())
        {
            items.Add(new MessageDialog.Item("Doors and windows", "Both are set to open, so their openings were left open."));
        }
        else if (summary.OpeningsCollected == 0)
        {
            items.Add(new MessageDialog.Item("Doors and windows", "No closed door or window categories have elements in this view or the chosen linked models."));
        }
        else if (summary.OpeningsFound == 0)
        {
            items.Add(new MessageDialog.Item("Doors and windows",
                $"{summary.OpeningsCollected} doors and windows were found, but none are within this view’s range (they are on other floors)."));
        }
        else
        {
            string openings = $"{summary.OpeningsClosed} of {summary.OpeningsAtCut} door and window openings at the cut plane were closed with a line across them.";
            if (summary.OpeningsAtCut < summary.OpeningsFound)
                openings += $" {summary.OpeningsFound - summary.OpeningsAtCut} are above or below the cut plane, where the wall is already solid.";
            if (summary.OpeningsClosed < summary.OpeningsAtCut)
                openings += $" {summary.OpeningsAtCut - summary.OpeningsClosed} had no geometry to measure, or their line matched one already drawn.";
            items.Add(new MessageDialog.Item("Doors and windows", openings));
        }

        if (summary.LayeredWalls > 0)
            items.Add(new MessageDialog.Item("Layered walls",
                $"{summary.LayeredWalls} walls with several layers were traced as one outline."));

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

    // Endpoints plus midpoint, so the two halves of a circle (same endpoints) stay distinct.
    private string GenerateEdgeKey(Curve curve)
    {
        XYZ mid = curve.Evaluate(0.5, true);
        return $"{GenerateCurveHash(curve)}|{Math.Round(mid.X, 4)},{Math.Round(mid.Y, 4)},{Math.Round(mid.Z, 4)}";
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

    // Draws every selected DORI level as one operation: the new regions are created first and the
    // camera's previous coverage is removed only once all of them succeeded. Any failure rolls the
    // whole group back, so the camera keeps exactly the coverage (and parameters) it had. A success
    // becomes a single Revit undo step.
    private void DrawCoverage()
    {
        Document doc = _drawingTools.Document;
        View view = _drawingTools.View;

        List<DoriLayerConfig> layers = _doriLayers != null && _doriLayers.Any()
            ? _doriLayers.ToList()
            : new List<DoriLayerConfig> { new DoriLayerConfig { Distance = _maxDistance, TypeId = _filledRegionTypeId, DrawDimension = _drawAngularDimension } };

        // Checked before anything changes, so a missing type never costs existing coverage
        List<DoriLayerConfig> missingTypes = layers.Where(l => l.TypeId == null || !(doc.GetElement(l.TypeId) is FilledRegionType)).ToList();
        if (missingTypes.Any())
        {
            MessageDialog.ShowWarning(
                "Can’t draw the coverage",
                "A DORI filled region type is missing from the project, so nothing was changed. Open Settings and click Create DORI region types, then draw again.");
            return;
        }

        // The association is stored on the regions themselves, so it survives reopening the window
        // or the project. Collected before drawing, because the new regions carry the same tags.
        // Untagged regions from older versions are never deleted.
        List<ElementId> previousCoverage = layers
            .SelectMany(l => ElementTagStorage.FindCoverageRegions(doc, view, _cameraElement, l.TypeId))
            .Concat(ElementTagStorage.FindFovDimensions(doc, view, _cameraElement))
            .Distinct()
            .ToList();

        var created = new List<ElementId>();
        DoriLayerConfig smallestLayer = layers.OrderBy(l => l.Distance).First(); // Carries the dimension
        ElementId smallestRegionId = null;
        string dimensionFailure = null;

        using (TransactionGroup group = new TransactionGroup(doc, "Draw Camera Coverage"))
        {
            try
            {
                group.Start();

                if (_cameraElement != null)
                {
                    UpdateCameraParameter();
                    AlignCameraSymbol(doc, view);
                }

                // What this coverage is drawn from, stored on every region so a later check can tell
                // whether it still matches the camera (issue #9). Captured after the parameter write.
                double reach = layers.Max(l => l.Distance) / 0.3048;
                string cameraState = _cameraElement != null
                    ? CoverageSource.WithDrawnValues(
                        CoverageSource.MergeDrawnState(_cameraStateAtSelection, CoverageSource.CaptureCameraState(_cameraElement)),
                        _rotationAngle, _fovAngle, _cameraResolution)
                    : null;
                string boundaryState = _cameraElement != null
                    ? CoverageSource.CaptureBoundaryState(doc, view, _position, reach)
                    : null;

                foreach (DoriLayerConfig layer in layers)
                {
                    bool carriesDimension = layer == smallestLayer && IsFovDimensionEnabled && _fovAngle < 180;
                    ElementId id = DrawLayer(layer.Distance, layer.TypeId, carriesDimension ? FovDimensionLegLengthMm / 304.8 : 0,
                        region =>
                        {
                            if (cameraState != null)
                                ElementTagStorage.TagCoverageSource(region, cameraState, boundaryState, reach);
                        });
                    if (id == ElementId.InvalidElementId)
                    {
                        group.RollBack();
                        MessageDialog.Show(
                            MessageDialog.Kind.Warning,
                            $"Couldn’t draw the {DescribeLayer(doc, layer.TypeId)} coverage",
                            "Nothing was changed: the camera keeps its previous coverage. This usually happens when Boundary lines create a very complex or self-intersecting shape near the camera. Tidy the Boundary lines around the camera, or move Region resolution towards Faster in Settings, then draw again.",
                            details: _drawingTools.LastFailure);
                        return;
                    }
                    created.Add(id);
                    if (layer == smallestLayer)
                        smallestRegionId = id;
                }

                if (previousCoverage.Any())
                {
                    using (Transaction transaction = new Transaction(doc, "Remove Previous Coverage"))
                    {
                        transaction.Start();
                        // One by one: deleting a region also deletes dimensions attached to it
                        foreach (ElementId id in previousCoverage)
                        {
                            if (doc.GetElement(id) != null)
                                doc.Delete(id);
                        }
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Removing the previous coverage could not be committed.");
                    }
                }

                // The dimension is a finishing touch: if it can't be placed, the coverage is still kept
                ElementId dimensionId = PlaceFovDimension(doc, view, smallestRegionId, smallestLayer, out dimensionFailure);
                if (dimensionId != null)
                    created.Add(dimensionId);

                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("The coverage could not be committed.");
            }
            catch (Exception ex)
            {
                if (group.HasStarted() && !group.HasEnded())
                    group.RollBack();

                MessageDialog.ShowError(
                    "Couldn’t draw the coverage",
                    "Revit reported an error, so nothing was changed: the camera keeps its previous coverage.",
                    ex);
                return;
            }
        }

        _lastBatchCreatedIds = created;
        ReportCameraCoverage();

        if (dimensionFailure != null)
        {
            MessageDialog.Show(
                MessageDialog.Kind.Info,
                "Coverage drawn without the angle dimension",
                "The coverage was drawn, but the field-of-view dimension couldn’t be placed on the innermost region. Add it by hand if you need it.",
                details: dimensionFailure);
        }
    }

    private static bool IsFovDimensionEnabled => !string.IsNullOrWhiteSpace(SettingsManager.Settings.FovDimensionTypeName);

    // Leg length split off each wedge side at the camera on the dimensioned region. The dimension
    // attaches to these short pieces, so its witness lines run back to the camera instead of
    // stopping where the long side is nearest the arc. 0.8 mm is just above Revit's shortest
    // allowed line (about 0.78 mm); DrawingTools never goes below that limit.
    private const double FovDimensionLegLengthMm = 0.8;

    // Dimensions the field of view on the smallest (innermost) region, in its own transaction so a
    // failure only skips the dimension. Type and arc distance come from the global settings.
    // Returns the dimension id, or null with the reason.
    private ElementId PlaceFovDimension(Document doc, View view, ElementId regionId, DoriLayerConfig layer, out string failure)
    {
        failure = null;
        if (regionId == null || layer == null) return null;
        if (!IsFovDimensionEnabled) return null;
        if (_fovAngle >= 180) return null; // No wedge edges to dimension; nothing to report

        string typeName = SettingsManager.Settings.FovDimensionTypeName;
        double radius = SettingsManager.Settings.FovDimensionDistanceMeters / 0.3048;
        if (radius <= 0) radius = 2.0 / 0.3048;

        List<DimensionType> angularTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(t => t.StyleType == DimensionStyleType.Angular)
            .ToList();

        DimensionType dimensionType =
            angularTypes.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
            ?? angularTypes.FirstOrDefault(t => t.Name.IndexOf("transparent", StringComparison.OrdinalIgnoreCase) < 0)
            ?? angularTypes.FirstOrDefault();

        if (dimensionType == null)
        {
            failure = "The project has no angular dimension type.";
            return null;
        }

        using (Transaction transaction = new Transaction(doc, "Dimension Camera Angle"))
        {
            try
            {
                transaction.Start();

                // Same parameters the region was drawn with, so the apex and wedge match it
                _drawingTools.SetParameters(_position, layer.Distance, _rotationAngle, _fovAngle, layer.TypeId);
                Dimension dimension = _drawingTools.CreateFovDimension(
                    doc.GetElement(regionId) as FilledRegion,
                    dimensionType,
                    radius);

                if (dimension == null)
                {
                    transaction.RollBack();
                    failure = _drawingTools.LastFailure;
                    return null;
                }

                ElementTagStorage.TagFovDimension(dimension, _cameraElement, view);

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    failure = "The dimension could not be committed.";
                    return null;
                }

                return dimension.Id;
            }
            catch (Exception ex)
            {
                if (transaction.HasStarted() && !transaction.HasEnded())
                    transaction.RollBack();
                failure = ex.Message;
                return null;
            }
        }
    }

    // Removes the regions (and dimension) of the last successful draw in one step. The coverage they
    // replaced is not restored here; Revit's own Undo does that.
    private void UndoLastCoverage()
    {
        Document doc = _drawingTools.Document;

        if (_lastBatchCreatedIds.Any(id => doc.GetElement(id) != null))
        {
            using (Transaction transaction = new Transaction(doc, "Undo Camera Coverage"))
            {
                transaction.Start();
                // One by one: deleting a region also deletes the dimension attached to it
                foreach (ElementId id in _lastBatchCreatedIds)
                {
                    if (doc.GetElement(id) != null)
                        doc.Delete(id);
                }
                transaction.Commit();
            }
        }

        _lastBatchCreatedIds.Clear();
    }

    // "Detection", "Observation", … for the plugin's region types, otherwise the type name.
    private static string DescribeLayer(Document doc, ElementId typeId)
    {
        string name = doc.GetElement(typeId)?.Name ?? "DORI";
        if (name.Contains("dori_25px")) return "Detection";
        if (name.Contains("dori_63px")) return "Observation";
        if (name.Contains("dori_125px")) return "Recognition";
        if (name.Contains("dori_250px")) return "Identification";
        return name;
    }

    // Creates one tagged region in its own transaction; the caller decides what to replace.
    // apexLegLength > 0 splits that length off each wedge side at the camera (see FovDimensionLegLengthMm).
    // tagSource runs with the new region inside its transaction.
    private ElementId DrawLayer(double distance, ElementId typeId, double apexLegLength, Action<FilledRegion> tagSource)
    {
        View view = _drawingTools.View;

        _drawingTools.SetParameters(_position, distance, _rotationAngle, _fovAngle, typeId);
        return _drawingTools.DrawFilledRegion( // Use slider resolution
            _sliderResolution,
            region =>
            {
                ElementTagStorage.TagCoverageRegion(region, _cameraElement, typeId, view);
                tagSource?.Invoke(region);
            },
            apexLegLength);
    }

    // The camera family turns its 2D symbol by the rotation parameter in the opposite sense to the
    // coverage, until the family is flipped. So after the rotation is written, the symbol's actual
    // direction in the view is measured and, when it points away from the coverage, the family is
    // flipped (up/down first, then left/right). A flip is kept only if it clearly brings the symbol
    // closer to the coverage; otherwise nothing changes. A flip never affects the coverage itself
    // or its out-of-date check.
    private void AlignCameraSymbol(Document doc, View view)
    {
        if (!SettingsManager.Settings.AutoFlipCameraSymbol) return;
        if (!(_cameraElement is FamilyInstance camera)) return;
        if (!camera.CanFlipFacing && !camera.CanFlipHand) return;

        double radians = _rotationAngle * Math.PI / 180.0;
        XYZ coverageDirection = new XYZ(Math.Cos(radians), Math.Sin(radians), 0);

        double? alignment = MeasureSymbolAlignment(camera, view, coverageDirection);
        if (alignment == null || alignment >= 0.5) return; // Within 60°: already matches

        using (Transaction transaction = new Transaction(doc, "Flip Camera Symbol"))
        {
            transaction.Start();

            var flips = new List<Action>();
            if (camera.CanFlipFacing) flips.Add(() => camera.flipFacing());
            if (camera.CanFlipHand) flips.Add(() => camera.flipHand());

            foreach (Action flip in flips)
            {
                flip();
                doc.Regenerate();

                double? after = MeasureSymbolAlignment(camera, view, coverageDirection);
                if (after != null && after > alignment + 0.25)
                {
                    transaction.Commit();
                    return;
                }

                flip(); // Didn't help: flip back
                doc.Regenerate();
            }

            transaction.RollBack();
        }
    }

    // Cosine of the angle between the camera's 2D symbol, as drawn in the view, and the coverage direction.
    private static double? MeasureSymbolAlignment(FamilyInstance camera, View view, XYZ coverageDirection)
    {
        return CameraSymbol.GetDirection(camera, view)?.DotProduct(coverageDirection);
    }

    // Tells the window whether the selected camera's coverage in this view is still up to date.
    private void ReportCameraCoverage()
    {
        if (_cameraElement == null || _mainWindow == null) return;

        View view = _drawingTools.View;
        Document doc = _drawingTools.Document;
        List<ElementId> regions = ElementTagStorage.FindCameraCoverage(doc, view, _cameraElement);
        _mainWindow.ShowCoverageStatus(CoverageStatus.Evaluate(doc, view, _cameraElement, regions));
    }

    // Reviews every camera with generated coverage in the view, lists the ones needing attention and
    // selects those cameras in Revit. Nothing is redrawn automatically.
    private void CheckViewCoverage()
    {
        Document doc = _drawingTools.Document;
        View view = _drawingTools.View;

        var statuses = ElementTagStorage.FindCoverageByCamera(doc, view)
            .Select(group => CoverageStatus.Evaluate(doc, view, doc.GetElement(group.Key), group.Value))
            .Where(s => s.State != CoverageState.None)
            .ToList();

        if (!statuses.Any())
        {
            MessageDialog.ShowInfo(
                "No coverage to check",
                $"No camera coverage drawn by Camera FOV was found in “{view.Name}”.");
            return;
        }

        var attention = statuses.Where(s => s.State != CoverageState.Current).ToList();
        if (!attention.Any())
        {
            MessageDialog.ShowSuccess(
                "All coverage is up to date",
                $"The coverage of all {statuses.Count} cameras in “{view.Name}” matches the cameras and the nearby Boundary lines.");
            return;
        }

        var items = attention
            .OrderBy(s => s.State)
            .Select(s => new MessageDialog.Item(DescribeCamera(s), DescribeState(s)))
            .ToList();

        List<ElementId> cameras = attention.Where(s => s.Camera != null).Select(s => s.Camera.Id).ToList();
        if (cameras.Any() && _uiDoc != null)
            _uiDoc.Selection.SetElementIds(cameras);

        MessageDialog.ShowWarning(
            $"{attention.Count} of {statuses.Count} cameras need attention",
            (cameras.Any() ? "Those cameras are now selected in Revit. " : string.Empty) +
            "Select one in the Camera FOV window and press Update to redraw it. Changes in linked models only count once the walls are traced again.",
            items);
    }

    private static string DescribeCamera(CoverageStatus status) => CameraData.Describe(status.Camera);

    // Which cameras see a picked point and how well (issue #7). Read-only.
    private void ReportPointCoverage(XYZ point)
    {
        if (point == null) return;

        Document doc = _drawingTools.Document;
        View view = _drawingTools.View;
        List<PointResult> results = PointCoverage.Evaluate(doc, view, point);

        if (!results.Any())
        {
            MessageDialog.ShowInfo(
                "No cameras to check",
                $"No cameras (security devices with the “{SettingsManager.Settings.ParameterName_UserRotation}” parameter) are visible in “{view.Name}”.");
            return;
        }

        List<PointResult> covering = results
            .Where(r => r.Verdict == PointVerdict.Covered)
            .OrderByDescending(r => r.PixelsPerMeter)
            .ToList();

        // The nearest cameras that don't cover it, with their reason; far-away ones would only add noise
        const int maxOthers = 12;
        List<PointResult> others = results
            .Where(r => r.Verdict != PointVerdict.Covered)
            .OrderBy(r => r.Verdict == PointVerdict.Unknown ? 0 : 1)
            .ThenBy(r => r.DistanceMeters)
            .ToList();

        var items = covering.Concat(others.Take(maxOthers))
            .Select(r => new MessageDialog.Item(
                CameraData.Describe(r.Camera),
                r.Reason + (r.CoverageState == CoverageState.Stale ? " Its drawn coverage is out of date." : string.Empty)))
            .ToList();

        string heading = covering.Any()
            ? $"{covering[0].Level.Name} coverage here"
            : "No camera covers this point";
        string message = covering.Any()
            ? $"{covering.Count} of {results.Count} cameras cover this point; the best reaches {covering[0].PixelsPerMeter:0} px/m."
            : $"None of the {results.Count} cameras in this view reaches Detection density here.";
        if (others.Count > maxOthers)
            message += $" {others.Count - maxOthers} more distant cameras aren’t listed.";
        message += SettingsManager.Settings.UseMountingGeometry
            ? $" Camera height and tilt are included, for a target {SettingsManager.Settings.TargetHeightMeters:0.0#} m above the floor."
            : " Plan-view check: camera height and tilt are switched off in Settings.";

        MessageDialog.Show(covering.Any() ? MessageDialog.Kind.Info : MessageDialog.Kind.Warning, heading, message, items);
    }

    // Collects the view's coverage and rooms and opens the read-only audit window (issue #6).
    private void OpenCoverageAudit()
    {
        Document doc = _drawingTools.Document;
        if (!(_drawingTools.View is ViewPlan view))
        {
            MessageDialog.ShowWarning("The audit needs a plan view", "Open Camera FOV in a floor plan to audit its coverage.");
            return;
        }

        TraceRange range = GetTraceRange(view);
        AuditData data = CoverageAudit.Collect(doc, view, range?.CutZ ?? (view.GenLevel?.ProjectElevation ?? 0) + 4);

        if (!data.Regions.Any() && !data.Cameras.Any())
        {
            MessageDialog.ShowInfo(
                "Nothing to audit",
                $"No cameras or camera coverage were found in “{view.Name}”." +
                (data.UntaggedDoriRegions > 0 ? $" {data.UntaggedDoriRegions} DORI regions aren’t linked to a camera; redraw them to include them." : string.Empty));
            return;
        }

        // How the cameras record (issue #19), saved back to the project from the storage window
        data.RecordingPlanJson = RecordingPlanStorage.Load(doc);
        MainWindow owner = _mainWindow;
        data.SaveRecordingPlan = json => owner?.SaveRecordingPlan(json);

        var window = new CoverageAuditWindow(data);
        if (_mainWindow != null && _mainWindow.IsVisible) window.Owner = _mainWindow;
        window.Show();
    }

    public static string DescribeState(CoverageStatus status)
    {
        switch (status.State)
        {
            case CoverageState.Current:
                return "Coverage is up to date.";
            case CoverageState.Stale:
                return $"Out of date: {string.Join(", ", status.Changes).ToLowerInvariant()} changed since it was drawn.";
            case CoverageState.NeedsReview:
                return "Boundary lines near the camera changed since it was drawn. Review it, or update to redraw.";
            case CoverageState.Unknown:
                return "Drawn by an older version, so changes can’t be tracked. Update to redraw it.";
            case CoverageState.CameraMissing:
                return $"The camera was deleted; {status.Regions.Count} coverage regions are left in the view.";
            default:
                return "No coverage in this view.";
        }
    }

    public string GetName()
    {
        return "Drawing Event Handler";
    }
}
}
