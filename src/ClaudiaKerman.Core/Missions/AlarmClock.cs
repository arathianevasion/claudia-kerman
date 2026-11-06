// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The KSP Alarm Clock as the mod uses it: one alarm per Mission at its Due Time (#616). The adapter's implementation is the
    /// stock <c>AlarmClockScenario</c>; Core tests use a fake that keeps the alarms in a dictionary.
    /// </summary>
    public interface IAlarmClock
    {
        /// <summary>Sets the Mission's alarm at <paramref name="ut"/>, replacing its earlier one.</summary>
        void Set(string missionId, double ut, string title);

        /// <summary>Removes the Mission's alarm, if it has one.</summary>
        void Clear(string missionId);
    }

    /// <summary>Carries the alarm commands of <see cref="MissionControl"/> out on an <see cref="IAlarmClock"/>.</summary>
    public static class AlarmClockCommands
    {
        /// <summary>Applies <paramref name="command"/> if it is an alarm command; false for any other command.</summary>
        public static bool Apply(AdapterCommand command, IAlarmClock clock)
        {
            var set = command as SetDueTimeAlarm;
            if (set != null)
            {
                clock.Set(set.MissionId, set.Ut, set.Title);
                return true;
            }
            var clear = command as ClearDueTimeAlarm;
            if (clear == null) return false;
            clock.Clear(clear.MissionId);
            return true;
        }
    }
}
