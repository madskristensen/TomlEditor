using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Tomlyn.Syntax;

namespace TomlEditor
{
    /// <summary>
    /// WPF UserControl for displaying the document outline in the Document Outline tool window.
    /// Shows a hierarchical tree of TOML tables and key-value pairs for navigation.
    /// </summary>
    public partial class DocumentOutlineControl : UserControl
    {
        private Document _document;
        private IWpfTextView _textView;
        private IVsTextView _vsTextView;
        private bool _isNavigating;

        public ObservableCollection<OutlineItem> OutlineItems { get; } = new ObservableCollection<OutlineItem>();

        public DocumentOutlineControl()
        {
            InitializeComponent();
            OutlineTreeView.ItemsSource = OutlineItems;
        }

        /// <summary>
        /// Initializes the control with the document and text view for the TOML file.
        /// </summary>
        public void Initialize(Document document, IWpfTextView textView, IVsTextView vsTextView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Unsubscribe from previous document if any
            if (_document != null)
            {
                _document.Parsed -= OnDocumentParsed;
            }

            _document = document;
            _textView = textView;
            _vsTextView = vsTextView;

            if (_document != null)
            {
                _document.Parsed += OnDocumentParsed;

                // Subscribe to caret position changes for sync
                if (_textView != null)
                {
                    _textView.Caret.PositionChanged += OnCaretPositionChanged;
                }

                // Initial population
                RefreshOutline();
            }
        }

        /// <summary>
        /// Cleans up event subscriptions when the control is disposed.
        /// </summary>
        public void Cleanup()
        {
            if (_document != null)
            {
                _document.Parsed -= OnDocumentParsed;
            }

            if (_textView != null)
            {
                _textView.Caret.PositionChanged -= OnCaretPositionChanged;
            }

            _document = null;
            _textView = null;
            _vsTextView = null;
            OutlineItems.Clear();
        }

        private void OnDocumentParsed(Document document)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshOutline();
            }).FireAndForget();
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (_isNavigating || _document?.Model == null)
            {
                return;
            }

            // Find and select the item that contains the current caret position
            int caretLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            SelectItemAtLine(caretLine);
        }

        private void SelectItemAtLine(int lineNumber)
        {
            // Find the closest item at or before the caret line
            OutlineItem bestMatch = FindItemAtLine(OutlineItems, lineNumber);

            if (bestMatch != null && OutlineTreeView.SelectedItem != bestMatch)
            {
                _isNavigating = true;
                try
                {
                    SelectTreeViewItem(OutlineTreeView, bestMatch);
                }
                finally
                {
                    _isNavigating = false;
                }
            }
        }

        private OutlineItem FindItemAtLine(IEnumerable<OutlineItem> items, int lineNumber)
        {
            OutlineItem result = null;

            foreach (OutlineItem item in items)
            {
                if (item.LineNumber <= lineNumber)
                {
                    result = item;

                    // Check children for a more specific match
                    OutlineItem childMatch = FindItemAtLine(item.Children, lineNumber);
                    if (childMatch != null)
                    {
                        result = childMatch;
                    }
                }
            }

            return result;
        }

        private void RefreshOutline()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OutlineItems.Clear();

            if (_document?.Model == null)
            {
                EmptyMessage.Visibility = Visibility.Visible;
                return;
            }

            // Check if there's any content to show
            bool hasContent = _document.Model.KeyValues.Any() || _document.Model.Tables.Any();

            if (!hasContent)
            {
                EmptyMessage.Visibility = Visibility.Visible;
                return;
            }

            EmptyMessage.Visibility = Visibility.Collapsed;

            // Add top-level key-values first (before any table)
            foreach (KeyValueSyntax kvp in _document.Model.KeyValues)
            {
                OutlineItems.Add(CreateKeyValueItem(kvp));
            }

            // Build hierarchical table structure
            BuildTableTree();

            // Expand all items
            ExpandAllTreeViewItems();
        }

        private void BuildTableTree()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Dictionary to track tables by their full path for hierarchy building
            var tableNodes = new Dictionary<string, OutlineItem>();

            foreach (TableSyntaxBase table in _document.Model.Tables)
            {
                string fullName = table.Name?.ToString() ?? string.Empty;
                string[] parts = fullName.Split('.');

                OutlineItem tableItem = CreateTableItem(table, parts[parts.Length - 1]);

                if (parts.Length == 1)
                {
                    // Top-level table
                    OutlineItems.Add(tableItem);
                    tableNodes[fullName] = tableItem;
                }
                else
                {
                    // Nested table - find or create parent chain
                    string parentPath = string.Join(".", parts.Take(parts.Length - 1));

                    if (tableNodes.TryGetValue(parentPath, out OutlineItem parent))
                    {
                        parent.Children.Add(tableItem);
                    }
                    else
                    {
                        // Parent doesn't exist yet, create placeholder parents
                        OutlineItem currentParent = null;
                        string currentPath = "";

                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            currentPath = i == 0 ? parts[i] : currentPath + "." + parts[i];

                            if (!tableNodes.TryGetValue(currentPath, out OutlineItem existingNode))
                            {
                                // Create a placeholder parent node
                                existingNode = new OutlineItem
                                {
                                    Text = parts[i],
                                    ItemType = OutlineItemType.Table,
                                    LineNumber = 0,
                                    FontWeight = FontWeights.Normal
                                };

                                if (currentParent == null)
                                {
                                    OutlineItems.Add(existingNode);
                                }
                                else
                                {
                                    currentParent.Children.Add(existingNode);
                                }

                                tableNodes[currentPath] = existingNode;
                            }

                            currentParent = existingNode;
                        }

                        currentParent?.Children.Add(tableItem);
                    }

                    tableNodes[fullName] = tableItem;
                }

                // Add key-values for this table
                if (table is TableSyntax tableWithItems)
                {
                    foreach (KeyValueSyntax kvp in tableWithItems.Items.OfType<KeyValueSyntax>())
                    {
                        tableItem.Children.Add(CreateKeyValueItem(kvp));
                    }
                }
            }
        }

        private OutlineItem CreateTableItem(TableSyntaxBase table, string displayName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int lineNumber = 0;
            if (_vsTextView != null && table.Span.Start.Offset >= 0)
            {
                _vsTextView.GetLineAndColumn(table.Span.Start.Offset, out lineNumber, out int _);
            }

            bool isArrayOfTables = table is TableArraySyntax;

            return new OutlineItem
            {
                Text = displayName,
                ItemType = isArrayOfTables ? OutlineItemType.ArrayOfTables : OutlineItemType.Table,
                LineNumber = lineNumber,
                Span = table.Span,
                FontWeight = FontWeights.Bold
            };
        }

        private OutlineItem CreateKeyValueItem(KeyValueSyntax kvp)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int lineNumber = 0;
            if (_vsTextView != null && kvp.Span.Start.Offset >= 0)
            {
                _vsTextView.GetLineAndColumn(kvp.Span.Start.Offset, out lineNumber, out int _);
            }

            string keyName = kvp.Key?.ToString() ?? "unknown";

            return new OutlineItem
            {
                Text = keyName,
                ItemType = OutlineItemType.KeyValue,
                LineNumber = lineNumber,
                Span = kvp.Span,
                FontWeight = FontWeights.Normal
            };
        }

        private void NavigateToItem(OutlineItem item)
        {
            if (item == null || _vsTextView == null || item.LineNumber < 0)
            {
                return;
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            _isNavigating = true;
            try
            {
                // Navigate to the item's line
                _vsTextView.SetCaretPos(item.LineNumber, 0);
                _vsTextView.CenterLines(item.LineNumber, 1);

                // Ensure the editor has focus
                _vsTextView.SendExplicitFocus();
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private void OutlineTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (OutlineTreeView.SelectedItem is OutlineItem item)
            {
                NavigateToItem(item);
            }
        }

        private void OutlineTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter && OutlineTreeView.SelectedItem is OutlineItem item)
            {
                NavigateToItem(item);
                e.Handled = true;
            }
        }

        private void OutlineTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Single-click navigation can be enabled here if desired
        }

        private void ExpandAllTreeViewItems()
        {
            foreach (OutlineItem item in OutlineItems)
            {
                ExpandTreeViewItem(OutlineTreeView, item);
            }
        }

        private void ExpandTreeViewItem(ItemsControl container, OutlineItem item)
        {
            if (container.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsExpanded = true;

                foreach (OutlineItem child in item.Children)
                {
                    ExpandTreeViewItem(treeViewItem, child);
                }
            }
        }

        private void SelectTreeViewItem(ItemsControl container, OutlineItem item)
        {
            // First, try to find the item directly
            if (container.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.BringIntoView();
                return;
            }

            // Search through all items recursively
            foreach (object containerItem in container.Items)
            {
                if (container.ItemContainerGenerator.ContainerFromItem(containerItem) is TreeViewItem childContainer)
                {
                    if (containerItem == item)
                    {
                        childContainer.IsSelected = true;
                        childContainer.BringIntoView();
                        return;
                    }

                    SelectTreeViewItem(childContainer, item);
                }
            }
        }
    }

    /// <summary>
    /// Represents an item in the document outline tree.
    /// </summary>
    public class OutlineItem
    {
        public string Text { get; set; }
        public OutlineItemType ItemType { get; set; }
        public int LineNumber { get; set; }
        public SourceSpan Span { get; set; }
        public FontWeight FontWeight { get; set; }
        public ObservableCollection<OutlineItem> Children { get; } = new ObservableCollection<OutlineItem>();

        /// <summary>
        /// Gets the appropriate icon for this item type.
        /// </summary>
        public ImageMoniker ImageMoniker
        {
            get
            {
                switch (ItemType)
                {
                    case OutlineItemType.Table:
                        return KnownMonikers.Class;
                    case OutlineItemType.ArrayOfTables:
                        return KnownMonikers.ClassPublic;
                    case OutlineItemType.KeyValue:
                        return KnownMonikers.Field;
                    default:
                        return KnownMonikers.Document;
                }
            }
        }
    }

    /// <summary>
    /// Types of items that can appear in the document outline.
    /// </summary>
    public enum OutlineItemType
    {
        Table,
        ArrayOfTables,
        KeyValue
    }
}
