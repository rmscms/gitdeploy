using System;
using System.Collections.Generic;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class BackupScheduleTimelineService
    {
        public static void RecalculateNextRun(BackupSchedule? schedule, DateTime? referenceUtc = null)
        {
            if (schedule == null)
            {
                return;
            }

            schedule.NextRunUtc = BackupSchedulePlanner.CalculateNextRunUtc(schedule, referenceUtc ?? DateTime.UtcNow);
        }

        public static DateTime? FindSoonestUpcomingRunUtc(IEnumerable<BackupSchedule>? schedules, DateTime? referenceUtc = null)
        {
            if (schedules == null)
            {
                return null;
            }

            var utcNow = referenceUtc ?? DateTime.UtcNow;
            DateTime? soonest = null;

            foreach (var schedule in schedules)
            {
                if (schedule == null || !schedule.Enabled)
                {
                    continue;
                }

                var next = schedule.NextRunUtc ?? BackupSchedulePlanner.CalculateNextRunUtc(schedule, utcNow);
                if (next == null)
                {
                    continue;
                }

                if (soonest == null || next < soonest)
                {
                    soonest = next;
                }
            }

            return soonest;
        }

        public static string BuildCountdownText(int activeCount, DateTime? nextRunUtc, DateTime? referenceUtc = null)
        {
            if (activeCount > 0)
            {
                return "running…";
            }

            if (nextRunUtc == null)
            {
                return "no schedule";
            }

            var utcNow = referenceUtc ?? DateTime.UtcNow;
            var diff = nextRunUtc.Value - utcNow;
            if (diff <= TimeSpan.Zero)
            {
                return "pending";
            }

            if (diff.TotalHours >= 1)
            {
                return $"{Math.Floor(diff.TotalHours)}h {diff.Minutes:D2}m";
            }

            return $"{diff.Minutes:D2}m {diff.Seconds:D2}s";
        }
    }
}
