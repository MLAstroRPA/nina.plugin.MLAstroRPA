using System;
using System.Collections.Generic;
using MLAstroRPA.Settings;

namespace MLAstroRPA.Broker
{
    /// <summary>Which axes a single correction step moves.</summary>
    public enum BridgeAxisMode
    {
        /// <summary>Move both axes in a single ALIGN command.</summary>
        Both = 0,

        /// <summary>Move only the axis with the larger remaining error.</summary>
        Auto = 1
    }

    /// <summary>
    /// Correction strategy of the controller. These values belong to MLAstro: TPPA only publishes the
    /// measured error, the tolerance and the direction, and never decides how much to move.
    /// </summary>
    public sealed class BridgeSettings
    {
        public BridgeAxisMode AxisMode { get; set; } = BridgeAxisMode.Both;
        public double SafetyFactor { get; set; } = 0.75;
        public double MaxStepArcMin { get; set; } = 60;
        public bool OvershootEnabled { get; set; }
        public bool OvershootUpEnabled { get; set; } = true;
        public bool OvershootDownEnabled { get; set; } = true;
        public double OvershootUpArcMin { get; set; } = 5;
        public double OvershootDownArcMin { get; set; } = 5;
        public bool ReverseAzimuth { get; set; }
        public bool ReverseAltitude { get; set; }

        public static BridgeSettings FromPluginSettings(PluginSettings settings)
        {
            if (settings == null) { return new BridgeSettings(); }

            return new BridgeSettings
            {
                AxisMode = settings.CorrectionAxisMode,
                SafetyFactor = Clamp(settings.CorrectionSafetyFactor, 0.05, 1.0),
                MaxStepArcMin = Math.Max(1, settings.CorrectionMaxStepArcMin),
                OvershootEnabled = settings.CorrectionOvershootEnabled,
                OvershootUpEnabled = settings.CorrectionOvershootUpEnabled,
                OvershootDownEnabled = settings.CorrectionOvershootDownEnabled,
                OvershootUpArcMin = Math.Max(0, settings.CorrectionOvershootUpArcMin),
                OvershootDownArcMin = Math.Max(0, settings.CorrectionOvershootDownArcMin),
                ReverseAzimuth = settings.SoftwareReverseAzimuth,
                ReverseAltitude = settings.SoftwareReverseAltitude
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }
    }

    /// <summary>What the controller intends to do with a single measurement.</summary>
    public sealed class CorrectionPlan
    {
        /// <summary>True when the measurement cannot be used and TPPA should just measure again.</summary>
        public bool VerifyOnly { get; set; }

        public bool HasMove { get; set; }
        public string Reason { get; set; }

        public bool MoveAzimuth { get; set; }
        public bool MoveAltitude { get; set; }
        public double AzimuthMagnitudeArcMin { get; set; }
        public double AltitudeMagnitudeArcMin { get; set; }

        /// <summary>Hardware AzDi flag: true moves the azimuth axis to the right.</summary>
        public bool AzimuthRight { get; set; }

        /// <summary>Hardware AlDi flag: true moves the altitude axis up.</summary>
        public bool AltitudeUp { get; set; }

        public double ToleranceArcMin { get; set; }
    }

    /// <summary>
    /// Turns a TPPA measurement into a hardware move. The direction is never re-derived from the error
    /// sign: it is taken from the direction TPPA published, which is the same direction it shows to the
    /// user in its own interface.
    /// </summary>
    public static class BridgeEngine
    {
        /// <summary>
        /// An overshoot has to be larger than the tolerance. At the top of the overshoot the measured
        /// error is approximately the overshoot itself, so a smaller overshoot would insert a sample
        /// that already looks "done" in the middle of the move chain.
        /// </summary>
        public static bool IsOvershootAboveTolerance(double overshootArcMin, double toleranceArcMin)
        {
            return toleranceArcMin <= 0 || overshootArcMin > toleranceArcMin;
        }

        public static CorrectionPlan CreatePlan(BridgeMeasurement measurement,
                                               BridgeSettings settings,
                                               IList<string> warnings = null)
        {
            settings ??= new BridgeSettings();
            var plan = new CorrectionPlan
            {
                ToleranceArcMin = measurement?.ToleranceArcMin > 0
                    ? measurement.ToleranceArcMin
                    : 0
            };

            if (measurement == null)
            {
                plan.VerifyOnly = true;
                plan.Reason = "no measurement";
                return plan;
            }

            if (!measurement.IsUsable)
            {
                plan.VerifyOnly = true;
                plan.Reason = $"measurement is {measurement.Status}";
                return plan;
            }

            if (measurement.ToleranceReached)
            {
                // Within tolerance: hold the axes still and let TPPA confirm the state with another
                // solve. Moving now would only restart the chain.
                plan.VerifyOnly = true;
                plan.Reason = $"within tolerance ({measurement.TotalErrorArcMin:0.##}' <= {plan.ToleranceArcMin:0.##}')";
                return plan;
            }

            var azimuthError = Math.Abs(measurement.AzimuthErrorArcMin);
            var altitudeError = Math.Abs(measurement.AltitudeErrorArcMin);

            if (settings.AxisMode == BridgeAxisMode.Auto && azimuthError > 0 && altitudeError > 0)
            {
                if (azimuthError >= altitudeError) { altitudeError = 0; } else { azimuthError = 0; }
            }

            var azimuthDirection = measurement.AzimuthDirectionValue;
            var altitudeDirection = measurement.AltitudeDirectionValue;

            var moveAzimuth = azimuthError > 0 && azimuthDirection != BridgeAzimuthDirection.None;
            var moveAltitude = altitudeError > 0 && altitudeDirection != BridgeAltitudeDirection.None;

            var azimuthMagnitude = moveAzimuth ? Step(azimuthError, settings) : 0;

            // Overshoot is an altitude feature with one switch per direction, and it works the way the
            // MLAstro TPPA plugin always did: the axis goes the FULL error plus the overshoot past the
            // target, and the next measurement corrects whatever is left. There is no back-off move - the
            // extra travel is what takes the play out of the axis, a deliberate return would put it back.
            var movesUp = altitudeDirection == BridgeAltitudeDirection.Up;
            var overshootAltitude = settings.OvershootEnabled
                                    && (movesUp ? settings.OvershootUpEnabled : settings.OvershootDownEnabled);
            var altitudeOvershootArcMin = movesUp ? settings.OvershootUpArcMin : settings.OvershootDownArcMin;

            var altitudeMagnitude = !moveAltitude ? 0
                : overshootAltitude
                    ? Math.Min(altitudeError + altitudeOvershootArcMin, settings.MaxStepArcMin)
                    : Step(altitudeError, settings);

            if (moveAltitude && overshootAltitude)
            {
                WarnIfOvershootIsNotUsable(altitudeOvershootArcMin, plan.ToleranceArcMin, warnings);
            }

            plan.MoveAzimuth = moveAzimuth;
            plan.MoveAltitude = moveAltitude;
            plan.AzimuthMagnitudeArcMin = Math.Round(azimuthMagnitude, 3);
            plan.AltitudeMagnitudeArcMin = Math.Round(altitudeMagnitude, 3);
            plan.AzimuthRight = azimuthDirection == BridgeAzimuthDirection.Right;
            plan.AltitudeUp = altitudeDirection == BridgeAltitudeDirection.Up;

            if (settings.ReverseAzimuth) { plan.AzimuthRight = !plan.AzimuthRight; }
            if (settings.ReverseAltitude) { plan.AltitudeUp = !plan.AltitudeUp; }

            plan.HasMove = (moveAzimuth && plan.AzimuthMagnitudeArcMin > 0)
                           || (moveAltitude && plan.AltitudeMagnitudeArcMin > 0);

            plan.Reason = plan.HasMove
                ? $"az {plan.AzimuthMagnitudeArcMin:0.##}' {(plan.MoveAzimuth ? (plan.AzimuthRight ? "right" : "left") : "hold")}, alt {plan.AltitudeMagnitudeArcMin:0.##}' {(plan.MoveAltitude ? (plan.AltitudeUp ? "up" : "down") : "hold")}"
                : "no movable axis";

            return plan;
        }

        private static double Step(double errorArcMin, BridgeSettings settings)
        {
            var requested = errorArcMin * settings.SafetyFactor;
            return Math.Min(requested, settings.MaxStepArcMin);
        }

        private static void WarnIfOvershootIsNotUsable(double overshootArcMin, double toleranceArcMin, IList<string> warnings)
        {
            if (warnings == null) { return; }
            if (!IsOvershootAboveTolerance(overshootArcMin, toleranceArcMin) && !warnings.Contains(OvershootWarning))
            {
                warnings.Add(OvershootWarning);
            }
        }

        public const string OvershootWarning =
            "Overshoot is not larger than the alignment tolerance. At the top of the overshoot the measured error is about the overshoot itself, so a sample that already looks finished can appear in the middle of the move chain. Increase the overshoot or turn it off.";

    }
}
