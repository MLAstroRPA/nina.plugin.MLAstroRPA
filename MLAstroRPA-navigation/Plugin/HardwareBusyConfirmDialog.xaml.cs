using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MLAstroRPA.Plugin
{
    /// <summary>Snapshot of the controller the confirmation dialog follows while it is open.</summary>
    public readonly record struct HardwareBusyLiveState(
        string Status,
        string LinkPath,
        double AzimuthDegrees,
        string AzimuthText,
        double AltitudeDegrees,
        string AltitudeText,
        bool AxesAreReady);

    /// <summary>
    /// Confirmation for the moment an alignment run starts while the MLAstro axes are still turning. It is a dialog of
    /// the plugin, drawn with the brushes of NINA's theme, that follows the controller live - its status, link and both
    /// axis angles - and closes itself as soon as the axes finish their travel, because nothing is left to stop then. It
    /// also closes itself once the readiness window of the run is over: from that moment the answer cannot change
    /// anything any more.
    /// <para>
    /// It is shown modeless, so a click next to it still reaches the NINA window: the dialog then shakes to show that
    /// this question has to be answered, and comes back to the front.
    /// </para>
    /// </summary>
    public partial class HardwareBusyConfirmDialog : Window
    {
        /// <summary>
        /// TPPA's readiness window (its <c>ReadyTimeoutMs</c>): a later answer changes nothing, because the run has
        /// already ended with "the controller is not ready".
        /// </summary>
        private const int DefaultAnswerTimeoutSeconds = 30;

        /// <summary>The firmware reports about four times a second, so this is live enough without being busy work.</summary>
        private static readonly TimeSpan LiveUpdateInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Offsets of the shake that reminds the operator that this question still has to be answered.</summary>
        private static readonly double[] ShakeOffsets = { -10, 10, -8, 8, -4, 4, 0 };

        private readonly Func<HardwareBusyLiveState> readState;
        private readonly DispatcherTimer liveTimer;
        private DispatcherTimer shakeTimer;
        private int liveTicks;
        private int secondsLeft;

        /// <summary>The answer of the operator, or null while the dialog is still open.</summary>
        public bool? Answer { get; private set; }

        /// <summary>
        /// True when the dialog closed because the axes finished their travel on their own: there is nothing left to
        /// stop, so the caller may continue with the run.
        /// </summary>
        public bool NothingToStop { get; private set; }

        public HardwareBusyConfirmDialog(Func<HardwareBusyLiveState> readState, int secondsToAnswer = DefaultAnswerTimeoutSeconds)
        {
            this.readState = readState ?? throw new ArgumentNullException(nameof(readState));

            InitializeComponent();

            secondsLeft = Math.Max(1, secondsToAnswer);
            UpdateCountdown();

            liveTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LiveUpdateInterval };
            liveTimer.Tick += OnLiveTick;

            Loaded += (_, __) => {
                UpdateLiveState();
                liveTimer.Start();
            };
            Closed += (_, __) => {
                liveTimer.Stop();
                shakeTimer?.Stop();
            };
            Deactivated += OnDeactivated;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnLiveTick(object sender, EventArgs e)
        {
            var state = UpdateLiveState();

            if (state.AxesAreReady)
            {
                // The travel ended by itself while the operator was deciding, so there is nothing left to stop.
                NothingToStop = true;
                liveTimer.Stop();
                HeadlineText.Text = "The MLAstro axes finished their travel. Nothing has to be stopped any more.";
                CloseWith(true);
                return;
            }

            // The countdown runs at a quarter of the poll rate, i.e. in seconds.
            if (++liveTicks < 4) { return; }

            liveTicks = 0;
            if (--secondsLeft > 0)
            {
                UpdateCountdown();
                return;
            }

            // The readiness window of the run is over: answer like the operator skipped it, so the axes stay as they are.
            liveTimer.Stop();
            CloseWith(false);
        }

        private HardwareBusyLiveState UpdateLiveState()
        {
            HardwareBusyLiveState state;
            try { state = readState(); }
            catch (Exception) { return default; }

            StatusText.Text = $"STATUS: {(string.IsNullOrWhiteSpace(state.Status) ? "unknown" : state.Status)}";
            LinkText.Text = string.IsNullOrWhiteSpace(state.LinkPath) ? "Link: unknown" : $"Link: {state.LinkPath}";
            AzText.Text = $"Azimuth  {state.AzimuthText ?? "-"}   ({state.AzimuthDegrees:0.00}°)";
            AltText.Text = $"Altitude  {state.AltitudeText ?? "-"}   ({state.AltitudeDegrees:0.00}°)";

            return state;
        }

        private void UpdateCountdown()
        {
            CountdownText.Text = $"Closes itself in {secondsLeft} s and skips the stop: the axes stay as they are.";
        }

        /// <summary>
        /// Answers the question and closes. The dialog is shown modeless, so the answer travels through
        /// <see cref="Answer"/> instead of <c>DialogResult</c>, which only a modal window has.
        /// </summary>
        private void CloseWith(bool answer)
        {
            Answer = answer;
            Close();
        }

        /// <summary>
        /// The operator clicked next to the dialog, so it shakes to show that this question still has to be answered,
        /// and comes back to the front. That is only possible while the NINA window stays enabled, which is why the
        /// dialog is modeless.
        /// </summary>
        private void OnDeactivated(object sender, EventArgs e)
        {
            if (!IsLoaded || Answer.HasValue) { return; }

            StartShake();
            try { Activate(); } catch (InvalidOperationException) { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) { return; }

            e.Handled = true;
            liveTimer.Stop();
            CloseWith(false);
        }

        /// <summary>
        /// Called when the operator clicked next to the dialog (the click shield caught it): the dialog shakes to show
        /// that this question still has to be answered, and comes back to the front above that shield.
        /// </summary>
        public void ShakeAndBringToFront()
        {
            if (Answer.HasValue) { return; }

            StartShake();
            try { Activate(); } catch (InvalidOperationException) { }
        }

        private void StartShake()
        {
            if (shakeTimer != null) { return; }

            // The same sound Windows plays when a click lands on a window that is locked by a dialog: the lock keeps the
            // click, so the reminder has to be heard here instead of being made by the shell.
            try { System.Media.SystemSounds.Beep.Play(); } catch (InvalidOperationException) { }

            var startLeft = Left;
            var index = 0;
            shakeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            shakeTimer.Tick += (_, __) => {
                if (index >= ShakeOffsets.Length)
                {
                    shakeTimer.Stop();
                    shakeTimer = null;
                    Left = startLeft;
                    return;
                }

                Left = startLeft + ShakeOffsets[index++];
            };
            shakeTimer.Start();
        }

        private void OnDragWindow(object sender, MouseButtonEventArgs e)
        {
            // The dialog has no title bar of its own, so the operator can still drag it out of the way.
            if (e.ButtonState != MouseButtonState.Pressed) { return; }

            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            liveTimer.Stop();
            CloseWith(true);
        }

        private void OnSkipClick(object sender, RoutedEventArgs e)
        {
            liveTimer.Stop();
            CloseWith(false);
        }
    }
}
