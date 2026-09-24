using Camera_FOV.Models;
using System;

namespace Camera_FOV.Services
{
    /// <summary>What a group of cameras needs: storage and network load.</summary>
    public sealed class StorageEstimate
    {
        public double TerabytesPerCamera;
        public double AverageMbpsPerCamera; // Averaged over the whole week, motion share included
        public double PeakMbpsPerCamera;    // While recording at the higher of the day and night rates
        public double RecordedShare;        // Share of the year recorded
    }

    /// <summary>
    /// Recording storage by EVS-EN IEC 62676-4:2026, chapter 10 (issue #19):
    ///  - video compression: T = c × s × p × d / 98 TB;
    ///  - image compression: T = c × i × f × p × d / 12 427 TB, with s = i × f / 128 Mbps;
    /// where c is the number of cameras, s the stream (Mbps), p the share of time with motion
    /// (1 = continuous), d the retention in days, i the image size (kB) and f the frame rate. The
    /// schedule scales them by the share of the year recorded, and the stream is the day and night
    /// rates weighted by the night hours.
    /// </summary>
    public static class RecordingStorage
    {
        public static StorageEstimate Estimate(RecordingGroup group, int publicHolidays)
        {
            var estimate = new StorageEstimate { RecordedShare = RecordedShare(group, publicHolidays) };
            double motion = group.MotionOnly ? Math.Max(0, Math.Min(1, group.MotionShare)) : 1;

            if (group.Codec == RecordingCodec.Mjpeg)
            {
                double stream = group.FrameSizeKb * group.FrameRate / 128.0;
                estimate.TerabytesPerCamera = group.FrameSizeKb * group.FrameRate * motion * group.RetentionDays * estimate.RecordedShare / 12427.0;
                estimate.AverageMbpsPerCamera = stream * motion * estimate.RecordedShare;
                estimate.PeakMbpsPerCamera = stream;
                return estimate;
            }

            double night = Math.Max(0, Math.Min(24, group.NightHoursPerDay)) / 24.0;
            double average = group.DayBitrateMbps * (1 - night) + group.NightBitrateMbps * night;
            estimate.TerabytesPerCamera = average * motion * group.RetentionDays * estimate.RecordedShare / 98.0;
            estimate.AverageMbpsPerCamera = average * motion * estimate.RecordedShare;
            estimate.PeakMbpsPerCamera = Math.Max(group.DayBitrateMbps, group.NightBitrateMbps);
            return estimate;
        }

        /// <summary>Share of the year's hours recorded: weekdays, Saturdays, and Sundays with public holidays.</summary>
        public static double RecordedShare(RecordingGroup group, int publicHolidays)
        {
            double Clamp(double value) => Math.Max(0, Math.Min(24, value));
            int holidays = Math.Max(0, Math.Min(60, publicHolidays));
            double weekdays = 261 - holidays, saturdays = 52, sundays = 52 + holidays;
            double hours = weekdays * Clamp(group.WeekdayHours) + saturdays * Clamp(group.SaturdayHours) + sundays * Clamp(group.SundayHours);
            return hours / (365.0 * 24.0);
        }
    }
}
