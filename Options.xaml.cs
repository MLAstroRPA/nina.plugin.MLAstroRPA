using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MLAstro_Robotic_Polar_Alignment.Dockables;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// Options ResourceDictionary of the merged MLAstroRPA+TPPA options page. The code-behind
    /// implements the sticky header: the MLAstro header bar (and the 4-tab strip below it) is pinned
    /// to the top of the NINA options viewport while the rest of the options content scrolls
    /// underneath. The DockPanel root of the options template raises Loaded="OnOptionsRootLoaded".
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    partial class Options : ResourceDictionary {
        private FrameworkElement? _optionsHeader;
        private FrameworkElement? _optionsTabHost;
        private TranslateTransform? _optionsHeaderTranslate;
        private TranslateTransform? _optionsTabHostTranslate;
        private double _headerTopInContent;
        private ScrollViewer? _optionsOuterScrollViewer;

        public Options() {
            InitializeComponent();
        }

        private void OnOptionsRootLoaded(object sender, RoutedEventArgs e) {
            if (sender is not FrameworkElement root) {
                return;
            }

            // The shared MLAstro header bar (DockPanel.Dock="Top" inside the template). Attach the
            // TranslateTransform once, at Loaded time (on the UI thread).
            _optionsHeader = FindDescendant<HeaderBar>(root) as FrameworkElement;
            if (_optionsHeader != null) {
                _optionsHeaderTranslate = new TranslateTransform();
                _optionsHeader.RenderTransform = _optionsHeaderTranslate;
            }

            var tabControl = FindDescendant<TabControl>(root);
            if (tabControl != null) {
                tabControl.SelectionChanged -= OnOptionsTabSelectionChanged;
                tabControl.SelectionChanged += OnOptionsTabSelectionChanged;
            }

            // The 4-tab header strip (UniformGrid x:Name="HeaderHost" inside the TabControl template).
            if (tabControl?.Template.FindName("HeaderHost", tabControl) is FrameworkElement tabHost) {
                _optionsTabHost = tabHost;
                _optionsTabHostTranslate = new TranslateTransform();
                tabHost.RenderTransform = _optionsTabHostTranslate;
            }

            // NINA hosts the plugin options inside its own ScrollViewer, so a layout-based fixed
            // header would scroll away. Find the ScrollViewer that scrolls the options and translate
            // header + tab strip to keep them pinned to the top of the viewport.
            var scrollViewer = FindOutermostAncestor<ScrollViewer>(root)
                ?? FindAncestor<ScrollViewer>(root);
            if (scrollViewer != null) {
                _optionsOuterScrollViewer = scrollViewer;

                if (_optionsHeader != null) {
                    // Distance from the top of the scroll content to the header (NINA's own page
                    // header may sit above ours, so it is not necessarily zero).
                    _headerTopInContent = _optionsHeader.TranslatePoint(new Point(0, 0), scrollViewer).Y
                        + scrollViewer.VerticalOffset;
                }

                scrollViewer.ScrollChanged -= OnOuterOptionsScrollChanged;
                scrollViewer.ScrollChanged += OnOuterOptionsScrollChanged;
                UpdateOptionsHeaderPin(scrollViewer);
            }
        }

        private void OnOuterOptionsScrollChanged(object sender, ScrollChangedEventArgs e) {
            if (sender is ScrollViewer scrollViewer) {
                UpdateOptionsHeaderPin(scrollViewer);
            }
        }

        private void UpdateOptionsHeaderPin(ScrollViewer scrollViewer) {
            // Let the header scroll up with the content until its top reaches the top of the
            // viewport, then pin it there (sticky) instead of letting it scroll away. The tab strip
            // is pinned together with the header, staying right below it.
            var y = Math.Max(0, (scrollViewer?.VerticalOffset ?? 0d) - _headerTopInContent);

            if (_optionsHeaderTranslate != null) {
                _optionsHeaderTranslate.Y = y;
            }

            if (_optionsTabHostTranslate != null) {
                _optionsTabHostTranslate.Y = y;
            }
        }

        private void OnOptionsTabSelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem tabItem) {
                return;
            }

            // Bring the view back to the top (HOME) of the newly selected tab: reset both the whole
            // options page scroll (NINA's outer ScrollViewer) and the selected tab's own inner
            // ScrollViewer, after layout has completed.
            tabItem.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (!tabItem.IsSelected) {
                    return;
                }

                _optionsOuterScrollViewer?.ScrollToTop();
                FindDescendant<ScrollViewer>(tabItem)?.ScrollToTop();
            }));
        }

        private static T? FindDescendant<T>(DependencyObject? root) where T : class {
            if (root == null) {
                return null;
            }

            if (root is T direct) {
                return direct;
            }

            if (root is Visual || root is System.Windows.Media.Media3D.Visual3D) {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++) {
                    var result = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
                    if (result != null) {
                        return result;
                    }
                }
            }

            return null;
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : class {
            node = GetVisualOrLogicalParent(node);
            while (node != null) {
                if (node is T match) {
                    return match;
                }
                node = GetVisualOrLogicalParent(node);
            }
            return null;
        }

        private static T? FindOutermostAncestor<T>(DependencyObject? node) where T : class {
            T? outermost = null;
            while (node != null) {
                if (node is T match) {
                    outermost = match;
                }
                node = GetVisualOrLogicalParent(node);
            }
            return outermost;
        }

        private static DependencyObject? GetVisualOrLogicalParent(DependencyObject? node) {
            if (node == null) {
                return null;
            }

            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D) {
                var visualParent = VisualTreeHelper.GetParent(node);
                if (visualParent != null) {
                    return visualParent;
                }
            }

            return node is FrameworkElement fe ? fe.Parent : null;
        }
    }
}
