using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Assister.Windows.App.Services;

namespace Assister.Windows.App.Controls;

internal sealed class ExecutionTimelineView : Expander
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ExecutionTimeline _timeline;
    private readonly List<(ExecutionStep Step, TextBlock Time, Canvas Track, Rectangle Bar)> _rows = [];

    public ExecutionTimelineView(ExecutionTimeline timeline, bool terminal, bool expanded, Action<bool> changed)
    {
        _timeline = timeline;
        Header = $"Execution · {timeline.Steps.Count} steps";
        FontSize = 18;
        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175));
        Margin = new Thickness(15, 0, 15, 24);
        IsExpanded = !terminal || expanded;
        Expanded += (_, _) => changed(true);
        Collapsed += (_, _) => changed(false);
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var step in timeline.Steps)
        {
            var color = new SolidColorBrush(step.Status is "failed" or "rejected" or "unavailable" or "interrupted" ? Color.FromRgb(240, 165, 165) :
                step.Kind == "ToolCall" ? Color.FromRgb(228, 169, 92) : Color.FromRgb(116, 214, 178));
            var row = new StackPanel { Margin = new Thickness(step.ParentId.Length > 0 ? 16 : 0, 0, 0, 14) };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = step.Label, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 0) });
            var time = new TextBlock { FontFamily = new FontFamily("Consolas"), Foreground = color };
            Grid.SetColumn(time, 1);
            header.Children.Add(time);
            row.Children.Add(header);
            var track = new Canvas { Height = 5, Background = new SolidColorBrush(Color.FromRgb(32, 43, 57)), ClipToBounds = true, Margin = new Thickness(0, 7, 0, 5) };
            var bar = new Rectangle { Height = 5, Fill = color };
            track.Children.Add(bar);
            row.Children.Add(track);
            row.Children.Add(new TextBlock { Text = step.Status, FontSize = 15, Foreground = color });
            panel.Children.Add(row);
            _rows.Add((step, time, track, bar));
        }
        Content = panel;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); if (!terminal) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var now = _timeline.Now;
        var origin = _timeline.Steps.Where(step => step.StartedAt.HasValue).Select(step => step.StartedAt!.Value).DefaultIfEmpty(now).Min();
        var span = Math.Max(1, (now - origin).TotalMilliseconds);
        foreach (var (step, time, track, bar) in _rows)
        {
            var elapsed = step.ElapsedMs(now);
            time.Text = elapsed is { } milliseconds ? (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s" : "—";
            var start = step.StartedAt is { } at ? Math.Clamp((at - origin).TotalMilliseconds / span, 0, 1) * track.ActualWidth : 0;
            Canvas.SetLeft(bar, start);
            bar.Width = elapsed is { } duration && step.StartedAt.HasValue ? Math.Min(Math.Max(2, duration / span * track.ActualWidth), Math.Max(0, track.ActualWidth - start)) : 0;
        }
    }
}
