using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GitDeployPro.Controls
{
    public enum WorkerState
    {
        Running,
        Paused,
        Queued,
        Done,
        Failed
    }

    /// <summary>Reusable live worker row (transfers, backups, scans, updates). Themed via palette tokens.</summary>
    public partial class WorkerCard : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(WorkerCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(WorkerCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty StatsTextProperty = DependencyProperty.Register(
            nameof(StatsText), typeof(string), typeof(WorkerCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
            nameof(IconKind), typeof(string), typeof(WorkerCard), new PropertyMetadata("zap"));

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(WorkerCard), new PropertyMetadata(0.0, OnVisualChanged));

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(WorkerState), typeof(WorkerCard), new PropertyMetadata(WorkerState.Running, OnVisualChanged));

        public static readonly DependencyProperty ShowControlsProperty = DependencyProperty.Register(
            nameof(ShowControls), typeof(bool), typeof(WorkerCard), new PropertyMetadata(true, OnVisualChanged));

        public event EventHandler? PauseRequested;
        public event EventHandler? CancelRequested;

        public WorkerCard()
        {
            InitializeComponent();
            Loaded += (_, _) => UpdateVisuals();
        }

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
        public string StatsText { get => (string)GetValue(StatsTextProperty); set => SetValue(StatsTextProperty, value); }
        public string IconKind { get => (string)GetValue(IconKindProperty); set => SetValue(IconKindProperty, value); }
        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
        public WorkerState State { get => (WorkerState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
        public bool ShowControls { get => (bool)GetValue(ShowControlsProperty); set => SetValue(ShowControlsProperty, value); }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => (d as WorkerCard)?.UpdateVisuals();

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            State = State == WorkerState.Paused ? WorkerState.Running : WorkerState.Paused;
            PauseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
            => CancelRequested?.Invoke(this, EventArgs.Empty);

        private void UpdateVisuals()
        {
            if (CardBorder == null)
            {
                return;
            }

            var (brushKey, softKey, text) = State switch
            {
                WorkerState.Running => ("Accent.Primary", "Accent.Soft", "Running"),
                WorkerState.Paused => ("Status.Warning", "Status.WarningSurface", "Paused"),
                WorkerState.Done => ("Status.Success", "Status.SuccessSurface", "Done"),
                WorkerState.Failed => ("Status.Error", "Status.ErrorSurface", "Failed"),
                _ => ("Text.Muted", "Surface.Input", "Queued")
            };

            StateBar.SetResourceReference(Border.BackgroundProperty, brushKey);
            StateBadge.SetResourceReference(Border.BackgroundProperty, softKey);
            StateText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            IconBox.SetResourceReference(Border.BackgroundProperty, softKey);
            IconPath.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey);
            StateText.Text = text;
            PercentText.Text = State == WorkerState.Queued ? "queued" : $"{Math.Floor(Math.Clamp(Progress, 0, 100))}%";
            PauseIcon.SetValue(Behaviors.AppIcon.KindProperty, State == WorkerState.Paused ? "play" : "pause");
            PauseButton.Visibility = ShowControls && (State == WorkerState.Running || State == WorkerState.Paused)
                ? Visibility.Visible
                : Visibility.Collapsed;
            CancelButton.Visibility = ShowControls && State != WorkerState.Done ? Visibility.Visible : Visibility.Collapsed;
            Bar.Tag = State.ToString();
            CardBorder.SetResourceReference(
                Border.BorderBrushProperty,
                State == WorkerState.Running ? "Accent.Glow" : "Border.Subtle");

            // gentle pulse on the state bar while running
            if (State == WorkerState.Running)
            {
                var pulse = new DoubleAnimation(1.0, 0.45, TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                StateBar.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                StateBar.BeginAnimation(OpacityProperty, null);
                StateBar.Opacity = 1;
            }
        }
    }
}
