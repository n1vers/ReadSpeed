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
    private SemaphoreSlim? _parallelSemaphore;
    private TaskCompletionSource? _sessionCompletion;
    private int _sessionParallelism;
    private int _sessionTaskCount;
    private SpeedUnit _speedUnit = SpeedUnit.MegabytesPerSecond;
    private SpeedUnit _historySpeedUnit = SpeedUnit.MegabytesPerSecond;
    private bool _isInitialized;
    private bool _startupArgumentsHandled;
    private readonly List<TestCard> _cards = new();
    private readonly Dictionary<TestCard, PauseController> _cardPauseControllers = new();
    private readonly SemaphoreSlim _historySaveLock = new(1, 1);
    private TestCard? _selectedCard;

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
        // Параллельные тесты завершаются одновременно, а Save пишет один и тот же файл:
        // запись идёт строго по очереди, и каждый раз сохраняется самое свежее состояние.
        await _historySaveLock.WaitAsync();

        try
        {
            List<BenchmarkResult> snapshot = _historyItems.ToList();

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
        finally
        {
            _historySaveLock.Release();
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
        await StartSourcesAsync(new[] { result.Source });
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

        foreach (TestCard card in _cards)
            card.SetUnit(_speedUnit);
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
        var sources = new List<string>();

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                sources.AddRange(files.Select(CleanSource).Where(f => f.Length > 0));
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            sources.AddRange(ParseSources(e.Data.GetData(DataFormats.UnicodeText) as string));
        }
        else if (e.Data.GetDataPresent(DataFormats.Text))
        {
            sources.AddRange(ParseSources(e.Data.GetData(DataFormats.Text) as string));
        }

        e.Handled = true;

        if (sources.Count == 0)
            return;

        SourceTextBox.Text = string.Join(Environment.NewLine, sources);
        await StartSourcesAsync(sources);
    }

    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери файл или несколько файлов для теста",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        SourceTextBox.Text = string.Join(Environment.NewLine, dialog.FileNames);
        await StartSourcesAsync(dialog.FileNames);
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

    private int GetThreadCount()
    {
        if (!int.TryParse(ThreadsTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            value = 1;

        value = Math.Clamp(value, 1, 64);
        ThreadsTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
        return value;
    }

    // ------------------------------------------------------------------
    // Параллельный запуск нескольких источников
    // ------------------------------------------------------------------

    private TestCard CreateTestCard(string source)
    {
        var card = new TestCard(source, SourceDetector.Detect(source));
        card.SetUnit(_speedUnit);
        card.SelectionChanged += Card_SelectionChanged;

        ParallelTestsPanel.Children.Add(card);
        _cards.Add(card);
        _cardPauseControllers[card] = new PauseController();

        return card;
    }

    private void Card_SelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not TestCard card)
            return;

        if (card.IsSelected)
        {
            foreach (TestCard other in _cards.Where(other => other != card))
                other.SetSelected(false);

            _selectedCard = card;
        }
        else if (_selectedCard == card)
        {
            _selectedCard = null;
        }

        UpdatePauseButton();
    }

    /// <summary>Режим «несколько источников»: вместо одного графика — карточка на каждый источник.</summary>
    private void ShowParallelMode(int count)
    {
        ParallelTestsPanel.Children.Clear();
        _cards.Clear();
        _cardPauseControllers.Clear();
        _selectedCard = null;
        ParallelTestsPanel.Columns = count >= 2 ? 2 : 1;

        LiveGraph.Visibility = Visibility.Collapsed;
        TestProgressBar.Visibility = Visibility.Collapsed;
        ParallelTestsScrollViewer.Visibility = Visibility.Visible;
    }

    /// <summary>Режим «один источник»: общий график. Карточки прошлого запуска скрываются.</summary>
    private void ShowSingleMode()
    {
        ParallelTestsScrollViewer.Visibility = Visibility.Collapsed;
        LiveGraph.Visibility = Visibility.Visible;
        TestProgressBar.Visibility = Visibility.Visible;
    }

    private async Task StartSourcesAsync(IEnumerable<string> sourceValues)
    {
        string[] sources = sourceValues
            .Select(CleanSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToArray();

        if (sources.Length == 0)
            return;

        if (sources.Any(source => SourceDetector.Detect(source) == SourceType.Unknown))
        {
            MessageBox.Show(
                "Один или несколько источников имеют неизвестный формат.\n\n" +
                "Используй пути к существующим файлам или HTTP/HTTPS ссылки.",
                "StorageBench",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MainTabs.SelectedIndex = 0;

        bool newSession = _cancellationTokenSource is null;

        if (newSession)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _pauseController = new PauseController();
            _sessionParallelism = GetParallelism();
            _parallelSemaphore = new SemaphoreSlim(_sessionParallelism);
            _sessionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionTaskCount = 0;

            ShowParallelMode(sources.Length);
            SetRunningState(true);
        }

        TestCard[] cards = sources.Select(CreateTestCard).ToArray();
        _sessionTaskCount += cards.Length;
        StatusText.Text = newSession
            ? $"Источников: {sources.Length} · одновременно: {_sessionParallelism}"
            : $"Добавлено источников: {sources.Length} · одновременно: {_sessionParallelism}";
        UpdateOverall();

        Task[] tasks = sources
            .Select((source, index) => RunParallelSourceAsync(
                source,
                cards[index],
                _parallelSemaphore!,
                _cancellationTokenSource!.Token))
            .ToArray();

        if (!newSession)
        {
            await Task.WhenAll(tasks);
            return;
        }

        try
        {
            await _sessionCompletion!.Task;
        }
        finally
        {
            bool wasCancelled = _cancellationTokenSource?.IsCancellationRequested == true;

            _parallelSemaphore?.Dispose();
            _parallelSemaphore = null;
            _sessionCompletion = null;
            _sessionTaskCount = 0;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _pauseController = null;

            // Карточки остаются на экране: итоги и графики каждого источника видны после теста.
            ShowSummary(_cards.ToArray(), wasCancelled);
            SetRunningState(false);
        }
    }

    private void ShowSummary(TestCard[] cards, bool wasCancelled)
    {
        int withResult = cards.Count(c => c.CompletedRuns > 0);
        int failed = cards.Count(c => c.State == TestCardState.Failed);

        string text = $"{(wasCancelled ? "Остановлено" : "Готово")}: " +
                      $"результат получен у {withResult} из {cards.Length}";

        if (failed > 0)
            text += $", ошибок: {failed}";

        StatusText.Text = text;
        ProgressText.Text = string.Empty;
    }

    /// <summary>Суммарная строка под карточками: общая скорость и сколько тестов в каком состоянии.</summary>
    private void UpdateOverall()
    {
        int active = _cards.Count(c => c.State is TestCardState.Running or TestCardState.Paused);
        int limit = _cancellationTokenSource is null ? GetParallelism() : _sessionParallelism;
        ParallelInfoText.Text = $"Параллельно: {active}/{limit}";

        if (_cards.Count == 0 || _cancellationTokenSource is null)
            return;

        int queued = _cards.Count(c => c.State == TestCardState.Queued);
        int finished = _cards.Count(c => c.State is
            TestCardState.Completed or TestCardState.Failed or TestCardState.Cancelled);
        double averageSpeed = _cards.Count > 0
            ? _cards.Average(c => c.CurrentSpeedMBps)
            : 0;

        ProgressText.Text =
            $"Средняя скорость: {FormatSpeed(averageSpeed)}   |   " +
            $"Активно: {active}   |   В очереди: {queued}   |   " +
            $"Завершено: {finished} из {_cards.Count}";
    }

    private async Task RunParallelSourceAsync(
        string source,
        TestCard card,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        SourceType sourceType = SourceDetector.Detect(source);
        int run = 0;

        try
        {
            do
            {
                // Источник занимает слот только на время одного прогона. В режиме Loop источник
                // после каждого прогона снова встаёт в очередь, поэтому при лимите параллельности
                // меньше числа источников по очереди проверяются все, а не только первые.
                card.SetQueued();
                UpdateOverall();

                await semaphore.WaitAsync(cancellationToken);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    run++;
                    card.BeginRun(run);

                    PauseController pauseController = _cardPauseControllers[card];

                    if (pauseController.IsPaused)
                        card.SetPaused(true);

                    UpdateOverall();

                    long? fileSize = sourceType == SourceType.Local
                        ? new FileInfo(source).Length
                        : null;

                    var progress = new Progress<ReadProgress>(p =>
                    {
                        if (p.IsCompleted)
                            return;

                        card.ReportProgress(p, fileSize);
                        UpdateOverall();
                    });

                    BenchmarkResult result = await RunWithThreadsAsync(
                        source,
                        sourceType,
                        GetThreadCount(),
                        new BenchmarkSettings
                        {
                            SourcePath = source,
                            BufferSize = 1024,
                            DisableWindowsCache = false,
                            PauseController = pauseController,
                            ThreadCount = GetThreadCount()
                        },
                        progress,
                        cancellationToken);

                    result.CompletedAt = DateTime.Now;
                    result.Status = "OK";

                    card.Complete(result);
                    UpdateOverall();

                    await AddToHistoryAsync(result);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            while (LoopCheckBox.IsChecked == true && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            card.Cancel();
        }
        catch (Exception ex)
        {
            // Ошибка одного источника не останавливает остальные.
            card.Fail(ex.Message);

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
            _sessionTaskCount--;
            if (_sessionTaskCount == 0)
                _sessionCompletion?.TrySetResult();

            UpdateOverall();
        }
    }

    private static IReadEngine CreateEngine(SourceType sourceType) => sourceType switch
    {
        SourceType.Local => new LocalReadEngine(),
        SourceType.Network => new NetworkReadEngine(),
        SourceType.Internet => new InternetReadEngine(),
        _ => throw new NotSupportedException("Этот тип источника пока не поддерживается.")
    };

    private static async Task<BenchmarkResult> RunWithThreadsAsync(
        string source,
        SourceType sourceType,
        int threadCount,
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        threadCount = Math.Clamp(threadCount, 1, 64);

        if (sourceType == SourceType.Internet)
            threadCount = 1;

        long? sourceLength = sourceType is SourceType.Local or SourceType.Network
            ? new FileInfo(source).Length
            : null;

        threadCount = sourceLength is > 0
            ? Math.Min(threadCount, (int)Math.Min(sourceLength.Value, 64))
            : 1;
        long segmentSize = sourceLength.HasValue
            ? (sourceLength.Value + threadCount - 1) / threadCount
            : 0;
        var threadProgress = new ReadProgress[threadCount];
        object progressLock = new();

        Task<BenchmarkResult>[] tasks = Enumerable.Range(0, threadCount)
            .Select(index =>
            {
                IReadEngine engine = CreateEngine(sourceType);
                var threadSettings = new BenchmarkSettings
                {
                    SourcePath = settings.SourcePath,
                    BufferSize = settings.BufferSize,
                    DisableWindowsCache = settings.DisableWindowsCache,
                    PauseController = settings.PauseController,
                    StartOffset = sourceLength.HasValue ? index * segmentSize : 0,
                    MaxBytes = sourceLength.HasValue
                        ? Math.Min(segmentSize, Math.Max(0, sourceLength.Value - index * segmentSize))
                        : null,
                    ThreadCount = 1
                };

                var threadReporter = new InlineProgress<ReadProgress>(sample =>
                {
                    if (sample.IsCompleted)
                        return;

                    ReadProgress aggregate;
                    lock (progressLock)
                    {
                        threadProgress[index] = sample;
                        aggregate = new ReadProgress
                        {
                            BytesRead = threadProgress.Sum(item => item?.BytesRead ?? 0),
                            CurrentSpeedMBps = threadProgress.Sum(item => item?.CurrentSpeedMBps ?? 0),
                            AverageSpeedMBps = threadProgress.Sum(item => item?.AverageSpeedMBps ?? 0),
                            Elapsed = threadProgress.Max(item => item?.Elapsed ?? TimeSpan.Zero)
                        };
                    }

                    progress?.Report(aggregate);
                });

                return engine.RunAsync(threadSettings, threadReporter, cancellationToken);
            })
            .ToArray();

        BenchmarkResult[] results = await Task.WhenAll(tasks);
        BenchmarkResult result = results[0];
        TimeSpan duration = results.Max(item => item.Duration);
        long totalBytes = results.Sum(item => item.TotalBytesRead);

        result.TotalBytesRead = totalBytes;
        result.Duration = duration;
        result.AverageSpeedMBps = duration.TotalSeconds > 0
            ? totalBytes / 1024d / 1024d / duration.TotalSeconds
            : results.Sum(item => item.AverageSpeedMBps);
        result.ReachedEnd = results.All(item => item.ReachedEnd);
        result.Samples = MergeSamples(results);

        if (result.Samples.Count > 0)
        {
            result.MinimumSpeedMBps = result.Samples.Min(sample => sample.SpeedMBps);
            result.MaximumSpeedMBps = result.Samples.Max(sample => sample.SpeedMBps);
        }

        result.Source = source;
        result.SourceKind = sourceType.ToString();

        return result;
    }

    private static List<SpeedSample> MergeSamples(IReadOnlyList<BenchmarkResult> results)
    {
        int count = results.Max(result => result.Samples.Count);
        var merged = new List<SpeedSample>(count);

        for (int index = 0; index < count; index++)
        {
            SpeedSample[] samples = results
                .Where(result => index < result.Samples.Count)
                .Select(result => result.Samples[index])
                .ToArray();

            if (samples.Length > 0)
            {
                merged.Add(new SpeedSample(
                    samples.Max(sample => sample.TimeSeconds),
                    samples.Sum(sample => sample.SpeedMBps)));
            }
        }

        return merged;
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
        ShowSingleMode();

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
                    DisableWindowsCache = false,
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
        if (_selectedCard is not null &&
            (_selectedCard.State is TestCardState.Running or TestCardState.Paused) &&
            _cardPauseControllers.TryGetValue(_selectedCard, out PauseController? selectedController))
        {
            bool pause = !selectedController.IsPaused;

            if (pause)
                selectedController.Pause();
            else
                selectedController.Resume();

            _selectedCard.SetPaused(pause);
            StatusText.Text = pause
                ? "Выбранный тест приостановлен"
                : "Выбранный тест продолжается";
        }
        else if (_cardPauseControllers.Count == 0)
        {
            return;
        }
        else
        {
            bool pause = _cards.Any(card => card.State == TestCardState.Running);

            foreach (TestCard card in _cards)
            {
                PauseController controller = _cardPauseControllers[card];

                if (pause)
                    controller.Pause();
                else
                    controller.Resume();

                card.SetPaused(pause);
            }

            StatusText.Text = pause
                ? "Все тесты приостановлены"
                : "Все тесты продолжаются";
        }

        UpdatePauseButton();
        UpdateOverall();
    }

    private void UpdatePauseButton()
    {
        if (_selectedCard is not null &&
            (_selectedCard.State is TestCardState.Running or TestCardState.Paused) &&
            _cardPauseControllers.TryGetValue(_selectedCard, out PauseController? controller))
        {
            PauseButton.Content = controller.IsPaused ? "Продолжить" : "Пауза";
            return;
        }

        bool allPaused = _cards.Any() &&
                         _cards.Where(card => card.State is TestCardState.Running or TestCardState.Paused)
                               .All(card => card.State == TestCardState.Paused);
        PauseButton.Content = allPaused ? "Продолжить" : "Пауза";
    }

    private void SetRunningState(bool isRunning)
    {
        PauseButton.IsEnabled = isRunning;
        CancelButton.IsEnabled = isRunning;
        StartButton.IsEnabled = true;
        SpeedUnitComboBox.IsEnabled = true;
        SourceTextBox.IsEnabled = true;
        ParallelTextBox.IsEnabled = true;
        RerunSelectedButton.IsEnabled = !isRunning &&
            HistoryGrid.SelectedItem is BenchmarkResult;

        if (!isRunning)
            PauseButton.Content = "Пауза";

        if (!isRunning)
        {
            TestProgressBar.IsIndeterminate = false;
            ParallelInfoText.Text = "Параллельно: 0/" + GetParallelism();
        }

        UpdatePauseButton();
    }

    private sealed class SpeedValueConverter : IValueConverter
    {
        public SpeedUnit Unit { get; set; } = SpeedUnit.MegabytesPerSecond;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            SpeedUnitFormatter.Convert((double)value, Unit).ToString("F1", culture);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
