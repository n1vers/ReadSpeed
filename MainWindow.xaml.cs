using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using StorageBench.Core.Interfaces;
using StorageBench.Core.Models;
using StorageBench.Core.Services;
using StorageBench.Controls;
using StorageBench.Engines;

namespace StorageBench;

public partial class MainWindow : Window
{
    private readonly HistoryService _history = new();
    private readonly ObservableCollection<BenchmarkResult> _historyItems = new();
    private readonly List<SpeedSample> _liveSamples = new();
    private readonly SpeedValueConverter _historySpeedValueConverter = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private PauseController? _pauseController;
    private SpeedUnit _speedUnit = SpeedUnit.MegabytesPerSecond;
    private SpeedUnit _historySpeedUnit = SpeedUnit.MegabytesPerSecond;
    private bool _isInitialized;
    private bool _startupArgumentsHandled;
    private readonly List<ParallelTestDisplay> _parallelDisplays = new();

    public MainWindow()
    {
        InitializeComponent();

        HistoryGrid.ItemsSource = _historyItems;
        AverageSpeedColumn.Binding = CreateHistorySpeedBinding(nameof(BenchmarkResult.AverageSpeedMBps));
        MinimumSpeedColumn.Binding = CreateHistorySpeedBinding(nameof(BenchmarkResult.MinimumSpeedMBps));
        MaximumSpeedColumn.Binding = CreateHistorySpeedBinding(nameof(BenchmarkResult.MaximumSpeedMBps));

        _isInitialized = true;

        UpdateMainSpeedUnitDisplay();
        UpdateHistorySpeedUnitDisplay();
        UpdateHistoryFooter();
    }

    private Binding CreateHistorySpeedBinding(string propertyName) => new(propertyName)
    {
        Converter = _historySpeedValueConverter
    };

    // ------------------------------------------------------------------
    // История
    // ------------------------------------------------------------------

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        List<BenchmarkResult> items = await Task.Run(() => _history.Load());

        foreach (BenchmarkResult item in items.OrderByDescending(x => x.StartedAt))
            _historyItems.Add(item);

        UpdateHistoryFooter();

        if (_startupArgumentsHandled)
            return;

        _startupArgumentsHandled = true;

        string[] startupArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        int parallelism = 2;
        var startupSources = new List<string>();

        for (int index = 0; index < startupArguments.Length; index++)
        {
            string argument = startupArguments[index];

            if (string.Equals(argument, "--loop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "-l", StringComparison.OrdinalIgnoreCase))
            {
                LoopCheckBox.IsChecked = true;
            }
            else if (string.Equals(argument, "--parallel", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(argument, "-p", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < startupArguments.Length &&
                    int.TryParse(startupArguments[++index], out int value))
                {
                    parallelism = value;
                }
            }
            else
            {
                startupSources.AddRange(ParseSources(argument));
            }
        }

        ParallelTextBox.Text = parallelism.ToString(CultureInfo.InvariantCulture);

        if (startupSources.Count > 0)
        {
            SourceTextBox.Text = string.Join(Environment.NewLine, startupSources);
            await StartSourcesAsync(startupSources);
        }
    }

    private async Task SaveHistoryAsync()
    {
        List<BenchmarkResult> snapshot = _historyItems.ToList();

        try
        {
            await Task.Run(() => _history.Save(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Не удалось сохранить историю:\n" + ex.Message,
                "StorageBench",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task AddToHistoryAsync(BenchmarkResult result)
    {
        _historyItems.Insert(0, result);

        while (_historyItems.Count > HistoryService.MaxEntries)
            _historyItems.RemoveAt(_historyItems.Count - 1);

        UpdateHistoryFooter();

        await SaveHistoryAsync();
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is BenchmarkResult result)
        {
            HistoryGraph.SetData(result.Samples, result.AverageSpeedMBps);
            HistoryDetailsText.Text = BuildDetails(result, _historySpeedUnit);
            DeleteSelectedButton.IsEnabled = true;
            RerunSelectedButton.IsEnabled = _cancellationTokenSource is null;
        }
        else
        {
            HistoryGraph.Clear();
            HistoryDetailsText.Text = string.Empty;
            DeleteSelectedButton.IsEnabled = false;
            RerunSelectedButton.IsEnabled = false;
        }
    }

    private async void RerunSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not BenchmarkResult result)
            return;

        SourceType sourceType = SourceDetector.Detect(result.Source);

        if (sourceType is SourceType.Local or SourceType.Network)
        {
            bool isAvailable = await Task.Run(() => File.Exists(result.Source));

            if (!isAvailable)
            {
                MessageBox.Show(
                    "Источник сейчас недоступен:\n\n" + result.Source,
                    "StorageBench",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        SourceTextBox.Text = result.Source;
        await StartSourceAsync(result.Source);
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not BenchmarkResult result)
            return;

        _historyItems.Remove(result);
        UpdateHistoryFooter();

        await SaveHistoryAsync();
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyItems.Count == 0)
            return;

        MessageBoxResult answer = MessageBox.Show(
            $"Удалить всю историю ({_historyItems.Count} записей)?",
            "StorageBench",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        _historyItems.Clear();
        UpdateHistoryFooter();

        await SaveHistoryAsync();
    }

    private void UpdateHistoryFooter()
    {
        HistoryCountText.Text = $"Записей: {_historyItems.Count}";
        ClearHistoryButton.IsEnabled = _historyItems.Count > 0;
    }

    private string BuildDetails(BenchmarkResult r, SpeedUnit unit)
    {
        string finish = r.ReachedEnd
            ? "источник прочитан целиком"
            : "тест остановлен пользователем";

        return
            $"Источник:\n{r.Source}\n\n" +
            $"Тип теста: {r.TestType}\n" +
            $"Начало: {r.StartedAt:dd.MM.yyyy HH:mm:ss}\n" +
            $"Длительность: {r.DurationText}\n" +
            $"Завершение: {r.CompletedAtText}\n" +
            $"Статус: {r.StatusText}\n" +
            $"Результат: {finish}\n" +
            (string.IsNullOrWhiteSpace(r.ErrorText)
                ? string.Empty
                : $"Ошибка: {r.ErrorText}\n") +
            "\n" +
            $"Средняя скорость: {FormatSpeed(r.AverageSpeedMBps, unit)}\n" +
            $"Минимальная скорость: {FormatSpeed(r.MinimumSpeedMBps, unit)}\n" +
            $"Максимальная скорость: {FormatSpeed(r.MaximumSpeedMBps, unit)}\n\n" +
            $"Прочитано: {r.TotalReadText}\n" +
            $"Размер файла: {r.FileSizeText}\n" +
            $"Размер буфера: {r.BufferSizeKB} KB";
    }

    private string FormatSpeed(double speedMBps, SpeedUnit unit) =>
        SpeedUnitFormatter.Format(speedMBps, unit);

    private string FormatSpeed(double speedMBps) =>
        FormatSpeed(speedMBps, _speedUnit);

    private void SpeedUnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
            return;

        if (SpeedUnitComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out SpeedUnit unit))
        {
            return;
        }

        _speedUnit = unit;
        UpdateMainSpeedUnitDisplay();
    }

    private void HistorySpeedUnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
            return;

        if (HistorySpeedUnitComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out SpeedUnit unit))
        {
            return;
        }

        _historySpeedUnit = unit;
        UpdateHistorySpeedUnitDisplay();
    }

    private void UpdateMainSpeedUnitDisplay()
    {
        LiveGraph.SetUnit(_speedUnit);

        foreach (ParallelTestDisplay display in _parallelDisplays)
            display.Graph.SetUnit(_speedUnit);
    }

    private void UpdateHistorySpeedUnitDisplay()
    {
        string label = SpeedUnitFormatter.GetLabel(_historySpeedUnit);
        AverageSpeedColumn.Header = $"Средняя, {label}";
        MinimumSpeedColumn.Header = $"Мин, {label}";
        MaximumSpeedColumn.Header = $"Макс, {label}";
        _historySpeedValueConverter.Unit = _historySpeedUnit;
        HistoryGrid.Items.Refresh();

        if (HistoryGrid.SelectedItem is BenchmarkResult result)
            HistoryDetailsText.Text = BuildDetails(result, _historySpeedUnit);

        HistoryGraph.SetUnit(_historySpeedUnit);
    }

    // ------------------------------------------------------------------
    // Drag & drop / ввод источника
    // ------------------------------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText) ||
            e.Data.GetDataPresent(DataFormats.Text))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        string? source = null;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];

            if (files is { Length: > 0 })
                source = files[0];
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            source = e.Data.GetData(DataFormats.UnicodeText) as string;
        }
        else if (e.Data.GetDataPresent(DataFormats.Text))
        {
            source = e.Data.GetData(DataFormats.Text) as string;
        }

        e.Handled = true;

        if (!string.IsNullOrWhiteSpace(source))
        {
            source = CleanSource(source);

            SourceTextBox.Text = source;
            await StartSourcesAsync(new[] { source });
        }
    }

    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери файл для теста",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        SourceTextBox.Text = dialog.FileName;
        await StartSourcesAsync(new[] { dialog.FileName });
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await StartSourcesAsync(ParseSources(SourceTextBox.Text));
    }

    private async void SourceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers == ModifierKeys.Control ||
             !SourceTextBox.Text.Contains('\n', StringComparison.Ordinal)))
        {
            e.Handled = true;
            await StartSourcesAsync(ParseSources(SourceTextBox.Text));
        }
    }

    // «Копировать как путь» в проводнике даёт путь в кавычках.
    private static string CleanSource(string? source) =>
        (source ?? string.Empty).Trim().Trim('"').Trim();

    private static IEnumerable<string> ParseSources(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanSource)
            .Where(source => !string.IsNullOrWhiteSpace(source));

    private int GetParallelism()
    {
        if (!int.TryParse(ParallelTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            value = 2;

        value = Math.Clamp(value, 1, 64);
        ParallelTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
        return value;
    }

    private ParallelTestDisplay CreateParallelTestDisplay(string source)
    {
        var display = new ParallelTestDisplay
        {
            Source = source,
            Status = new TextBlock
            {
                Text = "Подготовка теста...",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            },
            Progress = new ProgressBar
            {
                Height = 10,
                Minimum = 0,
                Maximum = 100,
                Margin = new Thickness(0, 0, 0, 8),
                IsIndeterminate = true
            },
            Graph = new SpeedGraph
            {
                Height = 190,
                EmptyText = "График появится во время теста"
            }
        };

        display.Graph.SetUnit(_speedUnit);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = source,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        content.Children.Add(display.Status);
        content.Children.Add(display.Progress);
        content.Children.Add(display.Graph);

        var border = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content
        };

        ParallelTestsPanel.Children.Add(border);
        return display;
    }

    // ------------------------------------------------------------------
    // Запуск теста
    // ------------------------------------------------------------------

    private async Task StartSourcesAsync(IEnumerable<string> sourceValues)
    {
        string[] sources = sourceValues
            .Select(CleanSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToArray();

        if (sources.Length == 0 || _cancellationTokenSource is not null)
            return;

        if (sources.Length == 1)
        {
            await StartSourceAsync(sources[0]);
            return;
        }

        if (sources.Any(source => SourceDetector.Detect(source) == SourceType.Unknown))
        {
            MessageBox.Show(
                "Один или несколько источников имеют неизвестный формат.\n\n" +
                "Используй пути к файлам или HTTP/HTTPS ссылки.",
                "StorageBench",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MainTabs.SelectedIndex = 0;
        _cancellationTokenSource = new CancellationTokenSource();
        _pauseController = new PauseController();
        SetRunningState(true);
        StatusText.Text = $"Запущено источников: {sources.Length}";
        ProgressText.Text = "Подготовка параллельных тестов...";
        LiveGraph.Visibility = Visibility.Collapsed;
        TestProgressBar.Visibility = Visibility.Collapsed;
        ParallelTestsPanel.Children.Clear();
        ParallelTestsScrollViewer.Visibility = Visibility.Visible;

        ParallelTestDisplay[] displays = sources
            .Select(CreateParallelTestDisplay)
            .ToArray();
        _parallelDisplays.AddRange(displays);

        using var semaphore = new SemaphoreSlim(GetParallelism());

        try
        {
            Task[] tasks = sources.Select((source, index) => RunParallelSourceAsync(
                source,
                displays[index],
                semaphore,
                _cancellationTokenSource.Token)).ToArray();

            await Task.WhenAll(tasks);
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _pauseController = null;
            ParallelTestsPanel.Children.Clear();
            _parallelDisplays.Clear();
            ParallelTestsScrollViewer.Visibility = Visibility.Collapsed;
            LiveGraph.Visibility = Visibility.Visible;
            TestProgressBar.Visibility = Visibility.Visible;
            SetRunningState(false);
        }
    }

    private async Task RunParallelSourceAsync(
        string source,
        ParallelTestDisplay display,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        SourceType sourceType = SourceDetector.Detect(source);
        bool semaphoreEntered = false;

        try
        {
            await semaphore.WaitAsync(cancellationToken);
            semaphoreEntered = true;

            IReadEngine engine = sourceType switch
            {
                SourceType.Local => new LocalReadEngine(),
                SourceType.Network => new NetworkReadEngine(),
                SourceType.Internet => new InternetReadEngine(),
                _ => throw new NotSupportedException("Этот тип источника пока не поддерживается.")
            };

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                display.Samples.Clear();
                display.Graph.Clear();
                display.Progress.Value = 0;
                display.Progress.IsIndeterminate = true;
                display.Status.Text = "Подготовка теста...";

                long? fileSize = sourceType == SourceType.Local
                    ? new FileInfo(source).Length
                    : null;

                var progress = new Progress<ReadProgress>(p =>
                {
                    if (p.IsCompleted)
                        return;

                    display.Progress.IsIndeterminate = false;
                    display.Samples.Add(new SpeedSample(
                        p.Elapsed.TotalSeconds,
                        p.CurrentSpeedMBps));
                    display.Graph.SetData(display.Samples, p.AverageSpeedMBps);
                    display.Status.Text =
                        $"Текущая скорость: {FormatSpeed(p.CurrentSpeedMBps)} | " +
                        $"Средняя: {FormatSpeed(p.AverageSpeedMBps)} | " +
                        $"Прочитано: {SizeFormatter.ToText(p.BytesRead)}";

                    if (fileSize is > 0)
                    {
                        display.Progress.Value = Math.Min(
                            100,
                            p.BytesRead * 100.0 / fileSize.Value);
                    }
                });

                BenchmarkResult result = await engine.RunAsync(
                    new BenchmarkSettings
                    {
                        SourcePath = source,
                        BufferSize = 1024,
                        DisableWindowsCache = true,
                        PauseController = _pauseController
                    },
                    progress,
                    cancellationToken);

                result.CompletedAt = DateTime.Now;
                result.Status = "OK";
                display.Progress.IsIndeterminate = false;
                display.Progress.Value = 100;
                display.Graph.SetData(result.Samples, result.AverageSpeedMBps);
                display.Status.Text =
                    $"Завершён. Средняя скорость: {FormatSpeed(result.AverageSpeedMBps)}";
                await AddToHistoryAsync(result);
            }
            while (LoopCheckBox.IsChecked == true && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            display.Status.Text = "Отменён";
        }
        catch (Exception ex)
        {
            display.Progress.IsIndeterminate = false;
            display.Status.Text = $"Ошибка: {ex.Message}";
            await AddToHistoryAsync(new BenchmarkResult
            {
                Source = source,
                SourceKind = sourceType.ToString(),
                TestType = "Ошибка тестирования",
                StartedAt = DateTime.Now,
                CompletedAt = DateTime.Now,
                Status = "ERROR",
                ErrorText = ex.Message
            });
        }
        finally
        {
            if (semaphoreEntered)
                semaphore.Release();
        }
    }

    private async Task StartSourceAsync(string source)
    {
        if (_cancellationTokenSource != null)
            return;

        SourceType sourceType = SourceDetector.Detect(source);

        if (sourceType == SourceType.Unknown)
        {
            MessageBox.Show(
                "Не удалось определить источник.\n\n" +
                "Перетащи существующий файл или укажи HTTP/HTTPS ссылку.",
                "StorageBench",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        MainTabs.SelectedIndex = 0;

        _cancellationTokenSource = new CancellationTokenSource();
        _pauseController = new PauseController();

        SetRunningState(true);

        _liveSamples.Clear();
        LiveGraph.SetData(null, null);

        StatusText.Text = $"Источник: {sourceType}\n{source}";
        ProgressText.Text = "Подготовка теста...";
        TestProgressBar.Value = 0;
        TestProgressBar.IsIndeterminate = true;

        try
        {
            do
            {
                _liveSamples.Clear();
                LiveGraph.SetData(null, null);
                ProgressText.Text = "Подготовка теста...";
                TestProgressBar.Value = 0;
                TestProgressBar.IsIndeterminate = true;

                var settings = new BenchmarkSettings
                {
                    SourcePath = source,
                    BufferSize = 1024,
                    DisableWindowsCache = true,
                    PauseController = _pauseController
                };

                IReadEngine engine = sourceType switch
                {
                    SourceType.Local => new LocalReadEngine(),
                    SourceType.Network => new NetworkReadEngine(),
                    SourceType.Internet => new InternetReadEngine(),
                    _ => throw new NotSupportedException(
                        "Этот тип источника пока не поддерживается.")
                };

                long? fileSize = null;

                if (sourceType == SourceType.Local)
                {
                    fileSize = new FileInfo(source).Length;
                }

                var progress = new Progress<ReadProgress>(p =>
                {
                    if (p.IsCompleted)
                        return;

                    TestProgressBar.IsIndeterminate = false;

                    _liveSamples.Add(new SpeedSample(
                        p.Elapsed.TotalSeconds,
                        p.CurrentSpeedMBps));

                    LiveGraph.SetData(_liveSamples, p.AverageSpeedMBps);

                    double readGB = p.BytesRead / 1024.0 / 1024.0 / 1024.0;

                    ProgressText.Text =
                        $"Текущая скорость: {FormatSpeed(p.CurrentSpeedMBps)}\n" +
                        $"Средняя скорость: {FormatSpeed(p.AverageSpeedMBps)}\n" +
                        $"Прочитано: {readGB:F2} GB\n" +
                        $"Время: {p.Elapsed:mm\\:ss\\.f}";

                    double dataPercent = fileSize.HasValue && fileSize.Value > 0
                        ? p.BytesRead * 100.0 / fileSize.Value
                        : 0;

                    TestProgressBar.Value = Math.Min(100, dataPercent);
                });

                BenchmarkResult result = await engine.RunAsync(
                    settings,
                    progress,
                    _cancellationTokenSource.Token);

                result.CompletedAt = DateTime.Now;
                result.Status = "OK";

                StatusText.Text = result.ReachedEnd
                    ? "Тест завершён: источник прочитан целиком"
                    : "Тест остановлен пользователем";

                ProgressText.Text =
                    $"Средняя скорость: {FormatSpeed(result.AverageSpeedMBps)}\n" +
                    $"Минимальная скорость: {FormatSpeed(result.MinimumSpeedMBps)}\n" +
                    $"Максимальная скорость: {FormatSpeed(result.MaximumSpeedMBps)}\n" +
                    $"Прочитано: {result.TotalReadText}\n" +
                    $"Время: {result.DurationText}";

                TestProgressBar.Value = 100;

                LiveGraph.SetData(result.Samples, result.AverageSpeedMBps);

                await AddToHistoryAsync(result);
            }
            while (LoopCheckBox.IsChecked == true &&
                   _cancellationTokenSource is { IsCancellationRequested: false });
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Тест отменён";
            ProgressText.Text = "Выполнение теста остановлено. В историю не записывается.";
        }
        catch (Exception ex)
        {
            await AddToHistoryAsync(new BenchmarkResult
            {
                Source = source,
                SourceKind = sourceType.ToString(),
                TestType = "Ошибка тестирования",
                StartedAt = DateTime.Now,
                CompletedAt = DateTime.Now,
                Status = "ERROR",
                ErrorText = ex.Message
            });

            StatusText.Text = "Ошибка тестирования";

            MessageBox.Show(
                ex.Message,
                "StorageBench — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _pauseController = null;

            SetRunningState(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        StatusText.Text = "Останавливаем тест...";
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pauseController is null)
            return;

        if (_pauseController.IsPaused)
        {
            _pauseController.Resume();
            PauseButton.Content = "Пауза";
            StatusText.Text = "Тест продолжается";
        }
        else
        {
            _pauseController.Pause();
            PauseButton.Content = "Продолжить";
            StatusText.Text = "Тест приостановлен";
        }
    }

    private void SetRunningState(bool isRunning)
    {
        PauseButton.IsEnabled = isRunning;
        CancelButton.IsEnabled = isRunning;
        StartButton.IsEnabled = !isRunning;
        SpeedUnitComboBox.IsEnabled = !isRunning;
        SourceTextBox.IsEnabled = !isRunning;
        ParallelTextBox.IsEnabled = !isRunning;
        RerunSelectedButton.IsEnabled = !isRunning &&
            HistoryGrid.SelectedItem is BenchmarkResult;

        if (!isRunning)
            PauseButton.Content = "Пауза";

        if (!isRunning)
            TestProgressBar.IsIndeterminate = false;
    }

    private sealed class ParallelTestDisplay
    {
        public string Source { get; init; } = string.Empty;
        public TextBlock Status { get; init; } = null!;
        public ProgressBar Progress { get; init; } = null!;
        public SpeedGraph Graph { get; init; } = null!;
        public List<SpeedSample> Samples { get; } = new();
    }

    private sealed class SpeedValueConverter : IValueConverter
    {
        public SpeedUnit Unit { get; set; } = SpeedUnit.MegabytesPerSecond;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            SpeedUnitFormatter.Convert((double)value, Unit).ToString("F1", culture);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
