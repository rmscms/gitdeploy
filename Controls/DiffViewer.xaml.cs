using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Highlighting;

namespace GitDeployPro.Controls
{
    public partial class DiffViewer : System.Windows.Controls.UserControl
    {
        public DiffViewer()
        {
            InitializeComponent();
            DiffEditor.IsReadOnly = true;
            DiffEditor.Options.EnableHyperlinks = false;
            DiffEditor.Options.EnableEmailHyperlinks = false;
            DiffEditor.Options.HighlightCurrentLine = false;
            DiffEditor.Options.ConvertTabsToSpaces = true;
            DiffEditor.TextArea.TextView.LineTransformers.Add(new DiffHighlightColorizer());
            UpdateEmptyState();
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(DiffViewer), new PropertyMetadata("Diff preview"));

        public static readonly DependencyProperty StatusProperty =
            DependencyProperty.Register(nameof(Status), typeof(string), typeof(DiffViewer), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DiffTextProperty =
            DependencyProperty.Register(nameof(DiffText), typeof(string), typeof(DiffViewer), new PropertyMetadata(string.Empty, OnDiffTextChanged));

        public static readonly DependencyProperty FilePathProperty =
            DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(DiffViewer), new PropertyMetadata(string.Empty, OnFilePathChanged));

        public static readonly DependencyProperty EmptyMessageProperty =
            DependencyProperty.Register(nameof(EmptyMessage), typeof(string), typeof(DiffViewer), new PropertyMetadata("Select a file to see the diff"));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Status
        {
            get => (string)GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        public string DiffText
        {
            get => (string)GetValue(DiffTextProperty);
            set => SetValue(DiffTextProperty, value);
        }

        public string FilePath
        {
            get => (string)GetValue(FilePathProperty);
            set => SetValue(FilePathProperty, value);
        }

        public string EmptyMessage
        {
            get => (string)GetValue(EmptyMessageProperty);
            set => SetValue(EmptyMessageProperty, value);
        }

        private static void OnDiffTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiffViewer viewer)
            {
                viewer.DiffEditor.Text = e.NewValue as string ?? string.Empty;
                viewer.UpdateHighlighting();
                viewer.UpdateEmptyState();
            }
        }

        private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiffViewer viewer)
            {
                viewer.UpdateHighlighting();
            }
        }

        private void UpdateEmptyState()
        {
            bool hasContent = !string.IsNullOrWhiteSpace(DiffEditor?.Text);
            if (EmptyState != null)
            {
                EmptyState.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
            }
            if (DiffEditor != null)
            {
                DiffEditor.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateHighlighting()
        {
            if (DiffEditor == null)
            {
                return;
            }

            IHighlightingDefinition? definition = null;
            var filePath = FilePath ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
                definition = HighlightingManager.Instance.GetDefinitionByExtension(ext);

                if (definition == null && HighlightingFallbacks.TryGetValue(ext, out var fallback))
                {
                    definition = HighlightingManager.Instance.GetDefinition(fallback);
                }
            }

            DiffEditor.SyntaxHighlighting = definition;
        }

        private static readonly Dictionary<string, string> HighlightingFallbacks = new(StringComparer.OrdinalIgnoreCase)
        {
            [".php"] = "HTML",
            [".blade.php"] = "HTML",
            [".cshtml"] = "HTML",
            [".razor"] = "HTML",
            [".scss"] = "CSS",
            [".sass"] = "CSS",
            [".ts"] = "JavaScript",
            [".jsx"] = "JavaScript",
            [".tsx"] = "JavaScript"
        };

        private sealed class DiffHighlightColorizer : DocumentColorizingTransformer
        {
            protected override void ColorizeLine(DocumentLine line)
            {
                if (CurrentContext?.Document == null) return;

                string text = CurrentContext.Document.GetText(line);
                if (string.IsNullOrEmpty(text)) return;

                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#101010");
                var foreground = System.Windows.Media.Brushes.White;

                if (text.StartsWith("+++ ") || text.StartsWith("--- ") || text.StartsWith("index"))
                {
                    color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#242424");
                    foreground = System.Windows.Media.Brushes.LightGray;
                }
                else if (text.StartsWith("@@"))
                {
                    color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1F2633");
                    foreground = System.Windows.Media.Brushes.LightSkyBlue;
                }
                else if (text.StartsWith("+"))
                {
                    color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F2A1A");
                    foreground = System.Windows.Media.Brushes.LightGreen;
                }
                else if (text.StartsWith("-"))
                {
                    color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2A1414");
                    foreground = System.Windows.Media.Brushes.IndianRed;
                }
                else if (text.StartsWith("diff --git"))
                {
                    color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E");
                    foreground = System.Windows.Media.Brushes.Orange;
                }

                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetBackgroundBrush(new System.Windows.Media.SolidColorBrush(color));
                    element.TextRunProperties.SetForegroundBrush(foreground);
                });
            }
        }
    }
}


