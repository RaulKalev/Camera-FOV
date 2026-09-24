using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Camera_FOV.Models
{
    public enum RecordingCodec
    {
        H264,
        H265,
        Mjpeg
    }

    /// <summary>
    /// How one group of cameras (by Revit type) records (issue #19): when, how, at what data rate and
    /// for how long it is kept. Saved in the project.
    /// </summary>
    public class RecordingGroup
    {
        public string Name { get; set; } = string.Empty; // The Revit type the cameras share
        public int HorizontalResolution { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public RecordingCodec Codec { get; set; } = RecordingCodec.H265;

        public bool MotionOnly { get; set; }            // Motion-triggered rather than continuous
        public double MotionShare { get; set; } = 0.3;  // Share of the recording time with motion, when motion-triggered

        public double DayBitrateMbps { get; set; }
        public double NightBitrateMbps { get; set; }
        public double NightHoursPerDay { get; set; } = 10;

        public double FrameRate { get; set; } = 25;
        public double FrameSizeKb { get; set; }         // Per image, for M-JPEG

        // Hours recorded per day (e.g. 11 for 21:00–08:00; 24 for all day)
        public double WeekdayHours { get; set; } = 24;
        public double SaturdayHours { get; set; } = 24;
        public double SundayHours { get; set; } = 24;   // Also used on public holidays

        public int RetentionDays { get; set; } = 30;

        public RecordingGroup Clone() => (RecordingGroup)MemberwiseClone();

        /// <summary>A starting data rate for a resolution and codec (Mbps), to be replaced by the camera's own figure.</summary>
        public static double DefaultBitrate(int horizontalResolution, RecordingCodec codec)
        {
            double h264 = horizontalResolution <= 1280 ? 2 : horizontalResolution <= 1920 ? 4 : horizontalResolution <= 2688 ? 6 : horizontalResolution <= 3840 ? 12 : 16;
            return codec == RecordingCodec.H265 ? h264 / 2 : h264;
        }

        /// <summary>A starting JPEG image size (kB) for a resolution.</summary>
        public static double DefaultFrameSize(int horizontalResolution) => Math.Round(Math.Max(50, horizontalResolution * 0.1));

        public static RecordingGroup For(string name, int resolution, double? frameRate)
        {
            var group = new RecordingGroup { Name = name, HorizontalResolution = resolution };
            group.DayBitrateMbps = group.NightBitrateMbps = DefaultBitrate(resolution, group.Codec);
            group.FrameSizeKb = DefaultFrameSize(resolution);
            if (frameRate > 0) group.FrameRate = frameRate.Value;
            return group;
        }
    }

    public class RecordingPlan
    {
        public int PublicHolidaysPerYear { get; set; } = 12;
        public List<RecordingGroup> Groups { get; set; } = new List<RecordingGroup>();

        public string Serialize() => JsonConvert.SerializeObject(this, Formatting.None);

        public static RecordingPlan Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new RecordingPlan();
            try
            {
                var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                return JsonConvert.DeserializeObject<RecordingPlan>(json, settings) ?? new RecordingPlan();
            }
            catch (JsonException)
            {
                return new RecordingPlan();
            }
        }

        public RecordingGroup Find(string name) => Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
