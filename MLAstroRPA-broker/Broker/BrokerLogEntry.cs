using System;

namespace MLAstro_Robotic_Polar_Alignment.Broker
{
    /// <summary>Direction of a Broker log line.</summary>
    public enum BrokerLogDirection
    {
        /// <summary>Local notice produced by this plugin (state changes, status text).</summary>
        Notice,

        /// <summary>Message published by this plugin towards TPPA (shown as "RPA -> TPPA").</summary>
        Tx,

        /// <summary>Message received from TPPA (shown as "TPPA -> RPA").</summary>
        Rx
    }

    /// <summary>Payload of <see cref="TppaBrokerClient.Traffic"/>.</summary>
    public sealed class BrokerTrafficEventArgs : EventArgs
    {
        public BrokerTrafficEventArgs(BrokerLogDirection direction, string detail)
        {
            Direction = direction;
            Detail = detail;
        }

        public BrokerLogDirection Direction { get; }

        public string Detail { get; }
    }

    /// <summary>One line of the Broker log shown on the options page.</summary>
    public sealed class BrokerLogEntry
    {
        public BrokerLogEntry(BrokerLogDirection direction, string message)
        {
            Direction = direction;
            Text = string.Format("{0:HH:mm:ss}  {1}{2}", DateTime.Now, Marker(direction), message);
        }

        public BrokerLogDirection Direction { get; }

        /// <summary>Timestamp + direction marker + message, ready to display.</summary>
        public string Text { get; }

        private static string Marker(BrokerLogDirection direction)
        {
            switch (direction)
            {
                case BrokerLogDirection.Tx: return "RPA \u2192 TPPA  ";   // sent by this plugin
                case BrokerLogDirection.Rx: return "TPPA \u2192 RPA  ";   // received from TPPA
                default: return "\u00b7   ";                               // local notice
            }
        }
    }

    /// <summary>Builds the Broker log text of an envelope received from TPPA.</summary>
    public static class BrokerTrafficText
    {
        public static string Received(ExternalCorrectionEnvelope envelope)
        {
            if (envelope == null) { return "malformed message"; }

            if (string.Equals(envelope.Kind, TppaBrokerKind.Measurement, StringComparison.Ordinal))
            {
                var measurement = envelope.PayloadAs<TppaMeasurement>();
                if (measurement != null)
                {
                    return string.Format("{0} (az {1:0.##}', alt {2:0.##}', total {3:0.##}', tol {4:0.##}')",
                                         envelope.Kind,
                                         measurement.AzimuthErrorArcMin,
                                         measurement.AltitudeErrorArcMin,
                                         measurement.TotalErrorArcMin,
                                         measurement.ToleranceArcMin);
                }
            }

            return envelope.Kind;
        }
    }
}
