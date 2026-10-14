using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MLAstroRPA.Plugin
{
    /// <summary>
    /// Invisible window that lies over the NINA window while the confirmation dialog is open. The dialog is modal, so
    /// Windows disables the NINA window and drops every click that lands on it - the operator only hears the warning
    /// sound and nothing in this process ever sees that click. This shield is enabled, so those clicks arrive here
    /// instead, which is what lets the dialog shake and come back to the front.
    /// </summary>
    internal sealed class HardwareBusyClickShield : Window
    {
        private readonly Window host;

        /// <summary>Raised for every click that lands next to the dialog.</summary>
        public event EventHandler Clicked;

        /// <summary>
        /// The dialog this shield belongs to. It lies above this window, and a click that falls inside its frame belongs
        /// to the dialog: it is not a click next to it and must not shake it.
        /// </summary>
        public Window Dialog { get; set; }

        public HardwareBusyClickShield(Window host)
        {
            this.host = host;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            // Invisible, but a background that is not null is what makes the window take the clicks.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // Above the NINA window: a window that is shown without being activated is otherwise placed underneath the
            // window that has the focus, which is exactly the window whose clicks have to be caught here.
            Topmost = true;

            if (host != null)
            {
                host.LocationChanged += OnHostMoved;
                host.SizeChanged += OnHostMoved;
                host.StateChanged += OnHostMoved;
            }

            MouseDown += (_, e) => {
                if (IsInsideDialog(e)) { return; }

                Clicked?.Invoke(this, EventArgs.Empty);
            };

            Closed += (_, __) => {
                if (host == null) { return; }

                host.LocationChanged -= OnHostMoved;
                host.SizeChanged -= OnHostMoved;
                host.StateChanged -= OnHostMoved;
            };
        }

        /// <summary>
        /// Fits the shield over the window it protects, so the clicks of the operator land on it. A window that is
        /// maximised has no useful bounds of its own, so the work area is used for it.
        /// </summary>
        public void Follow()
        {
            var hostBounds = host != null && host.WindowState != WindowState.Maximized && host.ActualWidth > 1
                ? new Rect(host.Left, host.Top, host.ActualWidth, host.ActualHeight)
                : new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

            Left = hostBounds.Left;
            Top = hostBounds.Top;
            Width = Math.Max(1, hostBounds.Width);
            Height = Math.Max(1, hostBounds.Height);
        }


        /// <summary>True when the click fell on the frame of the dialog instead of next to it.</summary>
        private bool IsInsideDialog(MouseButtonEventArgs e)
        {
            if (Dialog == null) { return false; }

            var click = PointToScreen(e.GetPosition(this));
            var topLeft = Dialog.PointToScreen(new Point(0, 0));
            var bottomRight = Dialog.PointToScreen(new Point(Dialog.ActualWidth, Dialog.ActualHeight));

            return click.X >= topLeft.X && click.X <= bottomRight.X
                   && click.Y >= topLeft.Y && click.Y <= bottomRight.Y;
        }
        private void OnHostMoved(object sender, EventArgs e)
        {
            Follow();
        }
    }
}
