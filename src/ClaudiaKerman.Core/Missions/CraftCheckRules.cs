// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>One entry of the fixed Blocker or Warning list.</summary>
    internal sealed class CraftCheckRule
    {
        public readonly string Code;
        public readonly CraftFindingSeverity Severity;
        public readonly string Title;
        /// <summary>When it applies, as Claude is told.</summary>
        public readonly string Condition;
        /// <summary>Raised only by the mod, never by Claude: left out of the prompt and the findings schema.</summary>
        public readonly bool ModOnly;

        public CraftCheckRule(string code, CraftFindingSeverity severity, string title, string condition, bool modOnly = false)
        {
            ModOnly = modOnly;
            Code = code;
            Severity = severity;
            Title = title;
            Condition = condition;
        }
    }

    /// <summary>
    /// The Craft Check's fixed lists (spec stories 48 and 49). Claude may only report these, by code; the mod
    /// decides each finding's severity from the list, never from Claude. Nothing is added to the lists here
    /// without a spec change.
    /// </summary>
    internal static class CraftCheckRules
    {
        private const CraftFindingSeverity Blocker = CraftFindingSeverity.Blocker;
        private const CraftFindingSeverity Warning = CraftFindingSeverity.Warning;
        /// <summary>dock's Blocker (#456), no docking port, RCS or monopropellant: the mod raises it and the Warning itself (<see cref="MarginCheck.Docking"/>), at the Craft Check and each Debrief.</summary>
        public const string DockingKit = "no_docking_kit";
        /// <summary>The Warning: no port on the craft is a size the Target Vessel has free.</summary>
        public const string DockingPortSize = "docking_port_size";
        /// <summary>The Warning (#757): a Maneuver Sequence budgets under the Delta-v Map by more than the tolerance (<see cref="PlanLegs"/>); the mod's own, once a Mission Plan exists.</summary>
        public const string LegUnderMap = "leg_under_map";
        /// <summary>The Warning (#823): an Entry the player named as hot flies with no Entry Shield (<see cref="Entries.HotWithNoShield"/>).</summary>
        public const string HotEntry = "hot_entry_no_shield";
        /// <summary>The Warning (#845): a heat shield on the stack sits past a docking port, so entries don't use it (<see cref="Entries.ShieldPastPort"/>).</summary>
        public const string ShieldPastPort = "heat_shield_past_port";
        /// <summary>The Warning (ADR 0019): a Chute Group likely to leave before its Entry (<see cref="ChuteGroupRules"/>).</summary>
        public const string ChuteGroupLeaves = "chute_group_leaves_early";
        /// <summary>The Blocker (ADR 0019): a Chute Group in a stage at or above an unlit engine the plan still needs: automatic staging refuses to fire it.</summary>
        public const string ChuteGroupAboveEngine = "chute_group_above_engine";
        /// <summary>The Warning (ADR 0019): a Chute Group that lands too fast, on delta-v the engines have.</summary>
        public const string ChuteGroupTooFast = "chute_group_too_fast";

        public static readonly IList<CraftCheckRule> All = new List<CraftCheckRule>
        {
            new CraftCheckRule("no_kos_processor", Blocker, "No kOS processor",
                "the craft has no kOS processor (no part has the kos_processor tag)."),
            new CraftCheckRule("no_seat_for_claudia", Blocker, "No seat for Claudia",
                "the Mission is crewed (she flies aboard) and the craft has no free crew seat for her."),
            new CraftCheckRule("no_probe_core_or_antenna", Blocker, "No probe core or antenna reaching home",
                "the Mission is remote (Claudia stays at Mission Control) and the craft has no probe core, or no antenna able to reach home. "
                + "The mod also raises it itself when no part is tagged probe_core, or (CommNet on) no antenna has transmitter power."),
            new CraftCheckRule("delta_v_short", Blocker, "Not enough delta-v",
                "the craft's delta-v is below what the Mission Goal's route needs from the Delta-v Map plus the margin."),
            new CraftCheckRule("twr_below_one", Blocker, "Thrust-to-weight under 1.0",
                "the thrust over weight is under 1.0 at launch, or for a landing, on the body concerned."),
            new CraftCheckRule("no_parachutes_for_return", Blocker, "No parachutes for the return",
                "a crewed Mission returns to Kerbin and the craft has no parachutes."),
            new CraftCheckRule("chutes_short_for_landing", Blocker, "Chutes short for an air landing",
                "a later Entry has no Chute Group, a powered finish lacks delta-v, or the Kerbin return is uncovered.", modOnly: true),
            new CraftCheckRule(ChuteGroupAboveEngine, Blocker, "Chutes in a stage above an engine still needed",
                "automatic staging won't fire a stage holding chutes, and an unlit engine at or below it is still needed.", modOnly: true),
            new CraftCheckRule("required_experiments_missing", Blocker, "Required experiments missing",
                "the Mission Goal needs experiments the craft has no parts for."),
            new CraftCheckRule(DockingKit, Blocker, "Nothing to dock with",
                "the Mission Goal docks and the craft lacks a docking port, RCS or monopropellant.", modOnly: true),
            new CraftCheckRule("child_vessel_problem", Blocker, "Child Vessel problem",
                "a Child Vessel the Mission Goal plans (a vessel in the separations facts, flown by Claudia after it separates) lacks, at its separation stage, a processor, "
                + "an antenna able to reach Claudia, the delta-v or thrust-to-weight its part of the Mission Goal needs, or electric power. "
                + "The mod also raises it itself for the problems it lists and for a staging plan that strands Claudia without a processor."),

            new CraftCheckRule("launch_twr_low", Warning, "Launch thrust-to-weight under 1.2",
                "the launch thrust over weight is under 1.2 (not under 1.0: a Blocker)."),
            new CraftCheckRule("landing_twr_low", Warning, "Landing thrust-to-weight under 1.5",
                "the landing thrust over weight is under 1.5 (not under 1.0: a Blocker)."),
            new CraftCheckRule("margin_under_25", Warning, "Delta-v margin under 25%",
                "the delta-v margin over the need is under 25% (not short: a Blocker)."),
            new CraftCheckRule("chutes_thin_for_landing", Warning, "Thin chutes for an air landing",
                "a powered finish has under 25% spare delta-v, or an Engineer repack is relied on.", modOnly: true),
            new CraftCheckRule(ChuteGroupLeaves, Warning, "A Chute Group may leave before its Entry",
                "the parts holding it drop in a stage the plan's delta-v burns through first.", modOnly: true),
            new CraftCheckRule(ChuteGroupTooFast, Warning, "A Chute Group lands too fast",
                "its predicted touchdown is over the limit, and the engines have the delta-v to finish it.", modOnly: true),
            new CraftCheckRule("no_landing_legs", Warning, "No landing legs",
                "the Mission Goal includes a landing and the craft has no landing legs."),
            new CraftCheckRule("no_electric_generation_or_low_battery", Warning, "No electric charge generation or a low battery",
                "the craft generates no electric charge, or its batteries are small for the Mission Goal."),
            new CraftCheckRule("weak_control_authority", Warning, "Weak control authority",
                "the craft has little to steer with for its mass (few or no reaction wheels, RCS or gimbals)."),
            new CraftCheckRule("no_attitude_control", Warning, "No way to hold attitude",
                "no part has the reaction_wheel, rcs or aero_stabiliser tag, so the craft can't hold attitude unpowered in air or point for a vacuum burn "
                + "(gimbals steer only while an engine burns). Applies on top of weak_control_authority."),
            new CraftCheckRule("no_heat_shield", Warning, "No heat shield on a high-speed return",
                "the Mission Goal returns to Kerbin at high speed (from the Mun or farther) and the craft has no heat shield."),
            new CraftCheckRule("eva_science_required", Warning, "EVA science required",
                "the Mission Goal needs science only a Kerbal on EVA can do (Claudia doesn't go on EVA; the player would have to)."),
            new CraftCheckRule("boot_file_set", Warning, "Boot file set on Claudia's processor",
                "the processor Claudia will use (chosen_processor) has a boot file set."),
            new CraftCheckRule("manual_stretches_without_crew", Warning, "Manual stretches planned with no other crew aboard",
                "the Mission Goal plans stretches the player flies by hand (EVA science, flying a sequence) and the crew list has no Kerbal "
                + "other than Claudia (trait SuperIntelligence) and Tourists (trait Tourist)."),
            new CraftCheckRule(DockingPortSize, Warning, "Docking port sizes may not match",
                "the Mission Goal docks and no port on the craft is a size the Target Vessel has free.", modOnly: true),
            new CraftCheckRule(LegUnderMap, Warning, "A leg's budget is under the Delta-v Map",
                "a Maneuver Sequence's budget is more than the tolerance under its legs' figure on the Delta-v Map.", modOnly: true),
            new CraftCheckRule(HotEntry, Warning, "A hot entry with no heat shield",
                "an Entry on Eve or Laythe, or straight in from a transfer, has no Entry Shield.", modOnly: true),
            new CraftCheckRule(ShieldPastPort, Warning, "A heat shield sits past a docking port",
                "a heat shield on the stack is reached only by crossing a docking port, and the Mission Goal has an Entry: it isn't an Entry Shield.", modOnly: true),
            new CraftCheckRule("ascent_cannot_reach_orbit", Warning, "The ascent can't reach orbit",
                "plan_ascent says reaches_orbit is false for the launch (no turn_shape it tried reaches orbit); name the Boost stage that falls short."),
            new CraftCheckRule("ascent_thin_margin", Warning, "The ascent reaches orbit by a thin margin",
                "plan_ascent says the launch reaches orbit but with a thin margin."),
        };

        public static CraftCheckRule Find(string code)
        {
            return All.FirstOrDefault(r => r.Code == code);
        }

        /// <summary>The rules as the system prompt lists them.</summary>
        public static string Describe()
        {
            var text = new StringBuilder();
            text.Append("Blockers (the Mission cannot start; the player cannot override them):\n");
            foreach (var rule in All.Where(r => !r.ModOnly && r.Severity == Blocker))
                text.Append("- ").Append(rule.Code).Append(": ").Append(rule.Title).Append(". Applies when ").Append(rule.Condition).Append('\n');
            text.Append("\nWarnings (the player must acknowledge each before the Mission starts):\n");
            foreach (var rule in All.Where(r => !r.ModOnly && r.Severity == Warning))
                text.Append("- ").Append(rule.Code).Append(": ").Append(rule.Title).Append(". Applies when ").Append(rule.Condition).Append('\n');
            return text.ToString();
        }

        /// <summary>The structured-output schema: a summary and findings whose code can only be one of the lists'.</summary>
        public static string SchemaJson()
        {
            var codes = new JsonWriter().BeginArray();
            foreach (var rule in All.Where(r => !r.ModOnly)) codes.String(rule.Code);
            codes.EndArray();
            return "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"summary\",\"findings\",\"child_vessel_stages\",\"unsure_because\",\"planned_turn_shape\"],\"properties\":{"
                + SecondLook.FieldJson + ","
                + "\"summary\":{\"type\":\"string\",\"description\":\"Your line to the player: concise mission-control voice, key numbers first, one or two sentences.\"},"
                + "\"child_vessel_stages\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"The separation stages (from the separations facts) "
                + "whose vessels the Mission Goal plans to fly as Child Vessels; empty when it plans none.\"},"
                + "\"planned_turn_shape\":{\"type\":\"number\",\"description\":\"The turn_shape plan_ascent gave for the launch; 0 when you did not call it or it found none.\"},"
                + "\"findings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
                + "\"required\":[\"code\",\"explanation\",\"fix\"],\"properties\":{"
                + "\"code\":{\"type\":\"string\",\"enum\":" + codes + "},"
                + "\"explanation\":{\"type\":\"string\",\"description\":\"What is wrong, with the numbers.\"},"
                + "\"fix\":{\"type\":\"string\",\"description\":\"A concrete change the player could make to the craft; \\\"\\\" if none is needed.\"}}}}}}";
        }
    }
}
