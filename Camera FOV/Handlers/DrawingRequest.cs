using Autodesk.Revit.DB;
using Camera_FOV.Utils;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Handlers
{
    // A snapshot of one UI action, captured when the user acts and validated when Revit runs it.
    // Nothing here changes after construction, so a later action can never alter a queued one.
    public sealed class DrawingRequest
    {
        public DrawingEventHandler.DrawingAction Action { get; }
        public DrawingTools DrawingTools { get; }
        public Document Document { get; }
        public ElementId ViewId { get; }
        public ElementId CameraId { get; }
        public XYZ Position { get; }
        public double MaxDistance { get; }
        public double RotationAngle { get; }
        public double FovAngle { get; }
        public ElementId FilledRegionTypeId { get; }
        public double SliderResolution { get; }
        public double UserRotation { get; }
        public IReadOnlyList<DoriLayerConfig> DoriLayers { get; }
        public bool DrawAngularDimension { get; }

        // The camera's state when it was selected in the window (see CoverageSource.MergeDrawnState).
        public string CameraStateAtSelection { get; }

        // Rules to store in the project (SaveTracingRules only)
        public Models.TracingRules TracingRules { get; }

        // Horizontal resolution (pixels) the DORI distances were calculated with (DrawFilledRegion)
        public int CameraResolution { get; }

        public DrawingRequest(
            DrawingEventHandler.DrawingAction action,
            DrawingTools drawingTools,
            Element camera = null,
            XYZ position = null,
            double maxDistance = 0,
            double rotationAngle = 0,
            double fovAngle = 90,
            ElementId filledRegionTypeId = null,
            double sliderResolution = 1.0,
            double userRotation = 0,
            IEnumerable<DoriLayerConfig> doriLayers = null,
            bool drawAngularDimension = false,
            string cameraStateAtSelection = null,
            Models.TracingRules tracingRules = null,
            int cameraResolution = 0)
        {
            CameraStateAtSelection = cameraStateAtSelection;
            TracingRules = tracingRules;
            CameraResolution = cameraResolution;
            Action = action;
            DrawingTools = drawingTools;
            Document = drawingTools.Document;
            ViewId = drawingTools.View.Id;
            CameraId = camera?.Id;
            Position = position;
            MaxDistance = maxDistance;
            RotationAngle = rotationAngle;
            FovAngle = fovAngle;
            FilledRegionTypeId = filledRegionTypeId;
            SliderResolution = sliderResolution;
            UserRotation = userRotation;
            DoriLayers = (doriLayers ?? Enumerable.Empty<DoriLayerConfig>())
                .Select(l => new DoriLayerConfig { Distance = l.Distance, TypeId = l.TypeId, DrawDimension = l.DrawDimension })
                .ToList()
                .AsReadOnly();
            DrawAngularDimension = drawAngularDimension;
        }

        // Live previews can be merged; only the newest one matters.
        public bool IsPreview => Action == DrawingEventHandler.DrawingAction.Update;

        // Requests that replace a waiting request of the same action: previews and automatic status checks.
        public bool IsCoalescable => IsPreview || Action == DrawingEventHandler.DrawingAction.CheckCoverage;

        // Requests the user didn't explicitly ask for: when skipped, nothing is reported.
        public bool IsSilent => IsCoalescable || Action == DrawingEventHandler.DrawingAction.Delete;

        // Window-close cleanup of the preview line may run in a project that is no longer active.
        public bool RequiresActiveDocument => Action != DrawingEventHandler.DrawingAction.Delete;

        // Actions that draw into the view or read its view range only run while that view is active.
        public bool RequiresActiveView =>
            Action == DrawingEventHandler.DrawingAction.Update ||
            Action == DrawingEventHandler.DrawingAction.Draw ||
            Action == DrawingEventHandler.DrawingAction.DrawFilledRegion ||
            Action == DrawingEventHandler.DrawingAction.TraceWallsAndDrawBoundary;

        public string Description
        {
            get
            {
                switch (Action)
                {
                    case DrawingEventHandler.DrawingAction.DrawFilledRegion: return "Drawing the camera coverage";
                    case DrawingEventHandler.DrawingAction.UndoFilledRegion: return "Undoing the last coverage";
                    case DrawingEventHandler.DrawingAction.UpdateCameraParameter: return "Updating the camera parameters";
                    case DrawingEventHandler.DrawingAction.CreateBoundaryLine: return "Creating the Boundary line style";
                    case DrawingEventHandler.DrawingAction.CreateFilledRegions: return "Creating the DORI filled region types";
                    case DrawingEventHandler.DrawingAction.TraceWallsAndDrawBoundary: return "Tracing the boundaries";
                    case DrawingEventHandler.DrawingAction.CheckViewCoverage: return "Checking the coverage";
                    case DrawingEventHandler.DrawingAction.SaveTracingRules: return "Saving the tracing rules";
                    case DrawingEventHandler.DrawingAction.CheckPoint: return "Checking the point";
                    case DrawingEventHandler.DrawingAction.CoverageAudit: return "The coverage audit";
                    default: return $"The {Action} action";
                }
            }
        }
    }
}
