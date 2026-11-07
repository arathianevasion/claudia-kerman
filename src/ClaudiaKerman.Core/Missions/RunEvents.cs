// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>What the mod noticed during a run, for the Debrief.</summary>
    public enum RunEventKind
    {
        /// <summary>A stage fired.</summary>
        Staging,
        /// <summary>A part was destroyed or broke off the craft.</summary>
        PartLost,
        /// <summary>A part passed the share of its heat limit set in <see cref="RunEventLog.OverheatPercent"/>.</summary>
        Overheating,
        /// <summary>A Building Block ended without success.</summary>
        BlockAborted,
        /// <summary>Science was collected (an experiment run, data transmitted or recovered).</summary>
        Science,
        /// <summary>The mod stopped the run: a runtime gate, the runaway hard stop or the player's Abort.</summary>
        Stopped,
        /// <summary>A landing aimed at Kerbin: km from KSC predicted, and actual once down (#790). The latest stands for the run.</summary>
        Landing,
        /// <summary>Daylight was asked for and the craft landed out of Daylight (#828), with the sun's figure.</summary>
        DarkLanding,
    }

    public sealed class RunEvent
    {
        /// <summary>Game time, s of UT.</summary>
        public double Ut;
        public RunEventKind Kind;
        public string Text = "";
    }

    /// <summary>
    /// The notable events of one run (staging, part loss, overheating, Building Block aborts, science), kept in order for
    /// the Debrief. The adapter reports the game's events (<see cref="RunEventSeen"/>); Mission Control adds the ones it
    /// sees itself. Bounded, so a run that stages or sheds parts without end cannot grow the Debrief request without end.
    /// </summary>
    public sealed class RunEventLog
    {
        /// <summary>Events kept; later ones only count toward <see cref="Dropped"/>.</summary>
        public const int Max = 40;
        /// <summary>The adapter reports Overheating when a part reaches this percent of its heat limit.</summary>
        public const double OverheatPercent = 90;

        private readonly List<RunEvent> _events = new List<RunEvent>();

        public IList<RunEvent> Events
        {
            get { return _events; }
        }

        /// <summary>Events left out because the log was full.</summary>
        public int Dropped { get; private set; }

        public int Count
        {
            get { return _events.Count; }
        }

        /// <summary>Adds an event. The same event twice in a row (a part reported hot every frame) counts once.</summary>
        public void Add(double ut, RunEventKind kind, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            var last = _events.Count > 0 ? _events[_events.Count - 1] : null;
            if (last != null && last.Kind == kind && last.Text == text) return;
            if (_events.Count >= Max)
            {
                Dropped++;
                return;
            }
            _events.Add(new RunEvent { Ut = ut, Kind = kind, Text = text });
        }

        /// <summary>Puts back a log that was saved: at most <see cref="Max"/> events, and how many were dropped.</summary>
        public void Restore(IEnumerable<RunEvent> events, int dropped)
        {
            Clear();
            foreach (var e in events.Take(Max)) _events.Add(e);
            Dropped = System.Math.Max(0, dropped);
        }

        public void Clear()
        {
            _events.Clear();
            Dropped = 0;
        }

        /// <summary>The run's one line of this kind: it replaces any before it.</summary>
        public void Replace(double ut, RunEventKind kind, string text)
        {
            _events.RemoveAll(e => e.Kind == kind);
            // It is the Debrief's own line: at the bound it takes the latest event's place.
            if (_events.Count >= Max)
            {
                _events.RemoveAt(_events.Count - 1);
                Dropped++;
            }
            Add(ut, kind, text);
        }

        /// <summary>The events other than science, one line each: "+12 s staging: stage 3 fired".</summary>
        public IList<string> EventLines(double startUt)
        {
            return Lines(_events.Where(e => e.Kind != RunEventKind.Science), startUt, true);
        }

        /// <summary>The science collected, one line each.</summary>
        public IList<string> ScienceLines(double startUt)
        {
            return Lines(_events.Where(e => e.Kind == RunEventKind.Science), startUt, false);
        }

        private IList<string> Lines(IEnumerable<RunEvent> events, double startUt, bool withKind)
        {
            var lines = events.Select(e => "+" + System.Math.Max(0, e.Ut - startUt).ToString("0", CultureInfo.InvariantCulture) + " s "
                + (withKind ? Label(e.Kind) + ": " : "") + e.Text).ToList();
            if (Dropped > 0 && withKind) lines.Add("(" + Dropped.ToString(CultureInfo.InvariantCulture) + " more events not listed)");
            return lines;
        }

        public static string Label(RunEventKind kind)
        {
            switch (kind)
            {
                case RunEventKind.Staging: return "staging";
                case RunEventKind.PartLost: return "part lost";
                case RunEventKind.Overheating: return "overheating";
                case RunEventKind.BlockAborted: return "block aborted";
                case RunEventKind.Science: return "science";
                case RunEventKind.Landing: return "landing";
                case RunEventKind.DarkLanding: return "landed in the dark";
                default: return "run stopped";
            }
        }

        /// <summary>The Debrief request's account of the run's events; "" when nothing was logged.</summary>
        public string DebriefText(double startUt)
        {
            var events = EventLines(startUt);
            var science = ScienceLines(startUt);
            if (events.Count == 0 && science.Count == 0) return "";
            var text = "";
            if (events.Count > 0) text += "\nEvents the mod logged during the run (quoted): " + string.Join("; ", events.ToArray()) + ".";
            if (science.Count > 0) text += "\nScience the mod saw collected during the run (quoted): " + string.Join("; ", science.ToArray()) + ".";
            return text;
        }
    }

    /// <summary>
    /// The adapter noticed something notable on the craft during a run (a stage fired, a part was lost, a part is
    /// overheating, science was collected). Ignored unless a run is going.
    /// </summary>
    public sealed class RunEventSeen : MissionInput
    {
        public readonly RunEventKind Kind;
        public readonly string Text;
        /// <summary>Game time of the event, s of UT.</summary>
        public readonly double Ut;
        public RunEventSeen(RunEventKind kind, string text, double ut) { Kind = kind; Text = text; Ut = ut; }
    }

    /// <summary>
    /// A stage or a decoupler fired on the Mission craft during a run (#605), so stock's delta-v left may fall by what was
    /// dropped. Not logged: the stage's own <see cref="RunEventSeen"/> is. Ignored unless a run is going.
    /// </summary>
    public sealed class CraftSeparated : MissionInput
    {
        /// <summary>The craft's throttle (0 to 1) at the moment of separation (#615); null when the event carries none, and the last snapshot's stands.</summary>
        public readonly double? Throttle;
        public CraftSeparated(double? throttle = null) { Throttle = throttle; }
    }
}
