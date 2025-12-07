using System;
using System.Collections.Generic;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class BackupSchedulePlanner
    {
        public static DateTime? CalculateNextRunUtc(BackupSchedule schedule, DateTime? fromUtc = null)
        {
            if (schedule == null || !schedule.Enabled)
            {
                return null;
            }

            var referenceLocal = (fromUtc ?? DateTime.UtcNow).ToLocalTime();
            var runTime = schedule.LocalRunTime;

            return schedule.Frequency switch
            {
                BackupScheduleFrequency.Once => schedule.LastRunUtc == null
                    ? GetNextDailyOccurrence(referenceLocal, runTime).ToUniversalTime()
                    : (DateTime?)null,
                BackupScheduleFrequency.Daily => GetNextDailyOccurrence(referenceLocal, runTime).ToUniversalTime(),
                BackupScheduleFrequency.Weekly => GetNextWeeklyOccurrence(referenceLocal, runTime, schedule.DaysOfWeek).ToUniversalTime(),
                BackupScheduleFrequency.Monthly => GetNextMonthlyOccurrence(referenceLocal, runTime, schedule.DayOfMonth).ToUniversalTime(),
                BackupScheduleFrequency.CustomInterval => GetNextIntervalOccurrence(referenceLocal, schedule.CustomIntervalMinutes).ToUniversalTime(),
                _ => GetNextDailyOccurrence(referenceLocal, runTime).ToUniversalTime()
            };
        }

        public static void RefreshNextRun(BackupSchedule schedule)
        {
            if (schedule == null) return;
            schedule.NextRunUtc = CalculateNextRunUtc(schedule, DateTime.UtcNow);
        }

        private static DateTime GetNextDailyOccurrence(DateTime referenceLocal, TimeSpan runTime)
        {
            var candidate = referenceLocal.Date.Add(runTime);
            if (candidate <= referenceLocal)
            {
                candidate = candidate.AddDays(1);
            }
            return candidate;
        }

        private static DateTime GetNextWeeklyOccurrence(DateTime referenceLocal, TimeSpan runTime, IList<DayOfWeek>? days)
        {
            var orderedDays = (days != null && days.Count > 0 ? days : new List<DayOfWeek> { referenceLocal.DayOfWeek })
                .Distinct()
                .OrderBy(d => ((int)d - (int)referenceLocal.DayOfWeek + 7) % 7)
                .ToList();

            foreach (var day in orderedDays)
            {
                var delta = ((int)day - (int)referenceLocal.DayOfWeek + 7) % 7;
                var candidate = referenceLocal.Date.AddDays(delta).Add(runTime);
                if (candidate > referenceLocal)
                {
                    return candidate;
                }
            }

            var first = orderedDays.First();
            var fallbackDelta = ((int)first - (int)referenceLocal.DayOfWeek + 7) % 7;
            if (fallbackDelta == 0) fallbackDelta = 7;
            return referenceLocal.Date.AddDays(fallbackDelta).Add(runTime);
        }

        private static DateTime GetNextMonthlyOccurrence(DateTime referenceLocal, TimeSpan runTime, int dayOfMonth)
        {
            var desiredDay = dayOfMonth <= 0 ? 1 : Math.Min(dayOfMonth, 31);
            var candidate = BuildMonthlyCandidate(referenceLocal.Year, referenceLocal.Month, desiredDay, runTime);
            if (candidate <= referenceLocal)
            {
                var next = referenceLocal.AddMonths(1);
                candidate = BuildMonthlyCandidate(next.Year, next.Month, desiredDay, runTime);
            }
            return candidate;
        }

        private static DateTime BuildMonthlyCandidate(int year, int month, int dayOfMonth, TimeSpan runTime)
        {
            var safeDay = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
            return new DateTime(year, month, safeDay, 0, 0, 0, DateTimeKind.Local).Add(runTime);
        }

        private static DateTime GetNextIntervalOccurrence(DateTime referenceLocal, int minutes)
        {
            var interval = minutes <= 0 ? 60 : minutes;
            return referenceLocal.AddMinutes(interval);
        }
    }
}

