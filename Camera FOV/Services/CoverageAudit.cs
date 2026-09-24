using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>A point in plan, in feet (Revit internal units).</summary>
    public struct PlanPoint
    {
        public double X;
        public double Y;

        public PlanPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    public sealed class AuditCamera
    {
        public string UniqueId;
        public string Label;
        public PlanPoint? Position;
        public int? Resolution;
        public double? FovDegrees;
        public double? AimDegrees; // Plan direction the camera faces, in degrees
        public CameraMount Mount;  // Height and tilt (issue #16)
        public ObservationCategory IntendedCategory; // What it is for (issue #14)
        public string RiskGrade;
        public double? FrameRate; // Frames per second (issue #18)
        public string Mark, FamilyName, TypeName, LevelName; // For the camera schedule (issue #15)
        public CoverageState State;
    }

    public sealed class AuditRegion
    {
        public string CameraUniqueId;
        public int LevelIndex;
        public List<List<PlanPoint>> Loops = new List<List<PlanPoint>>();
    }

    /// <summary>A room or space that needs an observation category (issue #13).</summary>
    public sealed class AuditZone
    {
        public string Name;
        public ObservationCategory Required;
        public List<List<PlanPoint>> Loops;
    }

    /// <summary>Plain data for the coverage audit window, so the window never touches the Revit API.</summary>
    public sealed class AuditData
    {
        public string ViewName;
        public List<AuditCamera> Cameras = new List<AuditCamera>();
        public List<AuditRegion> Regions = new List<AuditRegion>();
        public List<List<List<PlanPoint>>> Rooms = new List<List<List<PlanPoint>>>(); // room → loops → points
        public List<List<PlanPoint>> BoundaryLines = new List<List<PlanPoint>>(); // Obstructions, as open polylines
        public int HiddenRegions;
        public int UntaggedDoriRegions;
        public int LinkedRoomSources;
        public List<AuditZone> Zones = new List<AuditZone>();      // Rooms and spaces with a required category (issue #13)
        public List<string> UnreadableZones = new List<string>();  // Their required category isn't one of the categories
        public string ProjectName;
        public string RecordingPlanJson;                   // The project's recording plan (issue #19)
        public Action<string> SaveRecordingPlan;           // Writes it back to the project
    }

    /// <summary>
    /// Collects a plan view's generated coverage, cameras and rooms for the multi-camera audit (issue #6).
    /// Read-only: nothing in the model is created, changed or deleted.
    /// </summary>
    public static class CoverageAudit
    {
        public static AuditData Collect(Document doc, ViewPlan view, double cutZ)
        {
            var data = new AuditData { ViewName = view.Name, ProjectName = doc.Title };

            // Every filled region owned by the view, including hidden ones, so they can be counted
            var regions = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegion))
                .WhereElementIsNotElementType()
                .Where(r => r.OwnerViewId == view.Id)
                .ToList();

            Dictionary<string, List<ElementId>> byCamera = ElementTagStorage.FindCoverageByCamera(doc, view);
            var taggedIds = new HashSet<ElementId>(byCamera.Values.SelectMany(ids => ids));

            foreach (Element region in regions)
            {
                string typeName = doc.GetElement(region.GetTypeId())?.Name;
                DoriLevel level = CameraData.LevelForRegionType(typeName);
                if (level == null) continue; // Not a DORI region

                if (region.IsHidden(view)) { data.HiddenRegions++; continue; }
                if (!taggedIds.Contains(region.Id)) { data.UntaggedDoriRegions++; continue; }
            }

            foreach (var group in byCamera)
            {
                Element camera = doc.GetElement(group.Key);
                CoverageStatus status = CoverageStatus.Evaluate(doc, view, camera, group.Value);

                var auditCamera = new AuditCamera
                {
                    UniqueId = group.Key,
                    Label = CameraData.Describe(camera),
                    State = status.State
                };

                XYZ position = CoverageSource.GetCameraPosition(camera);
                if (position != null) auditCamera.Position = new PlanPoint(position.X, position.Y);
                if (camera != null) ReadCameraValues(camera, view, status, auditCamera);
                data.Cameras.Add(auditCamera);

                foreach (ElementId id in group.Value)
                {
                    if (!(doc.GetElement(id) is FilledRegion region)) continue;
                    DoriLevel level = CameraData.LevelForRegionType(doc.GetElement(region.GetTypeId())?.Name);
                    if (level == null) continue;

                    var auditRegion = new AuditRegion { CameraUniqueId = group.Key, LevelIndex = level.Index };
                    foreach (CurveLoop loop in region.GetBoundaries())
                        auditRegion.Loops.Add(Tessellate(loop.Cast<Curve>(), Transform.Identity));
                    data.Regions.Add(auditRegion);
                }
            }

            // Cameras with no coverage drawn in this view: the category mode works them out from their
            // parameters (issue #12), the same cameras the point check considers
            var withoutCoverage = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_SecurityDevices)
                .WhereElementIsNotElementType()
                .Where(e => CameraData.IsCamera(e) && !byCamera.ContainsKey(e.UniqueId));

            foreach (Element camera in withoutCoverage)
            {
                var auditCamera = new AuditCamera
                {
                    UniqueId = camera.UniqueId,
                    Label = CameraData.Describe(camera),
                    State = CoverageState.None
                };

                XYZ position = CoverageSource.GetCameraPosition(camera);
                if (position != null) auditCamera.Position = new PlanPoint(position.X, position.Y);
                ReadCameraValues(camera, view, null, auditCamera);
                data.Cameras.Add(auditCamera);
            }

            // Boundary lines (drawn or traced): what the coverage treats as blocking the view
            var boundaryLines = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(c => c.LineStyle?.Name == "Boundary");

            foreach (CurveElement line in boundaryLines)
            {
                Curve curve = line.GeometryCurve;
                if (curve == null) continue;

                IList<XYZ> points;
                try { points = curve.Tessellate(); }
                catch { continue; }
                if (points.Count >= 2)
                    data.BoundaryLines.Add(points.Select(p => new PlanPoint(p.X, p.Y)).ToList());
            }

            CollectRooms(doc, Transform.Identity, cutZ, data);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                int before = data.Rooms.Count;
                CollectRooms(linkDoc, link.GetTotalTransform(), cutZ, data);
                if (data.Rooms.Count > before) data.LinkedRoomSources++;
            }

            return data;
        }

        private static void ReadCameraValues(Element camera, View view, CoverageStatus status, AuditCamera auditCamera)
        {
            PointCoverage.CameraValues values = PointCoverage.GetCameraValues(camera, view, status);
            auditCamera.FovDegrees = values.FovDegrees;
            auditCamera.Resolution = values.Resolution;
            auditCamera.AimDegrees = values.AimDegrees;
            auditCamera.Mount = values.Mount;
            var (category, grade) = CameraPurpose.Read(camera);
            auditCamera.IntendedCategory = category;
            auditCamera.RiskGrade = grade;
            auditCamera.FrameRate = FrameRates.Read(camera);
            auditCamera.Mark = camera.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;
            auditCamera.TypeName = camera.Document.GetElement(camera.GetTypeId())?.Name ?? camera.Name;
            auditCamera.FamilyName = (camera as FamilyInstance)?.Symbol?.FamilyName ?? string.Empty;
            auditCamera.LevelName = camera.Document.GetElement(camera.LevelId) is Level level ? level.Name : string.Empty;
        }

        // Rooms and MEP spaces whose height range contains the plan's cut plane, with their boundaries
        // moved to the host. Both count as the area cameras should cover.
        private static void CollectRooms(Document doc, Transform toHost, double cutZ, AuditData data)
        {
            var options = new SpatialElementBoundaryOptions();
            var rooms = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces }))
                .WhereElementIsNotElementType()
                .OfType<SpatialElement>()
                .Where(r => r.Area > 0);

            foreach (SpatialElement room in rooms)
            {
                BoundingBoxXYZ box = room.get_BoundingBox(null);
                if (box == null) continue;

                double minZ = toHost.OfPoint(box.Min).Z, maxZ = toHost.OfPoint(box.Max).Z;
                if (cutZ < Math.Min(minZ, maxZ) - 1e-3 || cutZ > Math.Max(minZ, maxZ) + 1e-3) continue;

                IList<IList<BoundarySegment>> boundary;
                try { boundary = room.GetBoundarySegments(options); }
                catch { continue; }
                if (boundary == null || boundary.Count == 0) continue;

                var loops = boundary
                    .Select(segments => Tessellate(segments.Select(s => s.GetCurve()), toHost))
                    .Where(loop => loop.Count >= 3)
                    .ToList();
                if (!loops.Any()) continue;
                data.Rooms.Add(loops);

                // A required observation category marks the room as a zone to check (issue #13)
                string required = room.LookupParameter(SettingsManager.Settings.ParameterName_RequiredCategory ?? string.Empty)?.AsString();
                if (string.IsNullOrWhiteSpace(required)) continue;

                string name = string.Join(" ", new[] { room.Number, room.Name }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                ObservationCategory category = CameraPurpose.ParseCategory(required);
                if (category == null) data.UnreadableZones.Add($"{name} (“{required.Trim()}”)");
                else data.Zones.Add(new AuditZone { Name = name, Required = category, Loops = loops });
            }
        }

        private static List<PlanPoint> Tessellate(IEnumerable<Curve> curves, Transform toHost)
        {
            var points = new List<PlanPoint>();
            foreach (Curve curve in curves)
            {
                IList<XYZ> tessellated;
                try { tessellated = curve.Tessellate(); }
                catch { continue; }

                // Each curve's end is the next one's start; skip it to avoid duplicates
                for (int i = 0; i < tessellated.Count - 1; i++)
                {
                    XYZ p = toHost.OfPoint(tessellated[i]);
                    points.Add(new PlanPoint(p.X, p.Y));
                }
            }
            return points;
        }
    }
}
