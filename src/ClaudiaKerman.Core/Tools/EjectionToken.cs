// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The ejection node a watched wait of <c>transfer_to</c> carries to the step after it (#867, #872): the node's UT and components, and the phase
    /// that follows its burn. One string, "ut;prograde;normal;radial;phase" with invariant numbers, which the block's Kerboscript passes through
    /// untouched. Its format and parsing live here.
    /// </summary>
    internal sealed class EjectionToken
    {
        public double Ut;
        public double Prograde;
        public double Normal;
        public double Radial;
        public string Phase = "";

        /// <summary>The token of a step that flies an ejection node.</summary>
        public static EjectionToken Of(TransferFlightStep step)
        {
            return new EjectionToken { Ut = step.Ut, Prograde = step.Prograde, Normal = step.Normal, Radial = step.Radial, Phase = step.Phase };
        }

        public override string ToString()
        {
            return Num(Ut) + ";" + Num(Prograde) + ";" + Num(Normal) + ";" + Num(Radial) + ";" + Phase;
        }

        /// <summary>The token <see cref="ToString"/> wrote; null for anything else (empty, too few or too many fields, a number that doesn't read).</summary>
        public static EjectionToken Parse(string text)
        {
            var parts = (text ?? "").Split(';');
            if (parts.Length != 5) return null;
            var token = new EjectionToken { Phase = parts[4] };
            return Read(parts[0], out token.Ut) && Read(parts[1], out token.Prograde) && Read(parts[2], out token.Normal) && Read(parts[3], out token.Radial) ? token : null;
        }

        private static bool Read(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
