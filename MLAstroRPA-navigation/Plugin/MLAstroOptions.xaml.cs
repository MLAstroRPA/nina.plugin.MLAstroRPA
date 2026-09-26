using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MLAstroRPA.Dockables;
using MLAstroRPA.Plugin;
using MLAstroRPA.Services;
 
namespace MLAstroRPA.Plugin
{
    /// <summary>
    /// Code-behind for the MLAstro Options fragment module (body DataTemplates of the CONTROL /
    /// HARDWARE SETTING / CONNECTION tabs). Exported so NINA merges it into the application resources
    /// app-wide; the combined plugin options shell (Options.xaml) references the body templates with
    /// DynamicResource. It is NOT an options page template of its own.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class MLAstroOptions : ResourceDictionary
    {
        public MLAstroOptions()
        {
            InitializeComponent();
        }

        private FrameworkElement? _optionsHeader;
        private FrameworkElement? _optionsTabHost;
        private TranslateTransform? _optionsHeaderTranslate;
        private TranslateTransform? _optionsTabHostTranslate;
        private double _headerTopInContent;
        private ScrollViewer? _optionsOuterScrollViewer;

        private void OnOptionsRootLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement root)
            {
                return;
            }

            _optionsHeader = FindDescendant<HeaderBar>(root) as FrameworkElement;
            if (_optionsHeader != null)
            {
                // Create the transform on the UI thread (Loaded event) and attach it once.
                // Do NOT declare it in XAML (a template Freezable becomes read-only/frozen)
                // and do NOT create it in a field initializer (Options may be constructed on a
                // non-UI thread, making the transform owned by that thread).
                _optionsHeaderTranslate = new TranslateTransform();
                _optionsHeader.RenderTransform = _optionsHeaderTranslate;
            }

            var tabControl = FindDescendant<TabControl>(root);

            if (tabControl != null)
            {
                tabControl.SelectionChanged -= OnOptionsTabSelectionChanged;
                tabControl.SelectionChanged += OnOptionsTabSelectionChanged;
            }

            if (tabControl?.Template.FindName("HeaderHost", tabControl) is FrameworkElement tabHost)
            {
                _optionsTabHost = tabHost;
                _optionsTabHostTranslate = new TranslateTransform();
                tabHost.RenderTransform = _optionsTabHostTranslate;
            }

            // NINA hosts the plugin options inside its own ScrollViewer, so a layout-based
            // fixed header would scroll away. Find the ScrollViewer that scrolls the options
            // and translate the header to keep it pinned to the top of the viewport.
            var scrollViewer = FindOutermostAncestor<ScrollViewer>(root)
                ?? FindAncestor<ScrollViewer>(root);
            if (scrollViewer != null)
            {
                _optionsOuterScrollViewer = scrollViewer;

                if (_optionsHeader != null)
                {
                    // Distance from the top of the scroll content to the header (constant
                    // regardless of scroll). The header is NOT at the very top of the scroll
                    // content because NINA's own plugin page header sits above it.
                    _headerTopInContent = _optionsHeader.TranslatePoint(new Point(0, 0), scrollViewer).Y
                        + scrollViewer.VerticalOffset;
                }

                scrollViewer.ScrollChanged -= OnOuterOptionsScrollChanged;
                scrollViewer.ScrollChanged += OnOuterOptionsScrollChanged;
                UpdateOptionsHeaderPin(scrollViewer);
            }
        }

        private void OnOuterOptionsScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                UpdateOptionsHeaderPin(scrollViewer);
            }
        }

        private void UpdateOptionsHeaderPin(ScrollViewer scrollViewer)
        {
            // Let the header scroll up with the content until its top reaches the top of the
            // viewport, then pin it there (sticky) instead of letting it scroll away. The tab
            // bar is pinned together with the header, staying right below it.
            var y = Math.Max(0, (scrollViewer?.VerticalOffset ?? 0d) - _headerTopInContent);

            if (_optionsHeaderTranslate != null)
            {
                _optionsHeaderTranslate.Y = y;
            }

            if (_optionsTabHostTranslate != null)
            {
                _optionsTabHostTranslate.Y = y;
            }
        }

        private void OnOptionsTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem tabItem)
            {
                return;
            }

            // Bring the view back to the top (HOME) of the newly selected tab: reset
            // both the whole options page scroll (NINA's outer ScrollViewer) and the
            // selected tab's own inner ScrollViewer, after layout has completed.
            tabItem.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!tabItem.IsSelected)
                {
                    return;
                }

                _optionsOuterScrollViewer?.ScrollToTop();
                FindDescendant<ScrollViewer>(tabItem)?.ScrollToTop();
            }));
        }

        private void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FindAncestor<RichTextBox>(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            var scrollViewer = FindOutermostAncestor<ScrollViewer>(e.OriginalSource as DependencyObject)
                ?? sender as ScrollViewer;
            if (scrollViewer == null)
            {
                return;
            }

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3d));
            e.Handled = true;
        }

        private void OnOptionsScrollViewerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            scrollViewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel));
            scrollViewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel), true);
            RegisterPanelMouseWheelHandlers(scrollViewer.Content as DependencyObject);
        }

        private void OnComPortDropDownOpened(object sender, EventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.DataContext is MLAstroController manifest)
            {
                manifest.RefreshComPortsCommand.Execute(null);
            }
        }

        private void OnSerialTerminalInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (sender is TextBox textBox && textBox.DataContext is MLAstroController manifest)
            {
                manifest.SendSerialCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void OnSerialTerminalInputTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox || textBox.DataContext is not MLAstroController manifest || !manifest.IsHexInputEnabled)
            {
                return;
            }

            var sanitizedText = new string((textBox.Text ?? string.Empty)
                .Where(Uri.IsHexDigit)
                .Take(16)
                .ToArray())
                .ToUpperInvariant();

            if (sanitizedText == textBox.Text)
            {
                return;
            }

            var caretIndex = Math.Min(textBox.CaretIndex, sanitizedText.Length);
            textBox.Text = sanitizedText;
            textBox.CaretIndex = caretIndex;
        }

        private void OnNumericOnlyPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text == null || e.Text.Any(c => !char.IsDigit(c));
        }

        private void OnTimingTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            // Commit the pending binding value to the source so it persists immediately
            if (sender is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            // Clear focus so LostFocus also commits, and consume the Enter key
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void OnSerialTerminalResizeThumbDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            // The RichTextBox is a sibling of this Thumb (this element is inside a DataTemplate,
            // so it cannot be referenced by name - locate it through the visual tree instead).
            if (sender is not DependencyObject thumb)
            {
                return;
            }

            var parent = System.Windows.Media.VisualTreeHelper.GetParent(thumb);
            var richTextBox = FindDescendant<RichTextBox>(parent);
            if (richTextBox == null)
            {
                return;
            }

            var newHeight = richTextBox.ActualHeight + e.VerticalChange;
            richTextBox.Height = Math.Max(180, newHeight);
        }

        // ===== SYSTEM LOG (Wireless) - log table in the Web UI style =====
        // A RichTextBox (not a ListBox) so the log can be selected and copied,
        // the background follows the NINA theme (Background=Transparent), and a Thumb resizes the height
        // (it reuses OnSerialTerminalResizeThumbDragDelta, which looks for a RichTextBox in the same panel).
        private sealed class SystemLogBinding
        {
            public System.Collections.ObjectModel.ObservableCollection<MLAstroRPA.Dockables.SystemLogEntry> Source = null!;
            public System.Collections.Specialized.NotifyCollectionChangedEventHandler Handler = null!;
        }

        private void OnSystemLogLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not RichTextBox richTextBox || richTextBox.Tag is SystemLogBinding)
            {
                return;
            }

            if (richTextBox.DataContext is not MLAstroController controller)
            {
                return;
            }

            // The DataTemplate can load again (tab switch) -> drop the old subscription before adding the new one.
            if (richTextBox.Tag is SystemLogBinding previous)
            {
                previous.Source.CollectionChanged -= previous.Handler;
                richTextBox.Tag = null;
            }

            var binding = new SystemLogBinding { Source = controller.SystemLog };
            binding.Handler = (_, args) => SyncSystemLog(richTextBox, binding.Source, args);
            binding.Source.CollectionChanged += binding.Handler;
            richTextBox.Tag = binding;

            richTextBox.Unloaded += (_, _) =>
            {
                binding.Source.CollectionChanged -= binding.Handler;
            };

            RebuildSystemLog(richTextBox, binding.Source);
        }

        /// <summary>
        /// Keeps the document in sync with the collection: a new line is INSERTED AT THE TOP (no full rebuild)
        /// so a selection survives while the user copies the log.
        /// </summary>
        private static void SyncSystemLog(RichTextBox richTextBox,
                                          System.Collections.ObjectModel.ObservableCollection<MLAstroRPA.Dockables.SystemLogEntry> entries,
                                          System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (richTextBox == null || entries == null)
            {
                return;
            }

            try
            {
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                    && args.NewItems != null && args.NewItems.Count == 1 && args.NewStartingIndex == 0)
                {
                    var paragraph = CreateSystemLogParagraph((MLAstroRPA.Dockables.SystemLogEntry)args.NewItems[0]!, IsDarkBackground(richTextBox));
                    var first = richTextBox.Document.Blocks.FirstBlock;
                    if (first == null)
                    {
                        richTextBox.Document.Blocks.Add(paragraph);
                    }
                    else
                    {
                        richTextBox.Document.Blocks.InsertBefore(first, paragraph);
                    }

                    // Trims when the limit is exceeded (the collection trims its tail -> drop the last block).
                    while (richTextBox.Document.Blocks.Count > entries.Count)
                    {
                        var last = richTextBox.Document.Blocks.LastBlock;
                        if (last == null)
                        {
                            break;
                        }
                        richTextBox.Document.Blocks.Remove(last);
                    }
                    return;
                }

                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    var last = richTextBox.Document.Blocks.LastBlock;
                    if (last != null)
                    {
                        richTextBox.Document.Blocks.Remove(last);
                    }
                    return;
                }

                RebuildSystemLog(richTextBox, entries);
            }
            catch (Exception ex)
            {
                NINA.Core.Utility.Logger.Warning($"[MLAstro] System log render failed: {ex.Message}");
            }
        }

        private static void RebuildSystemLog(RichTextBox richTextBox,
                                             System.Collections.Generic.IEnumerable<MLAstroRPA.Dockables.SystemLogEntry> entries)
        {
            var document = new System.Windows.Documents.FlowDocument
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                PagePadding = new Thickness(2)
            };

            // Picks the palette from the real background of the log panel (light or dark theme) so the text always contrasts.
            var darkBackground = IsDarkBackground(richTextBox);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    document.Blocks.Add(CreateSystemLogParagraph(entry, darkBackground));
                }
            }

            richTextBox.Document = document;
            richTextBox.ScrollToHome();
        }

        /// <summary>
        /// One log line is several Runs: the plain content (colour and boldness by level) and the
        /// "(Backlash applied)" phrase in BRIGHT ORANGE - like the Web UI span.log-backlash.
        /// The Web UI highlights exactly that phrase and WRAPS IT IN BRACKETS when the string is just "Backlash applied",
        /// so the same is done here to keep the display identical to the web.
        /// </summary>
        private static System.Windows.Documents.Paragraph CreateSystemLogParagraph(MLAstroRPA.Dockables.SystemLogEntry entry, bool darkBackground)
        {
            var paragraph = new System.Windows.Documents.Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0)
            };

            var levelBrush = MLAstroRPA.Dockables.SystemLogEntry.BrushForLevel(entry.Level, darkBackground);
            var highlightBrush = MLAstroRPA.Dockables.SystemLogEntry.HighlightBrush(darkBackground);
            var text = entry.DisplayText ?? string.Empty;

            const string withParens = "(Backlash applied)";
            const string bare = "Backlash applied";
            var token = text.Contains(withParens) ? withParens : (text.Contains(bare) ? bare : null);

            if (token == null)
            {
                paragraph.Inlines.Add(new System.Windows.Documents.Run(text)
                {
                    Foreground = levelBrush,
                    FontWeight = entry.FontWeightValue
                });
                return paragraph;
            }

            // When the text is just "Backlash applied" (with the bracketed tail) -> the web shows "(Backlash applied)".
            var highlightedText = token == bare ? withParens : token;
            var index = 0;

            while (true)
            {
                var found = text.IndexOf(token, index, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }

                if (found > index)
                {
                    paragraph.Inlines.Add(new System.Windows.Documents.Run(text.Substring(index, found - index))
                    {
                        Foreground = levelBrush,
                        FontWeight = entry.FontWeightValue
                    });
                }

                paragraph.Inlines.Add(new System.Windows.Documents.Run(highlightedText)
                {
                    Foreground = highlightBrush,
                    FontWeight = FontWeights.Bold
                });

                index = found + token.Length;
            }

            if (index < text.Length)
            {
                paragraph.Inlines.Add(new System.Windows.Documents.Run(text.Substring(index))
                {
                    Foreground = levelBrush,
                    FontWeight = entry.FontWeightValue
                });
            }

            return paragraph;
        }

        /// <summary>
        /// Finds out whether the log panel sits on a dark or a light background (walks the visual tree up to the first
        /// solid background brush) so the text colour contrasts with that exact background.
        /// </summary>
        private static bool IsDarkBackground(DependencyObject element)
        {
            try
            {
                var current = element;
                while (current != null)
                {
                    Brush? background = null;
                    if (current is Panel panel) background = panel.Background;
                    else if (current is Border border) background = border.Background;
                    else if (current is Control control) background = control.Background;

                    if (background is SolidColorBrush solid && solid.Color.A > 0)
                    {
                        return Luminance(solid.Color) < 0.5;
                    }

                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
            }

            // Undetermined -> treat it as a dark background (the NINA default) so light text is used.
            return true;
        }

        private static double Luminance(Color color)
            => ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;

        private void OnSerialTerminalLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not RichTextBox richTextBox || richTextBox.Tag is TerminalSubscriptionState)
            {
                return;
            }

            if (richTextBox.DataContext is not MLAstroController manifest)
            {
                return;
            }

            var state = new TerminalSubscriptionState();
            state.Document = CreateTerminalDocument();

            state.TerminalScrollViewer = FindDescendant<ScrollViewer>(richTextBox);
            state.ShouldAutoScroll = true;
            richTextBox.Document = state.Document;

            state.EntryPropertyChangedHandler = (entrySender, _) =>
            {
                if (entrySender is SerialTerminalEntry entry)
                {
                    UpdateTerminalEntry(richTextBox, state, entry);
                }
            };
            state.CollectionChangedHandler = (_, args) =>
            {
                if (args.OldItems != null)
                {
                    foreach (SerialTerminalEntry entry in args.OldItems)
                    {
                        entry.PropertyChanged -= state.EntryPropertyChangedHandler;
                    }
                }

                if (args.NewItems != null)
                {
                    foreach (SerialTerminalEntry entry in args.NewItems)
                    {
                        entry.PropertyChanged += state.EntryPropertyChangedHandler;
                    }
                }

                ApplyTerminalCollectionChanged(richTextBox, state, manifest.SerialTerminalEntries, args);
            };

            state.ScrollChangedHandler = (_, _) =>
            {
                if (state.IsUpdatingScroll || state.TerminalScrollViewer == null)
                {
                    return;
                }

                state.LastVerticalOffset = state.TerminalScrollViewer.VerticalOffset;
                state.ShouldAutoScroll = IsAtTop(state.TerminalScrollViewer);
            };

            foreach (var entry in manifest.SerialTerminalEntries)
            {
                entry.PropertyChanged += state.EntryPropertyChangedHandler;
            }

            manifest.SerialTerminalEntries.CollectionChanged += state.CollectionChangedHandler;
            if (state.TerminalScrollViewer != null)
            {
                state.TerminalScrollViewer.ScrollChanged += state.ScrollChangedHandler;
                state.IsScrollChangedHandlerAttached = true;
            }

            richTextBox.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnSerialTerminalPreviewMouseWheel));
            richTextBox.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnSerialTerminalPreviewMouseWheel), true);
            richTextBox.Tag = state;

            RebuildTerminalDocument(richTextBox, state, manifest.SerialTerminalEntries);
        }

        private void OnSerialTerminalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not RichTextBox richTextBox)
            {
                return;
            }

            var scrollViewer = FindDescendant<ScrollViewer>(richTextBox);
            if (scrollViewer == null)
            {
                return;
            }

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3d));
            e.Handled = true;
        }

        private void OnSerialTerminalPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // HOME = scroll to top, END = scroll to bottom (terminal must have focus)
            if (e.Key != Key.Home && e.Key != Key.End)
            {
                return;
            }

            if (sender is not RichTextBox richTextBox)
            {
                return;
            }

            var scrollViewer = FindDescendant<ScrollViewer>(richTextBox);
            if (scrollViewer == null)
            {
                return;
            }

            if (e.Key == Key.Home)
            {
                scrollViewer.ScrollToTop();
            }
            else
            {
                scrollViewer.ScrollToEnd();
            }

            e.Handled = true;
        }

        private void OnSerialTerminalCopyClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu && contextMenu.PlacementTarget is RichTextBox richTextBox)
            {
                richTextBox.Copy();
            }
        }

        private static FlowDocument CreateTerminalDocument()
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left
            };
        }

        private static void RebuildTerminalDocument(RichTextBox richTextBox, TerminalSubscriptionState state, IEnumerable<SerialTerminalEntry> entries)
        {
            UpdateTerminalView(richTextBox, state, () =>
            {
                state.Document.Blocks.Clear();
                state.EntryParagraphs.Clear();
                state.OrderedEntries.Clear();

                foreach (var entry in entries)
                {
                    InsertTerminalEntry(state, entry, state.OrderedEntries.Count);
                }
            });
        }

        private static void ApplyTerminalCollectionChanged(RichTextBox richTextBox, TerminalSubscriptionState state, IEnumerable<SerialTerminalEntry> entries, NotifyCollectionChangedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                RebuildTerminalDocument(richTextBox, state, entries);
                return;
            }

            UpdateTerminalView(richTextBox, state, () =>
            {
                if (args.OldItems != null)
                {
                    foreach (SerialTerminalEntry entry in args.OldItems)
                    {
                        RemoveTerminalEntry(state, entry);
                    }
                }

                if (args.NewItems != null)
                {
                    var insertIndex = args.NewStartingIndex < 0 ? state.OrderedEntries.Count : args.NewStartingIndex;
                    foreach (SerialTerminalEntry entry in args.NewItems)
                    {
                        InsertTerminalEntry(state, entry, insertIndex++);
                    }
                }
            });
        }

        private static void UpdateTerminalEntry(RichTextBox richTextBox, TerminalSubscriptionState state, SerialTerminalEntry entry)
        {
            if (entry == null || state == null)
            {
                return;
            }

            UpdateTerminalView(richTextBox, state, () =>
            {
                if (!state.EntryParagraphs.TryGetValue(entry, out var paragraph))
                {
                    return;
                }

                paragraph.Inlines.Clear();
                paragraph.Inlines.Add(new Run(entry.Marker + entry.DisplayText));
                paragraph.Margin = new Thickness(0);
                paragraph.Foreground = entry.Foreground;
            });
        }

        private static void UpdateTerminalView(RichTextBox richTextBox, TerminalSubscriptionState state, Action updateAction)
        {
            if (richTextBox == null || state == null || updateAction == null)
            {
                return;
            }

            var scrollViewer = state.TerminalScrollViewer ?? FindDescendant<ScrollViewer>(richTextBox);
            var shouldAutoScroll = state.ShouldAutoScroll;
            var previousVerticalOffset = scrollViewer?.VerticalOffset ?? state.LastVerticalOffset;

            state.IsUpdatingScroll = true;
            try
            {
                updateAction();
                richTextBox.UpdateLayout();

                scrollViewer = state.TerminalScrollViewer ?? FindDescendant<ScrollViewer>(richTextBox);
                if (scrollViewer == null)
                {
                    if (shouldAutoScroll)
                    {
                        richTextBox.ScrollToHome();
                    }

                    return;
                }

                state.TerminalScrollViewer = scrollViewer;

                // Ensure the user-scroll detection handler is attached to the actual
                // ScrollViewer (it may not be available at Loaded time).
                if (!state.IsScrollChangedHandlerAttached)
                {
                    scrollViewer.ScrollChanged += state.ScrollChangedHandler;
                    state.IsScrollChangedHandlerAttached = true;
                }

                if (shouldAutoScroll)
                {
                    // Sticks to the TOP (newest on top). Do NOT recompute ShouldAutoScroll here -
                    // it is only driven by the user's ScrollChanged handler, otherwise a
                    // transient layout state can wrongly flip it off and move the view.
                    scrollViewer.ScrollToTop();
                }
                else
                {
                    // User scrolled away from the top: keep the current view still
                    scrollViewer.ScrollToVerticalOffset(previousVerticalOffset);
                }
            }
            finally
            {
                if (state.TerminalScrollViewer != null)
                {
                    state.LastVerticalOffset = state.TerminalScrollViewer.VerticalOffset;
                }

                state.IsUpdatingScroll = false;
            }
        }

        private static void InsertTerminalEntry(TerminalSubscriptionState state, SerialTerminalEntry entry, int index)
        {
            if (state == null || entry == null)
            {
                return;
            }

            var paragraph = new Paragraph(new Run(entry.Marker + entry.DisplayText))
            {
                Margin = new Thickness(0),
                Foreground = entry.Foreground
            };

            if (index < 0 || index >= state.OrderedEntries.Count)
            {
                state.Document.Blocks.Add(paragraph);
                state.OrderedEntries.Add(entry);
                state.EntryParagraphs[entry] = paragraph;
                return;
            }

            var nextEntry = state.OrderedEntries[index];
            if (state.EntryParagraphs.TryGetValue(nextEntry, out var nextParagraph))
            {
                state.Document.Blocks.InsertBefore(nextParagraph, paragraph);
            }
            else
            {
                state.Document.Blocks.Add(paragraph);
            }

            state.OrderedEntries.Insert(index, entry);
            state.EntryParagraphs[entry] = paragraph;
        }

        private static void RemoveTerminalEntry(TerminalSubscriptionState state, SerialTerminalEntry entry)
        {
            if (state == null || entry == null)
            {
                return;
            }

            if (state.EntryParagraphs.TryGetValue(entry, out var paragraph))
            {
                state.Document.Blocks.Remove(paragraph);
                state.EntryParagraphs.Remove(entry);
            }

            state.OrderedEntries.Remove(entry);
        }

        private static bool IsAtTop(ScrollViewer scrollViewer)
        {
            if (scrollViewer == null)
            {
                return true;
            }

            return scrollViewer.VerticalOffset <= 1;
        }

        private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var result = FindDescendant<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typedChild)
                {
                    return typedChild;
                }

                child = GetParentObject(child);
            }

            return null;
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

        private void RegisterPanelMouseWheelHandlers(DependencyObject? parent)
        {
            if (parent == null || parent is RichTextBox)
            {
                return;
            }

            if (parent is UIElement uiElement)
            {
                uiElement.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel));
                uiElement.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel), true);
            }

            if (parent is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                return;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                RegisterPanelMouseWheelHandlers(VisualTreeHelper.GetChild(parent, i));
            }
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

        private sealed class TerminalSubscriptionState
        {
            public NotifyCollectionChangedEventHandler CollectionChangedHandler { get; set; } = null!;

            public PropertyChangedEventHandler EntryPropertyChangedHandler { get; set; } = null!;

            public ScrollChangedEventHandler ScrollChangedHandler { get; set; } = null!;

            public ScrollViewer? TerminalScrollViewer { get; set; }

            public FlowDocument Document { get; set; } = null!;

            public Dictionary<SerialTerminalEntry, Paragraph> EntryParagraphs { get; } = new();

            public List<SerialTerminalEntry> OrderedEntries { get; } = new();

            public bool ShouldAutoScroll { get; set; }

            public bool IsUpdatingScroll { get; set; }

            public bool IsScrollChangedHandlerAttached { get; set; }

            public double LastVerticalOffset { get; set; }
        }
    }
}
