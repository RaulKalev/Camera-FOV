using Camera_FOV.Models;
using Camera_FOV.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Camera_FOV.UI
{
    /// <summary>
    /// Documents made from the audit's data: the acceptance test plan (issue #17) and the camera
    /// schedule (issue #15), both as CSV files that Excel opens.
    /// </summary>
    public partial class CoverageAuditWindow
    {
        private sealed class TestPosition
        {
            public string Location;
            public PlanPoint Point;
            public AuditCamera Camera;
            public ObservationCategory Category;
            public double Density;
            public double SlantMeters;
            public string Why;
        }

        // ------------------------------------------------------------------
        // Acceptance test plan (issue #17)
        // ------------------------------------------------------------------

        private void ExportTestPlan()
        {
            int choice = MessageDialog.Ask(MessageDialog.Kind.Info,
                "Which categories should the test plan cover?",
                "The standard asks for on-site tests where Validate or Scrutinise is required (IEC 62676-4:2026, Annex B). " +
                "Test positions come from rooms with a required category and from cameras’ intended categories.",
                new[] { "Validate and Scrutinise", "All categories", "Cancel" }, this);
            if (choice == 2) return;

            CsvTable table = BuildTestPlan(choice == 0 ? 5 : 0, out string summary);
            if (table == null)
            {
                MessageDialog.ShowInfo("Nothing to test",
                    "No room needs, and no camera is intended for, the chosen categories, and no camera in this view reaches a category to sample. " +
                    $"Set rooms’ “{SettingsManager.Settings.ParameterName_RequiredCategory}” or cameras’ intended category first.", owner: this);
                return;
            }
            SaveTable(table, "test plan", summary);
        }

        private CsvTable BuildTestPlan(int lowest, out string summary)
        {
            summary = null;
            var positions = new List<TestPosition>();

            // Each zone: the spot with the lowest density that still meets its category, the most
            // demanding place that should pass. Without one, its best spot, expected to fail.
            double[] best = BestDensity();
            foreach (AuditZone zone in _data.Zones.Where(z => z.Required.Index >= lowest))
            {
                int weakestMet = -1, strongest = -1;
                Fill(zone.Loops, index =>
                {
                    if (best[index] >= zone.Required.PixelsPerMeter && (weakestMet < 0 || best[index] < best[weakestMet])) weakestMet = index;
                    if (strongest < 0 || best[index] > best[strongest]) strongest = index;
                });

                int cell = weakestMet >= 0 ? weakestMet : strongest;
                if (cell < 0)
                {
                    // Too small for the grid: still listed, so every required room has a position
                    positions.Add(new TestPosition { Location = zone.Name, Point = zone.Loops[0][0], Category = zone.Required, Why = "Room too small to measure: choose the position on site" });
                    continue;
                }
                PlanPoint point = CellCentre(cell);
                var seen = SightsAt(point).FirstOrDefault();
                positions.Add(new TestPosition
                {
                    Location = zone.Name,
                    Point = point,
                    Camera = seen.Sight?.Camera,
                    Category = zone.Required,
                    Density = seen.Sight != null ? seen.Density : 0,
                    SlantMeters = seen.Sight != null ? seen.Sight.SlantMeters(seen.Meters) : 0,
                    Why = weakestMet >= 0 ? "Lowest density in the room that still meets its required category"
                        : best[cell] > 0 ? "Shortfall: the best spot in the room, expected to fail"
                        : "No camera sees this room"
                });
            }

            // Each camera with an intended category: where that category ends along its axis
            foreach (CameraSight sight in _sights.Where(s => s.Camera.IntendedCategory != null && s.Camera.IntendedCategory.Index >= lowest))
                AddEdgePosition(positions, sight, sight.Camera.IntendedCategory, "Edge of the camera’s intended category along its axis");

            // Annex B: when not every camera is tested, at least five views or 20 %, whichever is larger
            int views = _sights.Count;
            int sample = Math.Min(views, Math.Max(5, (int)Math.Ceiling(views * 0.2)));
            var tested = new HashSet<string>(positions.Where(p => p.Camera != null).Select(p => p.Camera.UniqueId));
            foreach (CameraSight sight in _sights.Where(s => !tested.Contains(s.Camera.UniqueId)).OrderBy(s => s.Camera.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                if (tested.Count >= sample) break;
                ObservationCategory top = CameraData.Categories.Reverse().FirstOrDefault(sight.Reaches);
                if (top != null && sight.EdgeAlongAxis(top, out _).HasValue && AddEdgePosition(positions, sight, top, "Sample view (Annex B: at least 5 views or 20 %)"))
                    tested.Add(sight.Camera.UniqueId);
            }

            if (!positions.Any()) return null;

            var table = new CsvTable()
                .Title($"Acceptance test plan: {_data.ProjectName}, view {_data.ViewName}")
                .Title($"Made {DateTime.Now:g} with Camera FOV. Pixel density by {CameraData.FormulaName(_sightFormula)}, target height {SettingsManager.Settings.TargetHeightMeters:0.0#} m.")
                .Title($"{views} cameras in the view, {tested.Count} with a test position. When not all are tested, test at least {sample} (5 views or 20 %, whichever is larger; IEC 62676-4:2026, Annex B).")
                .Columns("No.", "Location", "X (m)", "Y (m)", "Camera", "Expected category", "Expected px/m", "Distance from lens (m)",
                         "Why here", "Test object", "Presented object", "Observed category", "Result (pass/fail)", "Tested by", "Date", "Notes");

            int number = 1;
            foreach (TestPosition p in positions)
            {
                table.Row(number++, p.Location, p.Point.X * 0.3048, p.Point.Y * 0.3048, p.Camera?.Label ?? "None",
                    $"{p.Category.Name} ({p.Category.PixelsPerMeter:0} px/m)", Math.Round(p.Density), p.SlantMeters,
                    p.Why, TestObject(p.Category), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            }

            summary = $"{positions.Count} test positions for {tested.Count} cameras.";
            return table;
        }

        private bool AddEdgePosition(List<TestPosition> positions, CameraSight sight, ObservationCategory category, string why)
        {
            PlanPoint? edge = sight.EdgeAlongAxis(category, out double plan);
            positions.Add(new TestPosition
            {
                Location = edge.HasValue ? ZoneAt(edge.Value)?.Name ?? "In front of the camera" : "Not reachable",
                Point = edge ?? sight.Camera.Position.Value,
                Camera = sight.Camera,
                Category = category,
                Density = edge.HasValue ? sight.PixelsPerMeterAt(plan) : 0,
                SlantMeters = edge.HasValue ? sight.SlantMeters(plan) : 0,
                Why = edge.HasValue ? why : $"{why}: the camera can’t reach it, so it will fail"
            });
            return edge.HasValue;
        }

        private static string TestObject(ObservationCategory category)
        {
            if (category.Index >= 5) return "Test face (Annex B)";
            if (category.Index >= 3) return "Person or test face";
            return "Person-sized target";
        }

        // ------------------------------------------------------------------
        // Camera schedule (issue #15)
        // ------------------------------------------------------------------

        private void ExportSchedule()
        {
            CsvTable table = BuildSchedule();
            SaveTable(table, "camera schedule", $"{table.RowCount} cameras.");
        }

        private CsvTable BuildSchedule()
        {
            var sights = _sights.ToDictionary(s => s.Camera.UniqueId);
            RecordingPlan plan = RecordingPlan.Parse(_data.RecordingPlanJson);
            var zoneAreas = _data.Zones.Select(z => (Zone: z, Geometry: ToGeometry(z.Loops))).ToList();

            var columns = new List<string> { "Mark", "Family", "Type", "Level", "X (m)", "Y (m)", "Resolution (px)", "Horizontal FOV (°)", "Direction (°)",
                                             "Mounting height (m)", "Tilt (°)", "Dead zone to (m)" };
            columns.AddRange(CameraData.Categories.Select(c => $"{c.Name} {c.PixelsPerMeter:0} px/m to (m)"));
            columns.AddRange(CameraData.Levels.Select(l => $"{l.Name} {l.PixelsPerMeter:0} px/m to (m)"));
            columns.AddRange(new[] { "Intended category", "Risk grade", "Intended reached", "Rooms seen (required category)", "Frame rate (fps)",
                                     $"Frame rate for {SettingsManager.Settings.MinFramesPerCrossing} frame(s) of a running person (fps)",
                                     "Coverage", "Recording group", "Recording storage (TB)" });

            var table = new CsvTable()
                .Title($"Camera schedule: {_data.ProjectName}, view {_data.ViewName}")
                .Title($"Made {DateTime.Now:g} with Camera FOV. Pixel density by {CameraData.FormulaName(_sightFormula)}, target height {SettingsManager.Settings.TargetHeightMeters:0.0#} m. " +
                       "Distances are in plan from the camera to where each category ends, not counting walls.")
                .Columns(columns.ToArray());

            foreach (AuditCamera camera in _data.Cameras.Where(c => c.State != CoverageState.CameraMissing)
                         .OrderBy(c => c.Mark, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                sights.TryGetValue(camera.UniqueId, out CameraSight sight);
                var cells = new List<object>
                {
                    camera.Mark, camera.FamilyName, camera.TypeName, camera.LevelName,
                    camera.Position.HasValue ? camera.Position.Value.X * 0.3048 : double.NaN,
                    camera.Position.HasValue ? camera.Position.Value.Y * 0.3048 : double.NaN,
                    camera.Resolution, camera.FovDegrees, camera.AimDegrees.HasValue ? NormalizeDegrees(camera.AimDegrees.Value) : double.NaN,
                    camera.Mount?.HeightMeters ?? double.NaN, camera.Mount?.TiltDegrees ?? double.NaN,
                    sight != null && sight.NearFeet > 0 ? (double.IsPositiveInfinity(sight.NearFeet) ? (object)"Never sees the target" : sight.NearFeet * 0.3048) : double.NaN
                };

                cells.AddRange(CameraData.Categories.Select(c => ReachMeters(sight, c.PixelsPerMeter)));
                cells.AddRange(CameraData.Levels.Select(l => ReachMeters(sight, l.PixelsPerMeter)));

                ObservationCategory intended = camera.IntendedCategory;
                cells.Add(intended?.Name ?? string.Empty);
                cells.Add(camera.RiskGrade ?? string.Empty);
                cells.Add(intended == null || sight == null ? (object)string.Empty : sight.Reaches(intended));
                cells.Add(sight != null ? RoomsSeen(sight, zoneAreas) : string.Empty);
                cells.Add(camera.FrameRate ?? double.NaN);
                cells.Add(sight != null ? NeededFrameRate(sight, intended) : double.NaN);
                cells.Add(DescribeState(camera.State));

                RecordingGroup group = plan.Find(camera.TypeName);
                string groupName = group != null ? group.Name : $"{camera.TypeName} (not planned: typical figures)";
                group = group ?? RecordingGroup.For(camera.TypeName, camera.Resolution ?? 0, camera.FrameRate);
                cells.Add(groupName);
                cells.Add(RecordingStorage.Estimate(group, plan.PublicHolidaysPerYear).TerabytesPerCamera);

                table.Row(cells.ToArray());
            }

            return table;
        }

        private static object ReachMeters(CameraSight sight, double pixelsPerMeter)
        {
            if (sight == null) return double.NaN;
            double feet = Math.Min(sight.DistanceFeet(pixelsPerMeter), sight.FarFeet);
            return feet > sight.NearFeet && feet > 0 && !double.IsInfinity(feet) ? feet * 0.3048 : (object)"Not reached";
        }

        // Rooms with a required category this camera sees part of, and whether it reaches the category there
        private string RoomsSeen(CameraSight sight, List<(AuditZone Zone, Geometry Geometry)> zones)
        {
            Geometry seen = ToGeometry(sight.Outline(CameraData.Categories[0].PixelsPerMeter));
            var parts = new List<string>();
            foreach (var (zone, area) in zones)
            {
                if (!seen.Bounds.IntersectsWith(area.Bounds) || Geometry.Combine(seen, area, GeometryCombineMode.Intersect, null).GetArea() < 1) continue;
                Geometry reached = ToGeometry(sight.Outline(zone.Required.PixelsPerMeter));
                bool meets = Geometry.Combine(reached, area, GeometryCombineMode.Intersect, null).GetArea() >= 1;
                parts.Add($"{zone.Name} ({zone.Required.Name} {(meets ? "reached in part or all" : "not reached")})");
            }
            return string.Join("; ", parts);
        }

        // The frame rate at which a running person crossing at the intended category's distance (or
        // where the camera's view reaches Overview) appears in the needed number of frames (9.2)
        private static double NeededFrameRate(CameraSight sight, ObservationCategory intended)
        {
            double feet = intended != null && sight.Reaches(intended)
                ? Math.Min(sight.DistanceFeet(intended.PixelsPerMeter), sight.FarFeet)
                : sight.OverviewFeet;
            if (!(feet > 0) || double.IsInfinity(feet)) return double.NaN;

            double width = CameraData.SceneWidthMeters(sight.Camera.FovDegrees.Value, sight.SlantMeters(feet * 0.3048), CameraData.CurrentFormula);
            return FrameRates.RequiredFps(width, SettingsManager.Settings.RunningSpeedKmh, Math.Max(1, SettingsManager.Settings.MinFramesPerCrossing));
        }

        private static double NormalizeDegrees(double degrees)
        {
            degrees %= 360;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        // ------------------------------------------------------------------
        // Saving
        // ------------------------------------------------------------------

        private void SaveTable(CsvTable table, string what, string summary)
        {
            string safeName = string.Join("_", $"{_data.ProjectName} - {_data.ViewName} - {what}".Split(System.IO.Path.GetInvalidFileNameChars()));
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save the {what}",
                FileName = safeName + ".csv",
                DefaultExt = ".csv",
                Filter = "CSV for Excel (*.csv)|*.csv"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                table.Save(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError($"Couldn’t save the {what}", "Check that the file isn’t open in Excel and that you can write to the folder.", ex, this);
                return;
            }

            int open = MessageDialog.Ask(MessageDialog.Kind.Success, $"The {what} is saved", $"{summary} {dialog.FileName}", new[] { "Open it", "Close" }, this);
            if (open == 0)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true }); }
                catch (Exception ex) { MessageDialog.ShowError("Couldn’t open the file", "Open it from the folder instead.", ex, this); }
            }
        }
    }
}
