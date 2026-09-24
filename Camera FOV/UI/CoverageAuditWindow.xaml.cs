using Camera_FOV.Models;
using Camera_FOV.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Camera_FOV.UI
{
    /// <summary>
    /// Read-only review of a plan view's drawn coverage across all cameras (issue #6), one DORI level
    /// at a time or as the best level reached, against the rooms at the plan's cut height: uncovered,
    /// single-covered and overlapping areas. The map is drawn as vector geometry, so edges stay smooth
    /// at any zoom; a grid of the same data measures the areas. Works on plain data collected
    /// beforehand, so it never touches the model. The IEC categories mode (issue #12) instead works out
    /// the observation categories of IEC 62676-4:2026 from the cameras and Boundary lines themselves.
    /// </summary>
    public partial class CoverageAuditWindow : Window
    {
        // Canvas units are grid cells; the longest side has this many. Fine enough for the area figures,
        // small enough for large plans.
        private const int MaxCells = 1400;

        private readonly AuditData _data;
        private readonly Dictionary<string, AuditCamera> _cameras;
        private double _minX, _minY, _cell; // Grid origin and cell size, in feet
        private int _width, _height;
        private bool[] _inRoom;
        private int _level = -1; // DORI level index, -1 for the best level, or CategoryMode

        // The IEC 62676-4:2026 observation categories (issue #12), worked out from the cameras and
        // Boundary lines rather than the drawn regions. Cast when first shown, again if the formula changes.
        private const int CategoryMode = -2;
        private bool ShowingCategories => _level == CategoryMode;
        private List<CameraSight> _sights;
        private List<List<PlanPoint>> _sightOutlines; // Each sight's Overview outline
        private List<AuditCamera> _skipped;
        private PixelDensityFormula _sightFormula;

        private static readonly Color Uncovered = Color.FromRgb(0xE5, 0x48, 0x4D);
        private static readonly Color Single = Color.FromRgb(0x30, 0xB0, 0x5C);
        private static readonly Color Overlap = Color.FromRgb(0x3E, 0x8E, 0xF7);
        private static readonly Color BoundaryLine = Color.FromRgb(0x2E, 0xD1, 0x5E);
        private static readonly Color CategoryBoundaryLine = Color.FromRgb(0xFF, 0x4F, 0xD8); // Stands out from the green category ramp
        private static readonly Color[] LevelColors =
        {
            Color.FromRgb(0xF2, 0x8B, 0x82), Color.FromRgb(0xFD, 0xD6, 0x63),
            Color.FromRgb(0x8A, 0xB4, 0xF8), Color.FromRgb(0x81, 0xC9, 0x95)
        };
        // Overview to Scrutinise: one ordered ramp, light for the lowest density
        private static readonly Color[] CategoryColors =
        {
            Color.FromRgb(0xFD, 0xE7, 0x25), Color.FromRgb(0xB5, 0xDE, 0x2B), Color.FromRgb(0x6E, 0xCE, 0x58),
            Color.FromRgb(0x35, 0xB7, 0x79), Color.FromRgb(0x1F, 0x9E, 0x89), Color.FromRgb(0x26, 0x82, 0x8E),
            Color.FromRgb(0x3E, 0x4A, 0x89)
        };

        // Overlays whose on-screen size stays constant while zooming: element → base size factor
        private readonly List<(Shape Shape, double Thickness)> _lines = new List<(Shape, double)>();
        private readonly List<FrameworkElement> _markers = new List<FrameworkElement>();

        public CoverageAuditWindow(AuditData data)
        {
            InitializeComponent();
            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(Content as FrameworkElement);

            _data = data;
            _cameras = data.Cameras.ToDictionary(c => c.UniqueId);
            Title = $"Coverage audit · {data.ViewName}";

            SetUpGrid();
            LevelSelector.Children.OfType<RadioButton>().First(b => b.Tag?.ToString() == "-1").IsChecked = true; // Opens on Best level
        }

        private void EnsureSights()
        {
            if (_sights != null && _sightFormula == CameraData.CurrentFormula) return;

            _sightFormula = CameraData.CurrentFormula;
            _sights = CategoryCoverage.Cast(_data, _sightFormula, SettingsManager.Settings.Resolution, out _skipped);
            _sightOutlines = _sights.Select(s => s.Outline(CameraData.Categories[0].PixelsPerMeter)).ToList();
        }

        private IEnumerable<AuditRegion> IncludedRegions()
        {
            bool includeOutdated = IncludeOutdatedCheckBox.IsChecked == true;
            return _data.Regions.Where(r =>
            {
                if (!_cameras.TryGetValue(r.CameraUniqueId, out AuditCamera camera)) return false;
                switch (camera.State)
                {
                    case CoverageState.Current: return true;
                    case CoverageState.Stale:
                    case CoverageState.NeedsReview:
                    case CoverageState.Unknown: return includeOutdated;
                    default: return false; // Deleted cameras
                }
            });
        }

        private void SetUpGrid()
        {
            var points = _data.Regions.SelectMany(r => r.Loops).SelectMany(l => l)
                .Concat(_data.Rooms.SelectMany(room => room).SelectMany(l => l))
                .Concat(_data.BoundaryLines.SelectMany(l => l))
                .Concat(_data.Cameras.Where(c => c.Position.HasValue).Select(c => c.Position.Value))
                .ToList();

            if (!points.Any())
            {
                _width = _height = 1;
                _cell = 1;
                _inRoom = new bool[1];
                return;
            }

            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            double pad = Math.Max(maxX - minX, maxY - minY) * 0.02 + 1;
            _minX = minX - pad;
            _minY = minY - pad;
            _cell = Math.Max(maxX - minX + 2 * pad, maxY - minY + 2 * pad) / MaxCells;
            _width = Math.Max(1, (int)Math.Ceiling((maxX - minX + 2 * pad) / _cell));
            _height = Math.Max(1, (int)Math.Ceiling((maxY - minY + 2 * pad) / _cell));

            MapCanvas.Width = _width;
            MapCanvas.Height = _height;
            MapHost.Width = _width;
            MapHost.Height = _height;

            _inRoom = new bool[_width * _height];
            foreach (var room in _data.Rooms)
                Fill(room, index => _inRoom[index] = true);
        }

        private void Level_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton button && int.TryParse(button.Tag?.ToString(), out int level))
            {
                _level = level;
                Redraw();
            }
        }

        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded) Redraw();
        }

        private void Redraw()
        {
            // The category mode is worked out from the cameras themselves, so drawn coverage being out of date doesn't matter
            IncludeOutdatedCheckBox.IsEnabled = !ShowingCategories;
            BasisText.Text = ShowingCategories
                ? "Worked out in plan from each camera’s position, direction, field of view and resolution: camera height, tilt and anything not drawn as a Boundary line are not modelled. Nothing in the model is changed."
                : "Based on the drawn coverage in plan: camera height, tilt and anything not drawn as a Boundary line are not modelled. Nothing in the model is changed.";

            if (ShowingCategories)
            {
                EnsureSights();
                MeasureCategoryAreas();
                DrawCategoryMap();
                ShowCategorySources();
            }
            else
            {
                List<AuditRegion> regions = IncludedRegions().ToList();
                MeasureAreas(regions);
                DrawMap(regions);
                ShowSources();
            }

            // Keep the pin and refresh what covers it at the newly shown level
            DrawPin();
            if (_pin.HasValue) ShowPoint(_pin.Value);
        }

        // ------------------------------------------------------------------
        // Areas, measured on the grid
        // ------------------------------------------------------------------

        private void MeasureAreas(List<AuditRegion> regions)
        {
            int cells = _width * _height;
            double cellArea = _cell * _cell * 0.09290304; // ft² → m²
            bool hasRooms = _data.Rooms.Any();

            if (_level >= 0)
            {
                var counts = new int[cells];
                foreach (AuditRegion region in regions.Where(r => r.LevelIndex == _level))
                    Fill(region.Loops, index => counts[index]++);

                double roomArea = 0, uncovered = 0, single = 0, overlap = 0, covered = 0;
                for (int i = 0; i < cells; i++)
                {
                    if (counts[i] > 0) covered += cellArea;
                    if (!_inRoom[i]) continue;

                    roomArea += cellArea;
                    if (counts[i] == 0) uncovered += cellArea;
                    else if (counts[i] == 1) single += cellArea;
                    else overlap += cellArea;
                }

                string level = CameraData.Levels[_level].Name;
                int cameras = regions.Where(r => r.LevelIndex == _level).Select(r => r.CameraUniqueId).Distinct().Count();
                SummaryText.Text = hasRooms
                    ? $"{level} ({cameras} cameras): {Percent(single + overlap, roomArea)} of {roomArea:0} m² of rooms covered, " +
                      $"{overlap:0} m² by two or more cameras, {uncovered:0} m² uncovered."
                    : $"{level} ({cameras} cameras): {covered:0} m² covered. " +
                      "No rooms or spaces were found at this plan’s cut height, so gaps can’t be measured.";

                SetLegend(("Uncovered (in rooms)", Uncovered), ("One camera", Single), ("Two or more", Overlap), ("Boundary line", BoundaryLine));
            }
            else
            {
                var best = new int[cells]; // 0 = none, otherwise level index + 1
                foreach (AuditRegion region in regions)
                {
                    int value = region.LevelIndex + 1;
                    Fill(region.Loops, index => { if (best[index] < value) best[index] = value; });
                }

                var areas = new double[CameraData.Levels.Count + 1];
                double roomArea = 0;
                for (int i = 0; i < cells; i++)
                {
                    if (!_inRoom[i]) continue;
                    roomArea += cellArea;
                    areas[best[i]] += cellArea;
                }

                SummaryText.Text = hasRooms
                    ? "Best level in rooms: " + string.Join(", ", CameraData.Levels.Reverse().Select(l => $"{l.Name} {Percent(areas[l.Index + 1], roomArea)}")) +
                      $", uncovered {Percent(areas[0], roomArea)} of {roomArea:0} m²."
                    : "Best level reached anywhere in the drawn coverage. No rooms or spaces were found at this plan’s cut height, so gaps can’t be measured.";

                SetLegend(CameraData.Levels.Reverse().Select(l => (l.Name, LevelColors[l.Index]))
                    .Concat(new[] { ("Uncovered (in rooms)", Uncovered), ("Boundary line", BoundaryLine) }).ToArray());
            }
        }

        // Every grid cell takes the highest density of any camera that sees it, then the category that density reaches
        private void MeasureCategoryAreas()
        {
            int cells = _width * _height;
            double cellArea = _cell * _cell * 0.09290304; // ft² → m²
            var best = new double[cells];

            for (int s = 0; s < _sights.Count; s++)
            {
                CameraSight sight = _sights[s];
                PlanPoint camera = sight.Camera.Position.Value;
                Fill(new List<List<PlanPoint>> { _sightOutlines[s] }, index =>
                {
                    PlanPoint centre = CellCentre(index);
                    double dx = centre.X - camera.X, dy = centre.Y - camera.Y;
                    double density = sight.PixelsPerMeterAt(Math.Sqrt(dx * dx + dy * dy) * 0.3048);
                    if (density > best[index]) best[index] = density;
                });
            }

            var areas = new double[CameraData.Categories.Count + 1]; // 0 = below Overview or not seen, otherwise index + 1
            double roomArea = 0;
            for (int i = 0; i < cells; i++)
            {
                if (!_inRoom[i]) continue;
                roomArea += cellArea;
                areas[(CameraData.CategoryFor(best[i])?.Index ?? -1) + 1] += cellArea;
            }

            if (_data.Rooms.Any())
            {
                double high = CameraData.Categories.Where(c => c.HighDensity).Sum(c => areas[c.Index + 1]);
                SummaryText.Text = "Best category in rooms: " +
                    string.Join(", ", CameraData.Categories.Reverse().Select(c => $"{c.Name} {Percent(areas[c.Index + 1], roomArea)}")) +
                    $", below Overview or unseen {Percent(areas[0], roomArea)} of {roomArea:0} m². " +
                    $"Perceive or better (high pixel density) {Percent(high, roomArea)}.";
            }
            else
            {
                SummaryText.Text = "Best category any camera reaches. No rooms or spaces were found at this plan’s cut height, so areas can’t be measured.";
            }

            SetLegend(CameraData.Categories.Reverse().Select(c => ($"{c.Name} {c.PixelsPerMeter:0} px/m", CategoryColors[c.Index]))
                .Concat(new[] { ("Uncovered (in rooms)", Uncovered), ("Boundary line", CategoryBoundaryLine) }).ToArray());
        }

        private PlanPoint CellCentre(int index)
        {
            int row = index / _width, col = index % _width;
            return new PlanPoint(_minX + (col + 0.5) * _cell, _minY + (_height - 1 - row + 0.5) * _cell);
        }

        // ------------------------------------------------------------------
        // The map, drawn as vector geometry
        // ------------------------------------------------------------------

        private void ClearMap()
        {
            MapCanvas.Children.Clear();
            _lines.Clear();
            _markers.Clear();
            _pinShape = null;
            _pinLabel = null;
        }

        private void DrawMap(List<AuditRegion> regions)
        {
            ClearMap();
            Geometry rooms = Union(_data.Rooms.Select(ToGeometry));

            if (_level >= 0)
            {
                List<Geometry> shapes = regions.Where(r => r.LevelIndex == _level).Select(r => ToGeometry(r.Loops)).ToList();
                Geometry covered = Union(shapes);

                // Where two or more cameras overlap: the union of every pairwise intersection
                var overlaps = new List<Geometry>();
                for (int i = 0; i < shapes.Count; i++)
                for (int j = i + 1; j < shapes.Count; j++)
                {
                    if (!shapes[i].Bounds.IntersectsWith(shapes[j].Bounds)) continue;
                    Geometry both = Geometry.Combine(shapes[i], shapes[j], GeometryCombineMode.Intersect, null);
                    if (!both.IsEmpty()) overlaps.Add(both);
                }

                AddFill(covered, Single);
                AddFill(Union(overlaps), Overlap);
                if (rooms != null && covered != null) AddFill(Geometry.Combine(rooms, covered, GeometryCombineMode.Exclude, null), Uncovered);
                else if (rooms != null) AddFill(rooms, Uncovered);

                foreach (Geometry shape in shapes)
                    AddLine(shape, Color.FromArgb(150, 0xE8, 0xE8, 0xEC), 1);
            }
            else
            {
                // Lowest level first, so each spot shows the best level reaching it
                var ordered = regions.OrderBy(r => r.LevelIndex).ToList();
                foreach (AuditRegion region in ordered)
                    AddFill(ToGeometry(region.Loops), LevelColors[region.LevelIndex]);

                Geometry covered = Union(ordered.Select(r => ToGeometry(r.Loops)));
                if (rooms != null) AddFill(covered != null ? Geometry.Combine(rooms, covered, GeometryCombineMode.Exclude, null) : rooms, Uncovered);

                foreach (AuditRegion region in ordered)
                    AddLine(ToGeometry(region.Loops), LevelColors[region.LevelIndex], 1);
            }

            DrawOverlays(rooms);
        }

        // Rooms, Boundary lines and cameras over the coverage
        private void DrawOverlays(Geometry rooms)
        {
            // Outside the rooms, dim the coverage so the rooms stand out
            if (rooms != null)
            {
                var everything = new RectangleGeometry(new Rect(0, 0, _width, _height));
                Geometry outside = Geometry.Combine(everything, rooms, GeometryCombineMode.Exclude, null);
                var dim = (Color)((SolidColorBrush)FindResource("Surface.Inset")).Color;
                MapCanvas.Children.Add(new Path { Data = outside, Fill = new SolidColorBrush(Color.FromArgb(120, dim.R, dim.G, dim.B)), IsHitTestVisible = false });

                foreach (var room in _data.Rooms)
                    AddLine(ToGeometry(room), ((SolidColorBrush)FindResource("Text.Tertiary")).Color, 1.2);
            }

            // Obstructions, as the coverage sees them
            foreach (List<PlanPoint> line in _data.BoundaryLines)
                AddLine(ToPolyline(line), ShowingCategories ? CategoryBoundaryLine : BoundaryLine, 1.4);

            foreach (AuditCamera camera in _data.Cameras.Where(c => c.Position.HasValue))
                AddCamera(camera);

            ApplyZoomToOverlays();
        }

        // Lowest category first, so each spot shows the best category any camera reaches there. The
        // bands can reach far past the plan, so they are clipped to the map.
        private void DrawCategoryMap()
        {
            ClearMap();
            Geometry rooms = Union(_data.Rooms.Select(ToGeometry));
            var map = new RectangleGeometry(new Rect(0, 0, _width, _height));
            map.Freeze();

            foreach (ObservationCategory category in CameraData.Categories)
            {
                foreach (CameraSight sight in _sights)
                    AddFill(ToGeometry(new List<List<PlanPoint>> { sight.Outline(category.PixelsPerMeter) }), CategoryColors[category.Index], map);
            }

            List<Geometry> seen = _sightOutlines.Select(o => ToGeometry(new List<List<PlanPoint>> { o })).ToList();
            Geometry covered = Union(seen);
            if (rooms != null) AddFill(covered != null ? Geometry.Combine(rooms, covered, GeometryCombineMode.Exclude, null) : rooms, Uncovered);

            foreach (Geometry outline in seen)
            {
                var path = new Path { Data = outline, Clip = map, Stroke = new SolidColorBrush(Color.FromArgb(150, 0xE8, 0xE8, 0xEC)), StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                MapCanvas.Children.Add(path);
                _lines.Add((path, 1));
            }

            DrawOverlays(rooms);
        }

        private void AddFill(Geometry geometry, Color color, Geometry clip = null)
        {
            if (geometry == null || geometry.IsEmpty()) return;
            MapCanvas.Children.Add(new Path { Data = geometry, Clip = clip, Fill = new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)), IsHitTestVisible = false });
        }

        private void AddLine(Geometry geometry, Color color, double thickness)
        {
            if (geometry == null || geometry.IsEmpty()) return;
            var path = new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(color),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            MapCanvas.Children.Add(path);
            _lines.Add((path, thickness));
        }

        private void AddCamera(AuditCamera camera)
        {
            Point center = ToCanvas(camera.Position.Value);

            // The category mode uses every camera it has values for; the other modes only up-to-date drawn coverage
            string fill, note;
            if (ShowingCategories)
            {
                bool used = CategoryCoverage.CanSee(camera);
                fill = used ? "Text.Primary" : "Status.Warning";
                note = used ? string.Empty : " (no field of view, resolution or direction: left out)";
            }
            else
            {
                fill = camera.State == CoverageState.Current ? "Text.Primary" : camera.State == CoverageState.None ? "Text.Tertiary" : "Status.Warning";
                note = camera.State == CoverageState.Current ? string.Empty : $" ({DescribeState(camera.State)})";
            }

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)FindResource(fill),
                Stroke = (Brush)FindResource("Surface.Inset"),
                StrokeThickness = 1.2,
                RenderTransformOrigin = new Point(0.5, 0.5),
                ToolTip = camera.Label + note
            };
            Canvas.SetLeft(dot, center.X - 4);
            Canvas.SetTop(dot, center.Y - 4);
            MapCanvas.Children.Add(dot);
            _markers.Add(dot);
        }

        private Geometry ToGeometry(List<List<PlanPoint>> loops)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (StreamGeometryContext context = geometry.Open())
            {
                foreach (List<PlanPoint> loop in loops.Where(l => l.Count >= 3))
                {
                    context.BeginFigure(ToCanvas(loop[0]), true, true);
                    context.PolyLineTo(loop.Skip(1).Select(ToCanvas).ToList(), true, true);
                }
            }
            geometry.Freeze();
            return geometry;
        }

        private Geometry ToPolyline(List<PlanPoint> points)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(ToCanvas(points[0]), false, false);
                context.PolyLineTo(points.Skip(1).Select(ToCanvas).ToList(), true, true);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Geometry Union(IEnumerable<Geometry> geometries)
        {
            Geometry result = null;
            foreach (Geometry geometry in geometries)
            {
                if (geometry == null || geometry.IsEmpty()) continue;
                result = result == null ? geometry : Geometry.Combine(result, geometry, GeometryCombineMode.Union, null);
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Zoom and pan: wheel zooms at the cursor, the held wheel pans, a wheel double-click resets
        // ------------------------------------------------------------------

        private const double MaxZoom = 40;
        private Point? _panStart;
        private Point _panOrigin;

        private double Zoom => MapZoom.ScaleX;

        private void MapFrame_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point anchor = e.GetPosition(MapCanvas); // Canvas units, before the zoom
            double zoom = Math.Max(1, Math.Min(MaxZoom, Zoom * (e.Delta > 0 ? 1.25 : 0.8)));

            // Keep the spot under the cursor in place: screen = point × zoom + pan
            MapPan.X += anchor.X * (Zoom - zoom);
            MapPan.Y += anchor.Y * (Zoom - zoom);
            MapZoom.ScaleX = MapZoom.ScaleY = zoom;
            if (zoom <= 1) ResetZoom();

            ApplyZoomToOverlays();
            e.Handled = true;
        }

        private void MapFrame_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;

            if (e.ClickCount == 2)
            {
                ResetZoom();
                ApplyZoomToOverlays();
            }
            else
            {
                _panStart = e.GetPosition(MapHost);
                _panOrigin = new Point(MapPan.X, MapPan.Y);
                MapFrame.CaptureMouse();
                MapFrame.Cursor = Cursors.ScrollAll;
            }
            e.Handled = true;
        }

        private void MapFrame_MouseMove(object sender, MouseEventArgs e)
        {
            if (_panStart == null) return;

            Point now = e.GetPosition(MapHost);
            MapPan.X = _panOrigin.X + (now.X - _panStart.Value.X);
            MapPan.Y = _panOrigin.Y + (now.Y - _panStart.Value.Y);
        }

        private void MapFrame_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle || _panStart == null) return;

            _panStart = null;
            MapFrame.ReleaseMouseCapture();
            MapFrame.Cursor = null;
            e.Handled = true;
        }

        private void ResetZoom()
        {
            MapZoom.ScaleX = MapZoom.ScaleY = 1;
            MapPan.X = MapPan.Y = 0;
        }

        // Lines, dots and the pin keep their on-screen size at any zoom
        private void ApplyZoomToOverlays()
        {
            double factor = BaseScale / Zoom;

            foreach (var (shape, thickness) in _lines)
                shape.StrokeThickness = thickness * factor;

            foreach (FrameworkElement marker in _markers)
                marker.RenderTransform = new ScaleTransform(factor, factor);

            if (_pinShape != null) _pinShape.RenderTransform = new ScaleTransform(factor, factor);
            if (_pinLabel != null)
            {
                _pinLabel.RenderTransform = new ScaleTransform(factor, factor);
                Point tip = ToCanvas(_pin.Value);
                Canvas.SetLeft(_pinLabel, tip.X + 11 * factor);
                Canvas.SetTop(_pinLabel, tip.Y - 25 * factor);
            }
        }

        // Canvas units per screen pixel at zoom 1: the canvas is scaled down to fit the frame
        private double BaseScale => Math.Max(1.0, Math.Max(_width, _height) / 700.0);

        // ------------------------------------------------------------------
        // The pin: the clicked spot, the cameras covering it and the best density there
        // ------------------------------------------------------------------

        private PlanPoint? _pin; // Kept when the level changes
        private Path _pinShape;
        private Border _pinLabel;

        private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Ellipse) return; // Camera dots show their tooltip instead

            Point position = e.GetPosition(MapCanvas);
            _pin = new PlanPoint(_minX + position.X * _cell, _minY + (_height - position.Y) * _cell);
            DrawPin();
            ShowPoint(_pin.Value);
        }

        private void DrawPin()
        {
            if (_pinShape != null) MapCanvas.Children.Remove(_pinShape);
            if (_pinLabel != null) MapCanvas.Children.Remove(_pinLabel);
            _pinShape = null;
            _pinLabel = null;
            if (_pin == null) return;

            Point tip = ToCanvas(_pin.Value);
            _pinShape = new Path
            {
                // "F0": even-odd fill, so the small circle is a hole in the pin head
                Data = Geometry.Parse("F0 M 0,0 C -2,-6 -8,-9 -8,-15 A 8,8 0 1 1 8,-15 C 8,-9 2,-6 0,0 Z M 0,-18 A 3,3 0 1 0 0.01,-18 Z"),
                Fill = (Brush)FindResource("Accent"),
                Stroke = Brushes.White,
                StrokeThickness = 1.2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_pinShape, tip.X);
            Canvas.SetTop(_pinShape, tip.Y);
            MapCanvas.Children.Add(_pinShape);

            _pinLabel = new Border
            {
                Background = (Brush)FindResource("Surface.Raised"),
                BorderBrush = (Brush)FindResource("Surface.Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 2),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = DescribePinDensity(_pin.Value),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Text.Primary")
                }
            };
            MapCanvas.Children.Add(_pinLabel);

            ApplyZoomToOverlays();
        }

        // The highest density any included camera reaches at the spot, at the shown level
        private string DescribePinDensity(PlanPoint point)
        {
            if (ShowingCategories)
            {
                var sees = SightsAt(point);
                if (!sees.Any()) return "Not seen";
                ObservationCategory category = CameraData.CategoryFor(sees[0].Density);
                return category != null ? $"{category.Name} · {sees[0].Density:0} px/m" : $"{sees[0].Density:0} px/m";
            }

            var hits = HitsAt(point);
            double best = -1;
            foreach (var hit in hits)
            {
                double? density = DensityAt(hit.Camera, point);
                if (density.HasValue) best = Math.Max(best, density.Value);
            }

            if (best >= 0) return $"{best:0} px/m";
            return hits.Any() ? "Covered" : "Not covered";
        }

        private List<(AuditCamera Camera, int Level)> HitsAt(PlanPoint point)
        {
            return IncludedRegions()
                .Where(r => _level < 0 || r.LevelIndex == _level)
                .Where(r => Contains(r.Loops, point))
                .GroupBy(r => r.CameraUniqueId)
                .Select(g => (_cameras[g.Key], g.Max(r => r.LevelIndex)))
                .OrderByDescending(h => h.Item2)
                .ToList();
        }

        private static double? DensityAt(AuditCamera camera, PlanPoint point)
        {
            if (!camera.Position.HasValue || !camera.Resolution.HasValue || !camera.FovDegrees.HasValue) return null;

            double dx = point.X - camera.Position.Value.X, dy = point.Y - camera.Position.Value.Y;
            double meters = Math.Sqrt(dx * dx + dy * dy) * 0.3048;
            return CameraData.PixelsPerMeter(camera.Resolution.Value, camera.FovDegrees.Value, meters);
        }

        // The cameras that see the spot, best density first: inside the area seen at Overview or better
        private List<(CameraSight Sight, double Meters, double Density)> SightsAt(PlanPoint point)
        {
            var result = new List<(CameraSight, double, double)>();
            for (int s = 0; s < _sights.Count; s++)
            {
                if (!Contains(new List<List<PlanPoint>> { _sightOutlines[s] }, point)) continue;

                PlanPoint camera = _sights[s].Camera.Position.Value;
                double dx = point.X - camera.X, dy = point.Y - camera.Y;
                double meters = Math.Sqrt(dx * dx + dy * dy) * 0.3048;
                result.Add((_sights[s], meters, _sights[s].PixelsPerMeterAt(meters)));
            }
            return result.OrderByDescending(r => r.Item3).ToList();
        }

        private void ShowCategoryPoint(PlanPoint point)
        {
            var sees = SightsAt(point);
            if (!sees.Any())
            {
                PointText.Text = $"No camera sees this spot at {CameraData.Categories[0].Name} ({CameraData.Categories[0].PixelsPerMeter:0} px/m) or better.";
                return;
            }

            var lines = sees.Select(s =>
            {
                ObservationCategory category = CameraData.CategoryFor(s.Density);
                return $"{s.Sight.Camera.Label}: {category?.Name ?? "below Overview"}, {s.Density:0} px/m at {s.Meters:0.0} m";
            });

            string heading = sees.Count == 1 ? "1 camera sees this spot" : $"{sees.Count} cameras see this spot";
            PointText.Text = heading + ":\n" + string.Join("\n", lines);
        }

        private void ShowPoint(PlanPoint point)
        {
            if (ShowingCategories)
            {
                ShowCategoryPoint(point);
                return;
            }

            var hits = HitsAt(point);

            if (!hits.Any())
            {
                PointText.Text = _level >= 0
                    ? $"No camera covers this spot at {CameraData.Levels[_level].Name}."
                    : "No camera covers this spot.";
                return;
            }

            var lines = hits.Select(h =>
            {
                string line = $"{h.Camera.Label}: {CameraData.Levels[h.Level].Name}";
                if (h.Camera.Position.HasValue)
                {
                    double dx = point.X - h.Camera.Position.Value.X, dy = point.Y - h.Camera.Position.Value.Y;
                    double meters = Math.Sqrt(dx * dx + dy * dy) * 0.3048;
                    double? density = DensityAt(h.Camera, point);
                    line += density.HasValue ? $", {density.Value:0} px/m at {meters:0.0} m" : $", {meters:0.0} m";
                }
                if (h.Camera.State != CoverageState.Current) line += $" ({DescribeState(h.Camera.State)})";
                return line;
            });

            string heading = hits.Count == 1 ? "1 camera covers this spot" : $"{hits.Count} cameras cover this spot";
            PointText.Text = heading + ":\n" + string.Join("\n", lines);
        }

        // ------------------------------------------------------------------
        // Text
        // ------------------------------------------------------------------

        private void ShowSources()
        {
            int current = _data.Cameras.Count(c => c.State == CoverageState.Current);
            int outdated = _data.Cameras.Count(c => c.State == CoverageState.Stale || c.State == CoverageState.NeedsReview);
            int untracked = _data.Cameras.Count(c => c.State == CoverageState.Unknown);
            int deleted = _data.Cameras.Count(c => c.State == CoverageState.CameraMissing);
            int undrawn = _data.Cameras.Count(c => c.State == CoverageState.None);

            var parts = new List<string> { $"{current} cameras with up-to-date coverage" };
            if (undrawn > 0) parts.Add($"{undrawn} without drawn coverage");
            if (outdated > 0) parts.Add($"{outdated} out of date or needing review");
            if (untracked > 0) parts.Add($"{untracked} drawn by an older version");
            if (deleted > 0) parts.Add($"{deleted} deleted cameras’ leftover coverage (never included)");
            if (_data.HiddenRegions > 0) parts.Add($"{_data.HiddenRegions} hidden DORI regions (not included)");
            if (_data.UntaggedDoriRegions > 0) parts.Add($"{_data.UntaggedDoriRegions} DORI regions not linked to a camera (not included)");

            string includedNote = IncludeOutdatedCheckBox.IsChecked == true
                ? "Out-of-date and older coverage is included."
                : "Only up-to-date coverage is shown.";
            string rooms = _data.LinkedRoomSources > 0 ? $" Rooms include {_data.LinkedRoomSources} linked models." : string.Empty;

            SourcesText.Text = string.Join(" · ", parts) + ". " + includedNote + rooms;
        }

        private void ShowCategorySources()
        {
            string text = $"Worked out from {_sights.Count} cameras and the view’s Boundary lines with the {CameraData.FormulaName(_sightFormula)} pixel density formula, " +
                          "not from the drawn DORI regions, so cameras count whether or not their coverage is drawn or up to date.";
            if (_skipped.Any())
                text += $" Left out, with no field of view, resolution or direction: {string.Join(", ", _skipped.Select(c => c.Label))}.";
            if (_data.LinkedRoomSources > 0)
                text += $" Rooms include {_data.LinkedRoomSources} linked models.";

            SourcesText.Text = text;
        }

        private static string DescribeState(CoverageState state)
        {
            switch (state)
            {
                case CoverageState.None: return "no coverage drawn";
                case CoverageState.Stale: return "coverage out of date";
                case CoverageState.NeedsReview: return "needs review";
                case CoverageState.Unknown: return "drawn by an older version";
                case CoverageState.CameraMissing: return "camera deleted";
                default: return "up to date";
            }
        }

        private void SetLegend(params (string Label, Color Color)[] entries)
        {
            LegendPanel.Children.Clear();
            foreach (var entry in entries)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
                item.Children.Add(new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(entry.Color)
                });
                item.Children.Add(new TextBlock { Text = entry.Label, Style = (Style)FindResource("Type.Caption"), VerticalAlignment = VerticalAlignment.Center });
                LegendPanel.Children.Add(item);
            }
        }

        private static string Percent(double part, double whole) => whole > 0 ? $"{100 * part / whole:0}%" : "–";

        // ------------------------------------------------------------------
        // Geometry helpers
        // ------------------------------------------------------------------

        private Point ToCanvas(PlanPoint p) => new Point((p.X - _minX) / _cell, _height - (p.Y - _minY) / _cell);

        // Even-odd scanline fill of a set of loops (a region with holes, or a room); calls cell for
        // every grid cell whose centre lies inside. Row 0 of the grid is the top of the map.
        private void Fill(List<List<PlanPoint>> loops, Action<int> cell)
        {
            if (loops == null || loops.Count == 0 || !loops.Any(l => l.Count > 0)) return;

            double loopMinY = loops.SelectMany(l => l).Min(p => p.Y);
            double loopMaxY = loops.SelectMany(l => l).Max(p => p.Y);
            int rowFrom = Math.Max(0, (int)Math.Floor((loopMinY - _minY) / _cell));
            int rowTo = Math.Min(_height - 1, (int)Math.Ceiling((loopMaxY - _minY) / _cell));

            var crossings = new List<double>();
            for (int row = rowFrom; row <= rowTo; row++)
            {
                double y = _minY + (row + 0.5) * _cell;
                crossings.Clear();

                foreach (List<PlanPoint> loop in loops)
                {
                    for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                    {
                        PlanPoint a = loop[i], b = loop[j];
                        if ((a.Y > y) != (b.Y > y))
                            crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }
                }

                crossings.Sort();
                int gridRow = _height - 1 - row;
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int from = Math.Max(0, (int)Math.Ceiling((crossings[k] - _minX) / _cell - 0.5));
                    int to = Math.Min(_width - 1, (int)Math.Floor((crossings[k + 1] - _minX) / _cell - 0.5));
                    for (int col = from; col <= to; col++)
                        cell(gridRow * _width + col);
                }
            }
        }

        private static bool Contains(List<List<PlanPoint>> loops, PlanPoint point)
        {
            bool inside = false;
            foreach (List<PlanPoint> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    PlanPoint a = loop[i], b = loop[j];
                    if ((a.Y > point.Y) != (b.Y > point.Y) &&
                        point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                        inside = !inside;
                }
            }
            return inside;
        }
    }
}
