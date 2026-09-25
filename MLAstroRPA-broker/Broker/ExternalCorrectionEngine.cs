using System;
using System.Collections.Generic;
using MLAstro_Robotic_Polar_Alignment.Settings;

namespace MLAstro_Robotic_Polar_Alignment.Broker
{
    /// <summary>Which axes a single correction step moves.</summary>
    public enum ExternalAxisMode
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
    public sealed class ExternalCorrectionSettings
    {
        public ExternalAxisMode AxisMode { get; set; } = ExternalAxisMode.Both;
        public double SafetyFactor { get; set; } = 0.75;
        public double MaxStepArcMin { get; set; } = 60;
        public bool OvershootEnabled { get; set; }
        public bool OvershootUpEnabled { get; set; } = true;
        public bool OvershootDownEnabled { get; set; } = true;
        public double OvershootUpArcMin { get; set; } = 5;
        public double OvershootDownArcMin { get; set; } = 5;
        public bool ReverseAzimuth { get; set; }
        public bool ReverseAltitude { get; set; }
        public double AzimuthBacklashArcMin { get; set; }
        public int ConsecutiveToFinish { get; set; } = 2;
        public int TimeoutSec { get; set; } = 1800;

        public static ExternalCorrectionSettings FromPluginSettings(PluginSettings settings)
        {
            if (settings == null) { return new ExternalCorrectionSettings(); }

            return new ExternalCorrectionSettings
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
                ReverseAltitude = settings.SoftwareReverseAltitude,
                AzimuthBacklashArcMin = Math.Max(0, settings.CorrectionAzBacklashArcMin),
                ConsecutiveToFinish = Math.Max(1, settings.CorrectionConsecutiveToFinish),
                TimeoutSec = Math.Max(60, settings.CorrectionTimeoutSec)
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
    public static class ExternalCorrectionEngine
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

        public static CorrectionPlan CreatePlan(TppaMeasurement measurement,
                                               ExternalCorrectionSettings settings,
                                               IList<string> warnings = null)
        {
            settings ??= new ExternalCorrectionSettings();
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

            if (settings.AxisMode == ExternalAxisMode.Auto && azimuthError > 0 && altitudeError > 0)
            {
                if (azimuthError >= altitudeError) { altitudeError = 0; } else { azimuthError = 0; }
            }

            var azimuthDirection = measurement.AzimuthDirectionValue;
            var altitudeDirection = measurement.AltitudeDirectionValue;

            var moveAzimuth = azimuthError > 0 && azimuthDirection != ExternalAzimuthDirection.None;
            var moveAltitude = altitudeError > 0 && altitudeDirection != ExternalAltitudeDirection.None;

            var azimuthMagnitude = moveAzimuth ? Step(azimuthError, settings) : 0;
            var altitudeMagnitude = moveAltitude ? Step(altitudeError, settings) : 0;

            // Overshoot can be enabled per direction: the altitude axis has one switch for moving up and
            // one for moving down. Azimuth moves follow the master switch only.
            var movesUp = altitudeDirection == ExternalAltitudeDirection.Up;
            var overshootAltitude = settings.OvershootEnabled
                                    && (movesUp ? settings.OvershootUpEnabled : settings.OvershootDownEnabled);
            var altitudeOvershootArcMin = movesUp ? settings.OvershootUpArcMin : settings.OvershootDownArcMin;

            if (moveAzimuth && settings.OvershootEnabled)
            {
                azimuthMagnitude += settings.OvershootUpArcMin;
                WarnIfOvershootIsNotUsable(settings.OvershootUpArcMin, plan.ToleranceArcMin, warnings);
            }

            if (moveAltitude && overshootAltitude)
            {
                altitudeMagnitude += altitudeOvershootArcMin;
                WarnIfOvershootIsNotUsable(altitudeOvershootArcMin, plan.ToleranceArcMin, warnings);
            }

            if (moveAzimuth && altitudeMagnitude == 0
                && settings.AzimuthBacklashArcMin > 0
                && IsBacklashDirectionChange(measurement.AzimuthDirectionValue))
            {
                // Backlash compensation for a direction change: move past the target, then come back.
                azimuthMagnitude += settings.AzimuthBacklashArcMin;
            }

            plan.MoveAzimuth = moveAzimuth;
            plan.MoveAltitude = moveAltitude;
            plan.AzimuthMagnitudeArcMin = Math.Round(azimuthMagnitude, 3);
            plan.AltitudeMagnitudeArcMin = Math.Round(altitudeMagnitude, 3);
            plan.AzimuthRight = azimuthDirection == ExternalAzimuthDirection.Right;
            plan.AltitudeUp = altitudeDirection == ExternalAltitudeDirection.Up;

            if (settings.ReverseAzimuth) { plan.AzimuthRight = !plan.AzimuthRight; }
            if (settings.ReverseAltitude) { plan.AltitudeUp = !plan.AltitudeUp; }

            plan.HasMove = (moveAzimuth && plan.AzimuthMagnitudeArcMin > 0)
                           || (moveAltitude && plan.AltitudeMagnitudeArcMin > 0);

            plan.Reason = plan.HasMove
                ? $"az {plan.AzimuthMagnitudeArcMin:0.##}' {(plan.MoveAzimuth ? (plan.AzimuthRight ? "right" : "left") : "hold")}, alt {plan.AltitudeMagnitudeArcMin:0.##}' {(plan.MoveAltitude ? (plan.AltitudeUp ? "up" : "down") : "hold")}"
                : "no movable axis";

            return plan;
        }

        private static double Step(double errorArcMin, ExternalCorrectionSettings settings)
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

        /// <summary>
        /// Backlash compensation is only useful when the previous move went the other way. The controller
        /// keeps one sample of history, which is enough for the long move chains this mode produces.
        /// </summary>
        private static ExternalAzimuthDirection? lastAzimuthDirection;

        public static void ResetBacklashHistory() => lastAzimuthDirection = null;

        private static bool IsBacklashDirectionChange(ExternalAzimuthDirection direction)
        {
            var changed = lastAzimuthDirection.HasValue && lastAzimuthDirection.Value != direction;
            lastAzimuthDirection = direction;
            return changed;
        }
    }
}
