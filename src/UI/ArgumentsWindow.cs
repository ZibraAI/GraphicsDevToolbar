using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using GraphicsDevToolbar.Services;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace GraphicsDevToolbar.UI
{
    /// <summary>
    /// Item selected in the configuration list of <see cref="ArgumentsWindow"/>: 
    /// the active configuration (whichever it is when the window opens),
    /// a specific one,
    /// or all of them.
    /// </summary>
    internal sealed class ConfigSelection
    {
        public static readonly ConfigSelection Active = new ConfigSelection(null, false);
        public static readonly ConfigSelection All = new ConfigSelection(null, true);

        private ConfigSelection(string config, bool isAll)
        {
            Config = config;
            IsAll = isAll;
        }

        public static ConfigSelection Specific(string config) => new ConfigSelection(config, false);

        /// <summary>Name of a specific configuration, or null.</summary>
        public string Config { get; }

        public bool IsAll { get; }
    }

    /// <summary>
    /// Dialog for editing the startup project's command line arguments as a list of toggleable lines, per configuration.
    /// Edits <c>lines</c> in place; the caller saves them after the window closes if <see cref="HasChanges"/> is set, so
    /// there is no OK/Cancel.
    /// </summary>
    internal sealed class ArgumentsWindow : DialogWindow
    {
        private const string AllConfigurationsLabel = "All Configurations";
        private const string DifferentOptionsText = "<different options>";

        private readonly List<string> _configNames;
        private readonly string _activeConfig;
        /// <summary>Lines of every configuration, edited in place; entries are replaced, never modified.</summary>
        private readonly Dictionary<string, List<ArgumentLine>> _linesPerConfig;
        private readonly Dictionary<string, List<ArgumentLine>> _differingSavedLines;
        private readonly Func<string, string, string> _evaluate;

        private readonly ComboBox _configSelector;
        private readonly DockPanel _restorePanel;
        private readonly TextBox _restorePreview;
        private readonly StackPanel _rowsPanel = new StackPanel();
        private readonly StackPanel _configsPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBox _preview;
        private readonly StackPanel _evaluatedPanel;
        private readonly TextBox _evaluatedPreview;
        private readonly List<Row> _rows = new List<Row>();
        private InsertionAdorner _insertionAdorner;

        /// <summary>Configuration currently shown in the editor, or null for "All Configurations".</summary>
        private string _scope;
        private bool _editorDirty;
        private bool _loading;

        /// <summary>ComboBox item. The VS themed ComboBox style binds automation properties to a Text property.</summary>
        private sealed class ConfigItem
        {
            public ConfigItem(string text, string config, ConfigSelection selection)
            {
                Text = text;
                Config = config;
                Selection = selection;
            }

            public string Text { get; }

            /// <summary>Configuration name, or null for "All Configurations".</summary>
            public string Config { get; }

            public ConfigSelection Selection { get; }

            public override string ToString() => Text;
        }

        private sealed class Row
        {
            public DockPanel Panel;
            public Border Handle;
            public CheckBox Check;
            public TextBox Text;
            public Button Delete;

            /// <summary>
            /// Stands for differing lines under "All Configurations"; contributes no lines until edited, then becomes a
            /// normal row.
            /// </summary>
            public bool DifferentOptions;
        }

        /// <summary>Draws the line where a dragged row would be inserted.</summary>
        private sealed class InsertionAdorner : Adorner
        {
            private double _y;

            public InsertionAdorner(UIElement adornedElement) : base(adornedElement)
            {
                // Must not take the drag events away from the rows below it.
                IsHitTestVisible = false;
            }

            public double Y
            {
                get => _y;
                set
                {
                    if (_y == value)
                        return;
                    _y = value;
                    InvalidateVisual();
                }
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                var c = VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightColorKey);
                var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
                drawingContext.DrawLine(new Pen(brush, 2), new Point(0, _y), new Point(AdornedElement.RenderSize.Width, _y));
                drawingContext.DrawEllipse(brush, null, new Point(3, _y), 3, 3);
            }
        }

        /// <param name="selection">Configuration to show first; the active one if it doesn't exist (anymore).</param>
        /// <param name="lines">Lines for every configuration in <paramref name="configNames"/>; edited in place.</param>
        /// <param name="differingSavedLines">Saved lines that differ from the project's arguments, per configuration; offered for restoring.</param>
        /// <param name="evaluate">
        /// Expands the macros in a command line (second argument) for a configuration (first argument); returns null if
        /// it can't.
        /// </param>
        public ArgumentsWindow(string projectName, IEnumerable<string> configNames, string activeConfig, ConfigSelection selection,
            Dictionary<string, List<ArgumentLine>> lines, Dictionary<string, List<ArgumentLine>> differingSavedLines,
            Func<string, string, string> evaluate)
        {
            _configNames = new List<string>(configNames);
            if (activeConfig != null && _configNames.Contains(activeConfig))
                _activeConfig = activeConfig;
            else if (_configNames.Count > 0)
                _activeConfig = _configNames[0];
            _linesPerConfig = lines;
            _differingSavedLines = differingSavedLines;
            _evaluate = evaluate;

            Title = "Command Line Arguments - " + projectName;
            Width = 640;
            Height = 420;
            MinWidth = 380;
            MinHeight = 260;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = true;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new DockPanel { Margin = new Thickness(10) };

            // Configuration selector.
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var configLabel = new Label { Content = "Configuration:", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 8, 0) };
            configLabel.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogLabelStyleKey);
            DockPanel.SetDock(configLabel, Dock.Left);
            top.Children.Add(configLabel);
            _configSelector = new ComboBox();
            _configSelector.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogComboBoxStyleKey);
            // Same layout as the Property Pages: the active configuration first, then each one, then all.
            if (_activeConfig != null)
                _configSelector.Items.Add(new ConfigItem($"Active({_activeConfig})", _activeConfig, ConfigSelection.Active));
            foreach (var c in _configNames)
                _configSelector.Items.Add(new ConfigItem(c, c, ConfigSelection.Specific(c)));
            _configSelector.Items.Add(new ConfigItem(AllConfigurationsLabel, null, ConfigSelection.All));
            _configSelector.SelectedIndex = IndexOfSelection(selection);
            _configSelector.SelectionChanged += (s, e) => OnScopeChanged();
            top.Children.Add(_configSelector);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // The command line with macros expanded, as the debugger receives it; shown only when that differs.
            _evaluatedPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var evaluatedLabel = new Label { Content = "Evaluated command line:", Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };
            evaluatedLabel.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogLabelStyleKey);
            _evaluatedPanel.Children.Add(evaluatedLabel);
            _evaluatedPreview = CreateCommandLineBox();
            _evaluatedPanel.Children.Add(_evaluatedPreview);
            DockPanel.SetDock(_evaluatedPanel, Dock.Bottom);
            root.Children.Add(_evaluatedPanel);

            // Preview of the resulting command line.
            _preview = CreateCommandLineBox();
            var previewLabel = new Label { Content = "Resulting command line:", Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };
            previewLabel.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogLabelStyleKey);
            DockPanel.SetDock(_preview, Dock.Bottom);
            DockPanel.SetDock(previewLabel, Dock.Bottom);
            root.Children.Add(_preview);
            root.Children.Add(previewLabel);

            // Saved lines that differ from the project's arguments; offered while they differ from the edited ones.
            _restorePanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var restoreLabel = new Label { Content = "Saved command line (differs from the project's):", Padding = new Thickness(0) };
            restoreLabel.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogLabelStyleKey);
            DockPanel.SetDock(restoreLabel, Dock.Top);
            _restorePanel.Children.Add(restoreLabel);
            // No minimum height or vertical padding, so the row height comes from the box and the button stretches to
            // match it (and the box keeps the same height as the resulting command line box).
            var restoreButton = new Button { Content = "Restore", Margin = new Thickness(6, 2, 0, 0), ToolTip = "Restore the saved lines" };
            restoreButton.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
            restoreButton.MinHeight = 0;
            restoreButton.Padding = new Thickness(10, 0, 10, 0);
            restoreButton.Click += (s, e) => RestoreSaved();
            DockPanel.SetDock(restoreButton, Dock.Right);
            _restorePanel.Children.Add(restoreButton);
            _restorePreview = CreateCommandLineBox();
            _restorePanel.Children.Add(_restorePreview);
            DockPanel.SetDock(_restorePanel, Dock.Bottom);
            root.Children.Add(_restorePanel);

            // Lines, followed by the per-configuration command lines while they differ under "All Configurations".
            var linesPanel = new StackPanel();
            linesPanel.Children.Add(_rowsPanel);
            linesPanel.Children.Add(_configsPanel);
            var scroll = new ScrollViewer
            {
                Content = linesPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            scroll.SetResourceReference(StyleProperty, VsResourceKeys.ScrollViewerStyleKey);
            // Drops are handled by the scroll viewer rather than the rows panel, so that the empty space below the rows
            // accepts them too; the transparent background makes that space hit-testable. Preview events, because the
            // text boxes would otherwise handle the drag as a text drop.
            scroll.Background = Brushes.Transparent;
            scroll.AllowDrop = true;
            scroll.PreviewDragEnter += OnRowDragOver;
            scroll.PreviewDragOver += OnRowDragOver;
            scroll.PreviewDragLeave += (s, e) =>
            {
                var p = e.GetPosition(scroll);
                if (p.X < 0 || p.Y < 0 || p.X >= scroll.ActualWidth || p.Y >= scroll.ActualHeight)
                    HideInsertionLine();
            };
            scroll.PreviewDrop += OnRowDrop;
            root.Children.Add(scroll);

            Content = root;

            LoadScope((_configSelector.SelectedItem as ConfigItem)?.Config);
            Loaded += (s, e) => FocusRow(0);
            SourceInitialized += (s, e) => ApplyThemeToWindow();
            VSColorTheme.ThemeChanged += OnThemeChanged;
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };
            Closed += (s, e) =>
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                if (HasChanges)
                    CommitEditor();
            };
        }

        /// <summary>
        /// Whether the lines were changed, so that they need to be saved. Read after the window is closed, when the
        /// lines passed to the constructor hold the final state.
        /// </summary>
        public bool HasChanges { get; private set; }

        /// <summary>Configuration selected in the list, e.g. to open the window with it again.</summary>
        public ConfigSelection Selection => (_configSelector.SelectedItem as ConfigItem)?.Selection ?? ConfigSelection.Active;

        /// <summary>Index of the list item for <paramref name="selection"/>; the active configuration's if there is none.</summary>
        private int IndexOfSelection(ConfigSelection selection)
        {
            for (var i = 0; i < _configSelector.Items.Count; i++)
            {
                var item = (ConfigItem)_configSelector.Items[i];
                if (selection.IsAll ? item.Selection.IsAll
                    : selection.Config != null && string.Equals(item.Selection.Config, selection.Config, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        /// <summary>Read-only box showing a command line.</summary>
        private static TextBox CreateCommandLineBox()
        {
            var box = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 2, 0, 0),
                MaxHeight = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            box.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogTextBoxStyleKey);
            return box;
        }

        /// <summary>Records an edit of the rows.</summary>
        private void MarkChanged()
        {
            _editorDirty = true;
            HasChanges = true;
        }

        #region Scope handling

        private void OnScopeChanged()
        {
            if (_loading)
                return;
            CommitEditor();
            LoadScope((_configSelector.SelectedItem as ConfigItem)?.Config);
        }

        /// <summary>Stores the editor contents into <see cref="_linesPerConfig"/> for the current scope.</summary>
        private void CommitEditor()
        {
            var lines = EditorLines();
            if (_scope != null)
            {
                _linesPerConfig[_scope] = lines;
            }
            else if (_editorDirty)
            {
                // Only overwrite every configuration if the user actually edited while "All Configurations" was selected.
                // Sharing the list is fine: lists are replaced, never modified.
                foreach (var cfg in _configNames)
                    _linesPerConfig[cfg] = lines;
            }
            _editorDirty = false;
        }

        private void LoadScope(string scope)
        {
            _scope = scope;
            List<ArgumentLine> lines;
            var differ = false;
            if (scope == null)
            {
                lines = _configNames.Count > 0 ? _linesPerConfig[_configNames[0]] : new List<ArgumentLine>();
                foreach (var cfg in _configNames)
                {
                    if (!ArgumentStore.SameLines(_linesPerConfig[cfg], lines))
                    {
                        differ = true;
                        break;
                    }
                }
            }
            else
            {
                lines = _linesPerConfig[scope];
            }

            _loading = true;
            try
            {
                _rows.Clear();
                _rowsPanel.Children.Clear();
                if (differ)
                {
                    var row = AddRow(0, DifferentOptionsText, true);
                    row.DifferentOptions = true;
                    row.Text.FontStyle = FontStyles.Italic;
                }
                else
                {
                    foreach (var line in lines)
                        AddRow(_rows.Count, line.Text, line.Enabled);
                }
                EnsureTrailingEmptyRow();
            }
            finally
            {
                _loading = false;
            }
            LoadConfigurationPreviews(differ);
            _editorDirty = false;
            UpdatePreview();
        }

        /// <summary>
        /// Under "All Configurations" with differing lines, lists the command line of each configuration (configurations
        /// with the same lines share an entry), each with a button that copies its lines to all configurations.
        /// </summary>
        private void LoadConfigurationPreviews(bool differ)
        {
            _configsPanel.Children.Clear();
            if (!differ)
                return;

            var groups = new List<List<string>>();
            foreach (var cfg in _configNames)
            {
                List<string> group = null;
                foreach (var g in groups)
                {
                    if (ArgumentStore.SameLines(_linesPerConfig[g[0]], _linesPerConfig[cfg]))
                    {
                        group = g;
                        break;
                    }
                }
                if (group == null)
                    groups.Add(group = new List<string>());
                group.Add(cfg);
            }

            foreach (var group in groups)
            {
                // Captured now: the lines may be replaced by edits of the placeholder before the button is clicked.
                var lines = _linesPerConfig[group[0]];
                var label = new Label { Content = string.Join(", ", group) + ":", Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };
                label.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogLabelStyleKey);
                _configsPanel.Children.Add(label);

                var entry = new DockPanel();
                // Same layout as the Restore button: the button stretches to the height of the box.
                var copyButton = new Button
                {
                    Content = "Use for All",
                    Margin = new Thickness(6, 2, 0, 0),
                    ToolTip = "Replace the lines of all configurations with these (including disabled lines)",
                };
                copyButton.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
                copyButton.MinHeight = 0;
                copyButton.Padding = new Thickness(10, 0, 10, 0);
                copyButton.Click += (s, e) => CopyToAllConfigurations(lines);
                DockPanel.SetDock(copyButton, Dock.Right);
                entry.Children.Add(copyButton);
                var box = CreateCommandLineBox();
                box.Text = ArgumentStore.BuildCommandLine(lines);
                entry.Children.Add(box);
                _configsPanel.Children.Add(entry);
            }
        }

        private void CopyToAllConfigurations(List<ArgumentLine> lines)
        {
            // Sharing the list is fine: lists are replaced, never modified.
            foreach (var cfg in _configNames)
                _linesPerConfig[cfg] = lines;
            LoadScope(_scope);
            HasChanges = true;
        }

        /// <summary>Saved lines of the configurations in the current scope that differ from their edited lines.</summary>
        private Dictionary<string, List<ArgumentLine>> RestorableLines()
        {
            var result = new Dictionary<string, List<ArgumentLine>>(StringComparer.OrdinalIgnoreCase);
            var editor = _scope != null || _editorDirty ? EditorLines() : null;
            foreach (var cfg in _scope != null ? new List<string> { _scope } : _configNames)
            {
                if (_differingSavedLines.TryGetValue(cfg, out var saved) && !ArgumentStore.SameLines(saved, editor ?? _linesPerConfig[cfg]))
                    result[cfg] = saved;
            }
            return result;
        }

        private void UpdateRestore()
        {
            var restorable = RestorableLines();
            string commandLine = null;
            foreach (var lines in restorable.Values)
            {
                var current = ArgumentStore.BuildCommandLine(lines);
                if (commandLine != null && commandLine != current)
                {
                    commandLine = DifferentOptionsText;
                    break;
                }
                commandLine = current;
            }
            _restorePreview.Text = commandLine ?? "";
            _restorePanel.Visibility = restorable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RestoreSaved()
        {
            var restorable = RestorableLines();
            CommitEditor();
            foreach (var kv in restorable)
                _linesPerConfig[kv.Key] = kv.Value;
            LoadScope(_scope);
            HasChanges = true;
        }

        #endregion

        #region Rows

        private List<ArgumentLine> EditorLines()
        {
            var lines = new List<ArgumentLine>();
            foreach (var row in _rows)
            {
                if (!row.DifferentOptions && !IsBlank(row))
                    lines.Add(new ArgumentLine { Enabled = row.Check.IsChecked == true, Text = row.Text.Text });
            }
            return lines;
        }

        private bool HasDifferentOptionsRow()
        {
            foreach (var row in _rows)
            {
                if (row.DifferentOptions)
                    return true;
            }
            return false;
        }

        private static bool IsBlank(Row row) => string.IsNullOrWhiteSpace(row.Text.Text);

        private Row AddRow(int index, string text, bool enabled)
        {
            var row = new Row { Panel = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true } };

            AddDragHandle(row);

            row.Check = new CheckBox { IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Enable this line" };
            row.Check.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogCheckBoxStyleKey);
            row.Check.Checked += (s, e) => OnRowChanged();
            row.Check.Unchecked += (s, e) => OnRowChanged();
            DockPanel.SetDock(row.Check, Dock.Left);
            row.Panel.Children.Add(row.Check);

            row.Delete = new Button
            {
                Content = new CrispImage { Moniker = KnownMonikers.Close, Width = 16, Height = 16 },
                MinWidth = 0,
                MinHeight = 0,
                Width = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Remove line",
                Focusable = false,
            };
            row.Delete.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
            row.Delete.Click += (s, e) => RemoveRow(row, _rows.IndexOf(row));
            DockPanel.SetDock(row.Delete, Dock.Right);
            row.Panel.Children.Add(row.Delete);

            row.Text = new TextBox { Text = text ?? "", FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center };
            row.Text.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogTextBoxStyleKey);
            row.Text.TextChanged += (s, e) =>
            {
                if (!_loading && row.DifferentOptions)
                {
                    row.DifferentOptions = false;
                    row.Text.FontStyle = FontStyles.Normal;
                }
                OnRowChanged();
            };
            row.Text.GotKeyboardFocus += (s, e) =>
            {
                // Typing replaces the placeholder.
                if (row.DifferentOptions)
                    row.Text.SelectAll();
            };
            row.Text.LostKeyboardFocus += (s, e) => RemoveIfBlank(row);
            row.Text.PreviewKeyDown += (s, e) => OnRowKeyDown(row, e);
            row.Panel.Children.Add(row.Text);

            _rows.Insert(index, row);
            _rowsPanel.Children.Insert(index, row.Panel);
            UpdateRowStates();
            return row;
        }

        #endregion

        #region Drag and drop

        /// <summary>Adds the grip that starts dragging the row. The text box can't start drags, as it uses the mouse for selection.</summary>
        private void AddDragHandle(Row row)
        {
            // The dotted grip of VS toolbars, in the theme's colors. The transparent padding widens the grab area.
            var grip = new Rectangle { Width = 4, Height = 16 };
            grip.SetResourceReference(Shape.FillProperty, EnvironmentColors.CommandBarDragHandleHorizontalTextureBrushKey);
            row.Handle = new Border
            {
                Background = Brushes.Transparent,
                Child = grip,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder",
            };
            Point? dragStart = null;
            row.Handle.MouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(row.Handle);
                row.Handle.CaptureMouse();
                e.Handled = true;
            };
            row.Handle.MouseMove += (s, e) =>
            {
                if (dragStart == null || e.LeftButton != MouseButtonState.Pressed)
                    return;
                var delta = e.GetPosition(row.Handle) - dragStart.Value;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                dragStart = null;
                row.Handle.ReleaseMouseCapture();
                try
                {
                    DragDrop.DoDragDrop(row.Handle, new DataObject(typeof(Row), row), DragDropEffects.Move);
                }
                finally
                {
                    HideInsertionLine();
                }
            };
            row.Handle.MouseLeftButtonUp += (s, e) =>
            {
                dragStart = null;
                row.Handle.ReleaseMouseCapture();
            };
            DockPanel.SetDock(row.Handle, Dock.Left);
            row.Panel.Children.Add(row.Handle);
        }

        /// <summary>Returns the row being dragged, or null if the drag isn't one of this window's rows.</summary>
        private Row DraggedRow(DragEventArgs e)
        {
            var row = e.Data.GetData(typeof(Row)) as Row;
            return row != null && _rows.Contains(row) ? row : null;
        }

        private void OnRowDragOver(object sender, DragEventArgs e)
        {
            if (DraggedRow(e) == null)
                return;
            e.Effects = DragDropEffects.Move;
            ShowInsertionLine(InsertionIndex(e));
            e.Handled = true;
        }

        private void OnRowDrop(object sender, DragEventArgs e)
        {
            var row = DraggedRow(e);
            if (row == null)
                return;
            HideInsertionLine();
            MoveRow(row, InsertionIndex(e));
            e.Handled = true;
        }

        /// <summary>
        /// Index of the row before which the dragged row would be inserted: the row whose upper half is under the mouse.
        /// The trailing blank row always stays last, so this is at most its index.
        /// </summary>
        private int InsertionIndex(DragEventArgs e)
        {
            var y = e.GetPosition(_rowsPanel).Y;
            var last = _rows.Count - 1;
            for (var i = 0; i < last; i++)
            {
                var panel = _rows[i].Panel;
                if (y < panel.TranslatePoint(new Point(), _rowsPanel).Y + panel.ActualHeight / 2)
                    return i;
            }
            return last;
        }

        private void ShowInsertionLine(int index)
        {
            if (_insertionAdorner == null)
            {
                var layer = AdornerLayer.GetAdornerLayer(_rowsPanel);
                if (layer == null)
                    return;
                _insertionAdorner = new InsertionAdorner(_rowsPanel);
                layer.Add(_insertionAdorner);
            }
            // In the middle of the gap above the row (rows have a bottom margin of 4); kept fully visible above the first.
            var top = _rows[index].Panel.TranslatePoint(new Point(), _rowsPanel).Y;
            _insertionAdorner.Y = Math.Max(1, top - 2);
        }

        private void HideInsertionLine()
        {
            if (_insertionAdorner == null)
                return;
            AdornerLayer.GetAdornerLayer(_rowsPanel)?.Remove(_insertionAdorner);
            _insertionAdorner = null;
        }

        /// <summary>Moves the row so that it ends up before the row that is at <paramref name="insertIndex"/> now.</summary>
        private void MoveRow(Row row, int insertIndex)
        {
            var from = _rows.IndexOf(row);
            if (from < 0)
                return;
            var to = insertIndex > from ? insertIndex - 1 : insertIndex;
            if (to == from)
                return;
            _rows.RemoveAt(from);
            _rowsPanel.Children.RemoveAt(from);
            _rows.Insert(to, row);
            _rowsPanel.Children.Insert(to, row.Panel);
            MarkChanged();
            UpdatePreview();
            // Detaching the row's text box took its focus away, if it had it.
            FocusRow(to);
        }

        #endregion

        #region Row editing

        /// <summary>Removes the row and focuses the row at <paramref name="focusIndex"/> (clamped), or nothing if it is -1.</summary>
        private void RemoveRow(Row row, int focusIndex)
        {
            var index = _rows.IndexOf(row);
            if (index < 0)
                return;
            _rows.RemoveAt(index);
            _rowsPanel.Children.RemoveAt(index);
            MarkChanged();
            EnsureTrailingEmptyRow();
            UpdatePreview();
            if (focusIndex >= 0)
                FocusRow(Math.Min(focusIndex, _rows.Count - 1));
        }

        // Mirror of auto-adding: a blank line disappears once it loses focus (except the trailing one). Not while it has
        // focus, so clearing a line to retype it keeps the caret there.
        private void RemoveIfBlank(Row row)
        {
            if (!_loading && IsBlank(row) && _rows.IndexOf(row) < _rows.Count - 1)
                RemoveRow(row, focusIndex: -1);
        }

        private void OnRowChanged()
        {
            if (_loading)
                return;
            MarkChanged();
            EnsureTrailingEmptyRow();
            UpdatePreview();
        }

        /// <summary>Keeps exactly one blank row at the end, so a new line can always be typed.</summary>
        private void EnsureTrailingEmptyRow()
        {
            // The "<different options>" placeholder is shown alone, as a single line.
            if (_rows.Count == 0 || !IsBlank(_rows[_rows.Count - 1]) && !_rows[_rows.Count - 1].DifferentOptions)
                AddRow(_rows.Count, "", true);
            UpdateRowStates();
        }

        private void UpdateRowStates()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var placeholder = i == _rows.Count - 1 && IsBlank(_rows[i]) || _rows[i].DifferentOptions;
                var visibility = placeholder ? Visibility.Hidden : Visibility.Visible;
                _rows[i].Handle.Visibility = visibility;
                _rows[i].Check.Visibility = visibility;
                _rows[i].Delete.Visibility = visibility;
                _rows[i].Text.Opacity = placeholder || _rows[i].Check.IsChecked == true ? 1.0 : 0.5;
            }
        }

        private void FocusRow(int index)
        {
            if (index < 0 || index >= _rows.Count)
                return;
            var row = _rows[index];
            var textBox = row.Text;
#pragma warning disable VSTHRD001, VSTHRD110 // Deferring focus until the row is laid out.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                if (!row.DifferentOptions)
                    textBox.CaretIndex = textBox.Text.Length;
            }));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        private void OnRowKeyDown(Row row, KeyEventArgs e)
        {
            var index = _rows.IndexOf(row);
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                // Enter moves to the next line.
                EnsureTrailingEmptyRow();
                FocusRow(Math.Min(index + 1, _rows.Count - 1));
                e.Handled = true;
            }
            else if (e.Key == Key.Back && row.Text.Text.Length == 0 && index > 0)
            {
                // Backspace on an empty line (e.g. the trailing blank one) moves to the end of the previous line.
                if (index < _rows.Count - 1)
                    RemoveRow(row, focusIndex: index - 1);
                else
                    FocusRow(index - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && index > 0)
            {
                FocusRow(index - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && index < _rows.Count - 1)
            {
                FocusRow(index + 1);
                e.Handled = true;
            }
        }

        private void UpdatePreview()
        {
            UpdateRowStates();
            var commandLine = HasDifferentOptionsRow() ? null : ArgumentStore.BuildCommandLine(EditorLines());
            _preview.Text = commandLine ?? DifferentOptionsText;
            // Once the placeholder is edited, the edited lines replace those of every configuration.
            _configsPanel.Visibility = commandLine == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateEvaluatedPreview(commandLine);
            UpdateRestore();
        }

        /// <summary>
        /// Shows <paramref name="commandLine"/> evaluated for the configurations in the current scope, if that changes it
        /// for any of them.
        /// </summary>
        private void UpdateEvaluatedPreview(string commandLine)
        {
            string shown = null;
            var differs = false;
            if (commandLine != null)
            {
                foreach (var cfg in _scope != null ? new List<string> { _scope } : _configNames)
                {
                    var evaluated = _evaluate(cfg, commandLine);
                    if (evaluated == null)
                        continue;
                    if (evaluated != commandLine)
                        differs = true;
                    // Under "All Configurations", e.g. $(Configuration) evaluates differently per configuration.
                    if (shown != null && shown != evaluated)
                        shown = DifferentOptionsText;
                    else if (shown == null)
                        shown = evaluated;
                }
            }
            _evaluatedPreview.Text = differs ? shown : "";
            _evaluatedPanel.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Theming

        private void OnThemeChanged(ThemeChangedEventArgs e) => ApplyThemeToWindow();

        private void ApplyThemeToWindow()
        {
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var color = Color.FromArgb(bg.A, bg.R, bg.G, bg.B);

            // Lets CrispImage invert icons for dark backgrounds.
            ImageThemingUtilities.SetImageBackgroundColor(this, color);

            var luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var dark = luminance < 0.5 ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            }
        }

        private const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        #endregion
    }
}
