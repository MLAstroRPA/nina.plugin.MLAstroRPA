using NINA.Core.Utility;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MLAstroRPA.Dockables
{
    public partial class PolarAlignmentDockable : UserControl
    {
        private Button? _activeJogButton;

        public PolarAlignmentDockable()
        {
            Logger.Info("[MLAstro] PolarAlignmentDockable view created");
            InitializeComponent();
        }

        private void OnDockScrollViewerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            RegisterDockPanelMouseWheelHandlers(scrollViewer);
        }

        private void OnDockScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = FindOutermostAncestor<ScrollViewer>(e.OriginalSource as DependencyObject)
                ?? sender as ScrollViewer;

            if (scrollViewer == null)
            {
                return;
            }

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3d));
            e.Handled = true;
        }

        private void RegisterDockPanelMouseWheelHandlers(DependencyObject parent)
        {
            if (parent is UIElement uiElement)
            {
                uiElement.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnDockScrollViewerPreviewMouseWheel));
                uiElement.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnDockScrollViewerPreviewMouseWheel), true);
            }

            if (parent is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                return;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                RegisterDockPanelMouseWheelHandlers(VisualTreeHelper.GetChild(parent, i));
            }
        }

        private static T? FindOutermostAncestor<T>(DependencyObject? child) where T : DependencyObject
        {
            T? result = null;

            while (child != null)
            {
                if (child is T typedChild)
                {
                    result = typedChild;
                }

                child = GetParentObject(child);
            }

            return result;
        }

        private static DependencyObject? GetParentObject(DependencyObject? child)
        {
            if (child == null)
            {
                return null;
            }

            if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(child);
            }

            if (child is FrameworkContentElement frameworkContentElement)
            {
                return frameworkContentElement.Parent ?? ContentOperations.GetParent(frameworkContentElement);
            }

            if (child is FrameworkElement frameworkElement)
            {
                return frameworkElement.Parent;
            }

            return LogicalTreeHelper.GetParent(child);
        }

        #region Movement Event Handlers

        private void OnDockPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_activeJogButton == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var pointerPosition = e.GetPosition(_activeJogButton);
            if (pointerPosition.X < 0 || pointerPosition.Y < 0 ||
                pointerPosition.X > _activeJogButton.ActualWidth ||
                pointerPosition.Y > _activeJogButton.ActualHeight)
            {
                StopActiveJog();
            }
        }

        private void OnMoveUpDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                _activeJogButton = button;
            }
            (DataContext as PolarAlignmentDockVM)?.StartMoveUp();
        }

        private void OnMoveUpUp(object sender, MouseEventArgs e)
        {
            StopActiveJog();
        }

        private void OnMoveDownDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                _activeJogButton = button;
            }
            (DataContext as PolarAlignmentDockVM)?.StartMoveDown();
        }

        private void OnMoveDownUp(object sender, MouseEventArgs e)
        {
            StopActiveJog();
        }

        private void OnMoveLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                _activeJogButton = button;
            }
            (DataContext as PolarAlignmentDockVM)?.StartMoveLeft();
        }

        private void OnMoveLeftUp(object sender, MouseEventArgs e)
        {
            StopActiveJog();
        }

        private void OnMoveRightDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                _activeJogButton = button;
            }
            (DataContext as PolarAlignmentDockVM)?.StartMoveRight();
        }

        private void OnMoveRightUp(object sender, MouseEventArgs e)
        {
            StopActiveJog();
        }

        private void StopActiveJog()
        {
            if (_activeJogButton == null)
            {
                return;
            }

            _activeJogButton = null;
            (DataContext as PolarAlignmentDockVM)?.StopJogMovement();
        }

        #endregion

        #region Relative Settings Event Handlers

        /// <summary>Chỉ cho nhập CHỮ SỐ vào ô Relative D/M/S (giới hạn 0-5 / 0-59 / 0-59 do ViewModel ép).</summary>
        private void OnRelativeDigitsOnly(object sender, TextCompositionEventArgs e)
        {
            foreach (var c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        /// <summary>Enter trong ô D/M/S = chốt giá trị và gửi xuống thiết bị (giống thả nút +/-).</summary>
        private void OnRelativeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
            {
                return;
            }

            e.Handled = true;
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm == null)
            {
                return;
            }

            switch ((sender as FrameworkElement)?.Tag as string)
            {
                case "deg": vm.SendRelativeDegrees(); break;
                case "min": vm.SendRelativeMinutes(); break;
                case "sec": vm.SendRelativeSeconds(); break;
            }
        }

        /// <summary>Bắt đầu sửa bằng bàn phím ⇒ tạm dừng đồng bộ telemetry (giống khi bấm nút +/-).</summary>
        private void OnRelativeFieldGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            (DataContext as PolarAlignmentDockVM)?.StartEditingRelative();
        }

        // Degrees
        private void OnRelativeDegreesIncDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeDegrees++;
            }
        }

        private void OnRelativeDegreesDecDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeDegrees--;
            }
        }

        private void OnRelativeDegreesButtonUp(object sender, MouseButtonEventArgs e)
        {
            (DataContext as PolarAlignmentDockVM)?.SendRelativeDegrees();
        }

        // Minutes
        private void OnRelativeMinutesIncDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeMinutes += 5;
            }
        }

        private void OnRelativeMinutesDecDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeMinutes -= 5;
            }
        }

        private void OnRelativeMinutesButtonUp(object sender, MouseButtonEventArgs e)
        {
            (DataContext as PolarAlignmentDockVM)?.SendRelativeMinutes();
        }

        // Seconds
        private void OnRelativeSecondsIncDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeSeconds += 5;
            }
        }

        private void OnRelativeSecondsDecDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PolarAlignmentDockVM;
            if (vm != null)
            {
                vm.StartEditingRelative();
                vm.RelativeSeconds -= 5;
            }
        }

        private void OnRelativeSecondsButtonUp(object sender, MouseButtonEventArgs e)
        {
            (DataContext as PolarAlignmentDockVM)?.SendRelativeSeconds();
        }

        #endregion

        #region Alignment Editing Event Handlers

        private void OnAlignmentInputGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            (DataContext as PolarAlignmentDockVM)?.StartEditingAlignment();
        }

        /// <summary>
        /// Enter trong ô DMS (Az/Alt Error) → gửi ngay giá trị sai số của trục đó xuống firmware
        /// (firmware ghi FRAM + broadcast cho web UI). Không nhấn Enter thì giá trị vẫn được gửi
        /// khi bấm nút Align như trước. Trục lấy từ Tag="az"/"alt" của TextBox.
        /// </summary>
        private void OnAlignmentInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            var axis = (sender as FrameworkElement)?.Tag as string;
            (DataContext as PolarAlignmentDockVM)?.SendAlignmentAxisError(axis);
            e.Handled = true;
        }

        #endregion
    }
}
