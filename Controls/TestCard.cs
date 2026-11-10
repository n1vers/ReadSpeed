using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StorageBench.Core.Models;
using StorageBench.Core.Services;

namespace StorageBench.Controls;

public enum TestCardState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Карточка одного источника при параллельном запуске: название, статус,
/// прогресс, текущая / средняя / мин / макс скорость, объём, время и свой график.
/// Все методы вызываются из UI-потока.
/// </summary>
public sealed class TestCard : Border
{
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)));

    private readonly TextBlock _badgeText;
    private readonly Border _badge;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly SpeedGraph _graph;
    private readonly TextBlock _current;
    private readonly TextBlock _average;
    private readonly TextBlock _minimum;
    private readonly TextBlock _maximum;
    private readonly TextBlock _read;
    private readonly TextBlock _size;
    private readonly TextBlock _time;
    private readonly TextBlock _runs;
    private readonly List<SpeedSample> _samples = new();

    private SpeedUnit _unit = SpeedUnit.MegabytesPerSecond;
    private bool _hasData;
    private bool _hasKnownSize;
    private int _run;
    private double _currentMBps;
    private double _averageMBps;
    private double _minimumMBps;
    private double _maximumMBps;
    private long _bytesRead;
    private long? _fileSize;
    private TimeSpan _elapsed;

    public event EventHandler? SelectionChanged;

    public TestCard(string source, SourceType kind)
    {
        Source = source;

        var named = new BenchmarkResult { Source = source, SourceKind = kind.ToString() };

        BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        MouseLeftButtonUp += Card_MouseLeftButtonUp;
        Cursor = System.Windows.Input.Cursors.Hand;
        Padding = new Thickness(10);
        Margin = new Thickness(0, 0, 10, 10);

        // ----- заголовок: имя + бейдж состояния -----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = named.SourceName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = source
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        _badgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };

        _badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Child = _badgeText
        };
        Grid.SetColumn(_badge, 1);
        header.Children.Add(_badge);

        var subtitle = new TextBlock
        {
            Text = $"{named.SourceKindText} · {source}",
            FontSize = 11,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 8),
            ToolTip = source
        };

        // ----- прогресс -----
        _progress = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ----- сетка показателей -----
        var stats = new Grid { Margin = new Thickness(0, 0, 0, 8) };

        for (int i = 0; i < 2; i++)
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 4; i++)
            stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddStat(stats, 0, 0, "Текущая", out _current);
        AddStat(stats, 0, 1, "Средняя", out _average);
        AddStat(stats, 1, 0, "Минимум", out _minimum);
        AddStat(stats, 1, 1, "Максимум", out _maximum);
        AddStat(stats, 2, 0, "Прочитано", out _read);
        AddStat(stats, 2, 1, "Размер", out _size);
        AddStat(stats, 3, 0, "Время", out _time);
        AddStat(stats, 3, 1, "Прогонов", out _runs);

        // ----- сообщение об ошибке / отмене -----
        _message = new TextBlock
        {
            Foreground = ErrorBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };

        // ----- график -----
        _graph = new SpeedGraph
        {
            Height = 150,
            EmptyText = "График появится во время теста"
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(subtitle);
        root.Children.Add(_progress);
        root.Children.Add(stats);
        root.Children.Add(_message);
        root.Children.Add(_graph);
        Child = root;

        SetState(TestCardState.Queued);
        RefreshStats();
    }

    public string Source { get; }

    public TestCardState State { get; private set; }

    public bool IsSelected { get; private set; }

    /// <summary>Сколько прогонов этого источника завершилось успешно.</summary>
    public int CompletedRuns { get; private set; }

    /// <summary>Текущая скорость для суммарного показателя; у неактивной карточки 0.</summary>
    public double CurrentSpeedMBps => State == TestCardState.Running ? _currentMBps : 0;

    public void SetUnit(SpeedUnit unit)
    {
        _unit = unit;
        _graph.SetUnit(unit);
        RefreshStats();
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        BorderBrush = new SolidColorBrush(selected
            ? Color.FromRgb(0x4F, 0x9D, 0xD9)
            : Color.FromRgb(0x55, 0x55, 0x55));
        BorderThickness = new Thickness(selected ? 2 : 1);
    }

    /// <summary>Источник ждёт свободный поток.</summary>
    public void SetQueued()
    {
        SetState(TestCardState.Queued);
    }

    private void Card_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SetSelected(!IsSelected);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>Начало очередного прогона: график и показатели обнуляются.</summary>
    public void BeginRun(int run)
    {
        _run = run;
        _samples.Clear();
        _graph.Clear();
        _hasData = false;
        _currentMBps = 0;
        _averageMBps = 0;
        _minimumMBps = 0;
        _maximumMBps = 0;
        _bytesRead = 0;
        _elapsed = TimeSpan.Zero;
        _message.Visibility = Visibility.Collapsed;

        SetState(TestCardState.Running);
        RefreshStats();
    }

    public void ReportProgress(ReadProgress progress, long? fileSize)
    {
        if (State != TestCardState.Running && State != TestCardState.Paused)
            return;

        _samples.Add(new SpeedSample(progress.Elapsed.TotalSeconds, progress.CurrentSpeedMBps));

        _currentMBps = progress.CurrentSpeedMBps;
        _averageMBps = progress.AverageSpeedMBps;
        _bytesRead = progress.BytesRead;
        _elapsed = progress.Elapsed;
        _fileSize = fileSize;

        if (!_hasData)
        {
            _minimumMBps = progress.CurrentSpeedMBps;
            _maximumMBps = progress.CurrentSpeedMBps;
            _hasData = true;
        }
        else
        {
            _minimumMBps = Math.Min(_minimumMBps, progress.CurrentSpeedMBps);
            _maximumMBps = Math.Max(_maximumMBps, progress.CurrentSpeedMBps);
        }

        _hasKnownSize = fileSize is > 0;

        if (_hasKnownSize)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Min(100, progress.BytesRead * 100.0 / fileSize.GetValueOrDefault(1));
        }
        else
        {
            _progress.IsIndeterminate = State == TestCardState.Running;
        }

        _graph.SetData(_samples, progress.AverageSpeedMBps);
        RefreshStats();
    }

    public void Complete(BenchmarkResult result)
    {
        _hasData = true;
        _currentMBps = result.AverageSpeedMBps;
        _averageMBps = result.AverageSpeedMBps;
        _minimumMBps = result.MinimumSpeedMBps;
        _maximumMBps = result.MaximumSpeedMBps;
        _bytesRead = result.TotalBytesRead;
        _fileSize = result.FileSizeBytes;
        _elapsed = result.Duration;
        CompletedRuns++;

        _graph.SetData(result.Samples, result.AverageSpeedMBps);

        SetState(TestCardState.Completed);
        RefreshStats();
    }

    public void Fail(string message)
    {
        _message.Text = "Ошибка: " + message;
        _message.Visibility = Visibility.Visible;

        SetState(TestCardState.Failed);
        RefreshStats();
    }

    public void Cancel()
    {
        SetState(TestCardState.Cancelled);
        RefreshStats();
    }

    public void SetPaused(bool paused)
    {
        if (paused && State == TestCardState.Running)
            SetState(TestCardState.Paused);
        else if (!paused && State == TestCardState.Paused)
            SetState(TestCardState.Running);
    }

    // ------------------------------------------------------------------

    private void SetState(TestCardState state)
    {
        State = state;

        (string text, Color color) = state switch
        {
            TestCardState.Queued => ("В очереди", Color.FromRgb(0x6B, 0x6B, 0x6B)),
            TestCardState.Running => (_run > 1 ? $"Идёт · прогон {_run}" : "Идёт", Color.FromRgb(0x2F, 0x7D, 0xD1)),
            TestCardState.Paused => ("Пауза", Color.FromRgb(0xB8, 0x86, 0x0B)),
            TestCardState.Completed => ("Завершён", Color.FromRgb(0x1F, 0x9D, 0x3A)),
            TestCardState.Failed => ("Ошибка", Color.FromRgb(0xC0, 0x39, 0x2B)),
            _ => ("Отменён", Color.FromRgb(0x6B, 0x6B, 0x6B))
        };

        _badgeText.Text = text;
        _badge.Background = new SolidColorBrush(color);

        switch (state)
        {
            case TestCardState.Queued:
                _progress.IsIndeterminate = false;
                if (CompletedRuns == 0)
                    _progress.Value = 0;
                break;

            case TestCardState.Running:
                _progress.IsIndeterminate = !_hasKnownSize || !_hasData;
                _progress.Value = _hasData && _hasKnownSize ? _progress.Value : 0;
                break;

            case TestCardState.Paused:
                _progress.IsIndeterminate = false;
                break;

            case TestCardState.Completed:
                _progress.IsIndeterminate = false;
                _progress.Value = 100;
                break;

            default:
                _progress.IsIndeterminate = false;
                break;
        }
    }

    private void RefreshStats()
    {
        const string Dash = "—";

        if (!_hasData)
        {
            _current.Text = Dash;
            _average.Text = Dash;
            _minimum.Text = Dash;
            _maximum.Text = Dash;
            _read.Text = Dash;
            _size.Text = Dash;
            _time.Text = Dash;
        }
        else
        {
            _current.Text = SpeedUnitFormatter.Format(_currentMBps, _unit);
            _average.Text = SpeedUnitFormatter.Format(_averageMBps, _unit);
            _minimum.Text = SpeedUnitFormatter.Format(_minimumMBps, _unit);
            _maximum.Text = SpeedUnitFormatter.Format(_maximumMBps, _unit);

            string read = SizeFormatter.ToText(_bytesRead);

            if (_fileSize is > 0)
            {
                double percent = Math.Min(100, _bytesRead * 100.0 / _fileSize.Value);
                read += $" ({percent:0}%)";
            }

            _read.Text = read;
            _size.Text = _fileSize.HasValue ? SizeFormatter.ToText(_fileSize.Value) : "неизвестен";
            _time.Text = _elapsed.ToString(@"mm\:ss\.f");
        }

        _runs.Text = CompletedRuns.ToString();
    }

    private static void AddStat(Grid grid, int row, int column, string caption, out TextBlock value)
    {
        value = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "—"
        };

        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };

        panel.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 10,
            Foreground = MutedBrush
        });

        panel.Children.Add(value);

        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
