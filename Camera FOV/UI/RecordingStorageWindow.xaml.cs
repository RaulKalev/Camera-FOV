using Camera_FOV.Models;
using Camera_FOV.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Camera_FOV.UI
{
    /// <summary>
    /// Recording storage for the cameras in a view (issue #19): one schedule per camera type, the
    /// storage each needs and the network load, by the formulas of IEC 62676-4:2026, chapter 10.
    /// The plan is kept in the project.
    /// </summary>
    public partial class RecordingStorageWindow : Window
    {
        private sealed class GroupEditor
        {
            public RecordingGroup Group;
            public int Cameras;
            public ComboBox Codec, Mode;
            public TextBox Motion, Day, Night, NightHours, FrameSize, FrameRate, Weekday, Saturday, Sunday, Retention;
            public FrameworkElement VideoFields, ImageFields;
            public TextBlock Result;
        }

        private static readonly string[] CodecNames = { "H.264", "H.265", "M-JPEG" };
        private readonly AuditData _data;
        private readonly RecordingPlan _plan;
        private readonly List<GroupEditor> _editors = new List<GroupEditor>();
        private bool _loading;

        public RecordingStorageWindow(AuditData data)
        {
            InitializeComponent();
            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

            _data = data;
            _plan = RecordingPlan.Parse(data.RecordingPlanJson);
            Title = $"Recording storage · {data.ViewName}";

            _loading = true;
            HolidaysBox.Text = _plan.PublicHolidaysPerYear.ToString(CultureInfo.InvariantCulture);
            HolidaysBox.TextChanged += (s, e) => Recalculate();

            // One group per Revit type among the view's cameras; saved schedules are reused by name
            var types = data.Cameras
                .Where(c => c.State != CoverageState.CameraMissing && !string.IsNullOrEmpty(c.TypeName))
                .GroupBy(c => c.TypeName)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var type in types)
            {
                int resolution = type.Select(c => c.Resolution ?? 0).GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key;
                double? fps = type.Select(c => c.FrameRate).FirstOrDefault(f => f > 0);
                RecordingGroup group = _plan.Find(type.Key)?.Clone() ?? RecordingGroup.For(type.Key, resolution, fps);
                group.HorizontalResolution = resolution;
                AddGroup(group, type.Count());
            }

            if (!_editors.Any())
                GroupsPanel.Children.Add(new TextBlock { Margin = new Thickness(0, 16, 0, 0), Style = (Style)FindResource("Type.Secondary"), Text = "No cameras with a family type were found in this view." });

            _loading = false;
            Recalculate();
        }

        // ------------------------------------------------------------------
        // One card per camera type
        // ------------------------------------------------------------------

        private void AddGroup(RecordingGroup group, int cameras)
        {
            var editor = new GroupEditor { Group = group, Cameras = cameras };

            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Style = (Style)FindResource("Type.Body"), Margin = new Thickness(12, 10, 12, 0) });
            card.Children.Add(new TextBlock
            {
                Text = $"{cameras} camera{(cameras == 1 ? "" : "s")}" + (group.HorizontalResolution > 0 ? $" · {group.HorizontalResolution} px" : string.Empty),
                Style = (Style)FindResource("Type.Caption"),
                Margin = new Thickness(12, 2, 12, 0)
            });

            var fields = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
            editor.Codec = Combo(fields, "Compression", CodecNames, (int)group.Codec);
            editor.Mode = Combo(fields, "Recording", new[] { "Continuous", "On motion" }, group.MotionOnly ? 1 : 0);
            editor.Motion = Field(fields, "Motion share", group.MotionShare * 100, "%");
            card.Children.Add(fields);

            var video = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
            editor.Day = Field(video, "Day data rate", group.DayBitrateMbps, "Mbps");
            editor.Night = Field(video, "Night data rate", group.NightBitrateMbps, "Mbps");
            editor.NightHours = Field(video, "Night hours a day", group.NightHoursPerDay, "h");
            editor.VideoFields = video;
            card.Children.Add(video);

            var image = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
            editor.FrameSize = Field(image, "Image size", group.FrameSizeKb, "kB");
            editor.FrameRate = Field(image, "Frame rate", group.FrameRate, "fps");
            editor.ImageFields = image;
            card.Children.Add(image);

            var schedule = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
            editor.Weekday = Field(schedule, "Hours Mon–Fri", group.WeekdayHours, "h");
            editor.Saturday = Field(schedule, "Hours Saturday", group.SaturdayHours, "h");
            editor.Sunday = Field(schedule, "Hours Sun, holidays", group.SundayHours, "h");
            editor.Retention = Field(schedule, "Kept for", group.RetentionDays, "days");
            card.Children.Add(schedule);

            editor.Result = new TextBlock { Style = (Style)FindResource("Type.Body"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 12) };
            card.Children.Add(editor.Result);

            _editors.Add(editor);
            GroupsPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 12, 0, 0), Child = card });
        }

        private ComboBox Combo(Panel panel, string label, string[] items, int selected)
        {
            var box = new ComboBox { Width = 130, Style = (Style)FindResource("Input.Combo"), ItemsSource = items, SelectedIndex = selected };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            box.SelectionChanged += (s, e) => Recalculate();
            panel.Children.Add(Labelled(label, box, null));
            return box;
        }

        private TextBox Field(Panel panel, string label, double value, string unit)
        {
            var box = new TextBox { Width = 72, Style = (Style)FindResource("Input.Value"), Text = value.ToString("0.##", CultureInfo.InvariantCulture) };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{label} in {unit}");
            box.TextChanged += (s, e) => Recalculate();
            panel.Children.Add(Labelled(label, box, unit));
            return box;
        }

        private FrameworkElement Labelled(string label, FrameworkElement input, string unit)
        {
            var stack = new StackPanel { Margin = new Thickness(4, 4, 12, 4) };
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Type.Caption"), Margin = new Thickness(0, 0, 0, 3) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(input);
            if (unit != null) row.Children.Add(new TextBlock { Text = unit, Style = (Style)FindResource("Type.Unit"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(row);
            return stack;
        }

        // ------------------------------------------------------------------
        // Results
        // ------------------------------------------------------------------

        private static bool TryRead(TextBox box, double min, double max, out double value)
        {
            string text = box.Text?.Trim();
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            return parsed && value >= min && value <= max;
        }

        // Reads a card into its group; false (with the field) when a value can't be used
        private static bool TryReadGroup(GroupEditor e, out string problem)
        {
            RecordingGroup g = e.Group;
            problem = null;
            g.Codec = (RecordingCodec)Math.Max(0, e.Codec.SelectedIndex);
            g.MotionOnly = e.Mode.SelectedIndex == 1;

            var checks = new (TextBox Box, double Min, double Max, string Name, Action<double> Set)[]
            {
                (e.Motion, 0, 100, "motion share", v => g.MotionShare = v / 100),
                (e.Day, 0, 1000, "day data rate", v => g.DayBitrateMbps = v),
                (e.Night, 0, 1000, "night data rate", v => g.NightBitrateMbps = v),
                (e.NightHours, 0, 24, "night hours", v => g.NightHoursPerDay = v),
                (e.FrameSize, 0, 100000, "image size", v => g.FrameSizeKb = v),
                (e.FrameRate, 0, 1000, "frame rate", v => g.FrameRate = v),
                (e.Weekday, 0, 24, "hours Mon–Fri", v => g.WeekdayHours = v),
                (e.Saturday, 0, 24, "hours Saturday", v => g.SaturdayHours = v),
                (e.Sunday, 0, 24, "hours Sunday", v => g.SundayHours = v),
                (e.Retention, 0, 3650, "days kept", v => g.RetentionDays = (int)Math.Round(v))
            };

            foreach (var check in checks)
            {
                if (!TryRead(check.Box, check.Min, check.Max, out double value))
                {
                    problem = $"Check the {check.Name}: a number from {check.Min:0} to {check.Max:0}.";
                    return false;
                }
                check.Set(value);
            }
            return true;
        }

        private void Recalculate()
        {
            if (_loading) return;

            bool holidaysOk = TryRead(HolidaysBox, 0, 60, out double holidays);
            _plan.PublicHolidaysPerYear = holidaysOk ? (int)Math.Round(holidays) : _plan.PublicHolidaysPerYear;

            double total = 0, average = 0, peak = 0;
            bool allValid = holidaysOk;
            foreach (GroupEditor e in _editors)
            {
                bool image = e.Codec.SelectedIndex == (int)RecordingCodec.Mjpeg;
                e.VideoFields.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
                e.ImageFields.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                e.Motion.IsEnabled = e.Mode.SelectedIndex == 1;

                if (!TryReadGroup(e, out string problem))
                {
                    allValid = false;
                    e.Result.Text = problem;
                    e.Result.SetResourceReference(ForegroundProperty, "Status.Warning");
                    continue;
                }

                StorageEstimate estimate = RecordingStorage.Estimate(e.Group, _plan.PublicHolidaysPerYear);
                total += estimate.TerabytesPerCamera * e.Cameras;
                average += estimate.AverageMbpsPerCamera * e.Cameras;
                peak += estimate.PeakMbpsPerCamera * e.Cameras;

                e.Result.SetResourceReference(ForegroundProperty, "Text.Primary");
                e.Result.Text = $"{estimate.TerabytesPerCamera * e.Cameras:0.00} TB for {e.Cameras} camera{(e.Cameras == 1 ? "" : "s")} " +
                                $"({estimate.TerabytesPerCamera:0.000} TB each) · average {estimate.AverageMbpsPerCamera * e.Cameras:0.#} Mbps, " +
                                $"peak {estimate.PeakMbpsPerCamera * e.Cameras:0.#} Mbps · records {estimate.RecordedShare:P0} of the time.";
            }

            TotalText.Text = allValid
                ? $"Total {total:0.00} TB · network average {average:0.#} Mbps, peak {peak:0.#} Mbps"
                : "Some values can’t be used; the total leaves those groups out.";
            SaveButton.IsEnabled = allValid && _editors.Any();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Groups of types not in this view are kept as they were
            foreach (GroupEditor editor in _editors)
            {
                _plan.Groups.RemoveAll(g => string.Equals(g.Name, editor.Group.Name, StringComparison.OrdinalIgnoreCase));
                _plan.Groups.Add(editor.Group.Clone());
            }

            string json = _plan.Serialize();
            _data.RecordingPlanJson = json;
            _data.SaveRecordingPlan?.Invoke(json);
            MessageDialog.ShowSuccess("Recording plan saved", "It is kept in the project and used by the camera schedule export.", owner: this);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
