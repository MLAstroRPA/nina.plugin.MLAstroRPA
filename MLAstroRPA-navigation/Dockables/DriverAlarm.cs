using System;
using System.ComponentModel;
using System.Windows.Media;

namespace MLAstro_Robotic_Polar_Alignment.Dockables
{
    /// <summary>
    /// One row in the industrial-HMI-style alarm history: a firmware error/warning code
    /// that became active at <see cref="ActivatedAt"/> and, once cleared, shows the END
    /// time on the SAME row via <see cref="ClearedAt"/>.
    /// Severity: 1 = WARNING, 2 = ERROR (matches the firmware's error telemetry encoding).
    /// </summary>
    public class DriverAlarm : INotifyPropertyChanged
    {
        public DriverAlarm(string code, string description, int severity)
        {
            Code = code;
            Description = description;
            Severity = severity;
            _activatedAt = DateTime.Now;
        }

        public string Code { get; }

        public string Description { get; }

        /// <summary>1 = WARNING, 2 = ERROR.</summary>
        public int Severity { get; }

        public string SeverityText => Severity == 2 ? "ERROR" : "WARNING";

        public Brush SeverityBrush => Severity == 2 ? Brushes.IndianRed : Brushes.DarkOrange;

        public Brush StateBrush => IsActive ? Brushes.Orange : Brushes.LimeGreen;

        private DateTime _activatedAt;

        public DateTime ActivatedAt
        {
            get => _activatedAt;
            set
            {
                _activatedAt = value;
                OnPropertyChanged(nameof(ActivatedAt));
                OnPropertyChanged(nameof(ActivatedText));
            }
        }

        private DateTime? _clearedAt;

        /// <summary>Null while the alarm is still active; set to the end time when it clears.</summary>
        public DateTime? ClearedAt
        {
            get => _clearedAt;
            set
            {
                _clearedAt = value;
                OnPropertyChanged(nameof(ClearedAt));
                OnPropertyChanged(nameof(ClearedText));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
            }
        }

        public bool IsActive => ClearedAt == null;

        public string StateText => IsActive ? "ACTIVE" : "CLEARED";

        public string ActivatedText => ActivatedAt.ToString("HH:mm:ss");

        public string ClearedText => ClearedAt?.ToString("HH:mm:ss") ?? "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
