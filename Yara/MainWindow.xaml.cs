using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HomographResolver;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using WhiteBehemoth.Resolver;
using WhiteBehemoth.Yara.Models;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara;

public partial class MainWindow : Window
{
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly AppSettings _settings;

    private string? _currentFilePath;
    private List<ResolvedHomograph> _allHomographs = [];
    private List<Run> _homographRuns = [];
    private List<(ResolvedHomograph Homograph, Run Run)> _lowConfidence = [];
    private int _currentHomographIndex = -1;
    private Run? _highlightedRun;
    private bool _suppressTextChanged;

    private CancellationTokenSource? _resolutionCts;
    private readonly ObservableCollection<CleanRule> _cleanRules = [];
    private ObservableCollection<OpenAiEndpoint> _openAiConfigs = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MainWindow(ILlmClientFactory llmClientFactory, IOptionsMonitor<AppSettings> optionsMonitor)
    {
        _llmClientFactory = llmClientFactory;
        _settings = optionsMonitor.CurrentValue;
        InitializeComponent();
        DataContext = _settings;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // OpenAI configs
        _openAiConfigs = new ObservableCollection<OpenAiEndpoint>(_settings.Llm.OpenAiEndpoints);
        ActiveOpenAiConfig.ItemsSource = _openAiConfigs;
        OpenAiConfigList.ItemsSource = _openAiConfigs;

        _openAiConfigs.CollectionChanged += (_, _) =>
            _settings.Llm.OpenAiEndpoints = [.. _openAiConfigs];

        OaiApiKey.PasswordChanged += (_, _) =>
        {
            if (OpenAiConfigList.SelectedItem is OpenAiEndpoint ep)
                ep.ApiKey = OaiApiKey.Password;
        };

        // Restore active provider selection
        var provider = _settings.Llm.SelectedProvider ?? "";
        if (provider.StartsWith("foundry:"))
        {
            UseFoundry.IsChecked = true;
        }
        else
        {
            UseOpenAi.IsChecked = true;
            int idx = 0;
            if (provider.StartsWith("openai:") && int.TryParse(provider[7..], out var parsed))
                idx = parsed;
            if (idx < _openAiConfigs.Count)
                ActiveOpenAiConfig.SelectedIndex = idx;
        }

        // Clean rules
        CleanRulesComboBox.ItemsSource = _cleanRules;
        LoadCleanRules();

        // Threshold change → rebuild navigation
        _settings.Homograph.PropertyChanged += (s, ev) =>
        {
            if (ev.PropertyName == nameof(Settings.HomographConfig.Threshold) && _allHomographs.Count > 0)
                Dispatcher.Invoke(RebuildLowConfidenceList);
            if (ev.PropertyName == nameof(Settings.HomographConfig.CleanRegexPath))
                Dispatcher.Invoke(LoadCleanRules);
        };

        UpdateLlmStatusDisplay();
        _settings.EnableAutoSave();
    }

    // ── File menu ────────────────────────────────────────────────────────────

    private void Open_Click(object sender, ExecutedRoutedEventArgs e) => OpenFile(Encoding.UTF8);

    private void OpenNonUnicode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag && int.TryParse(tag, out int cp))
            OpenFile(Encoding.GetEncoding(cp));
    }

    private async void OpenFile(Encoding encoding)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть текстовый файл",
            Filter = "Текстовые файлы (*.txt;*.fb2)|*.txt;*.fb2|fb2 (*.fb2)|*.fb2|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        string text = File.ReadAllText(dlg.FileName, encoding);
        if (Path.GetExtension(dlg.FileName) == ".fb2")
        {
            var fd = new Fb2.Document.Fb2Document();
            await fd.LoadAsync(text, new Fb2.Document.LoadingOptions.Fb2LoadingOptions());
            text = string.Join(Environment.NewLine, fd.Bodies);
        }

        SetPlainText(text);
        ResetHomographState();
        _currentFilePath = dlg.FileName;
        Title = $"Яра — {Path.GetFileName(dlg.FileName)}";
        MainTabs.SelectedIndex = 0;

        await UpdateStatisticsAsync(text);
    }

    private void Save_Click(object sender, ExecutedRoutedEventArgs e)
    {
        if (_currentFilePath == null)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Сохранить текстовый файл",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            _currentFilePath = dlg.FileName;
        }
        File.WriteAllText(_currentFilePath, GetPlainText(), Encoding.UTF8);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Сохранить проект",
            Filter = "Проект (*.zip)|*.zip"
        };
        if (dlg.ShowDialog() != true) return;

        var state = new ProjectState
        {
            Threshold = _settings.Homograph.Threshold,
            CurrentLowConfidenceIndex = _lowConfidence.Count > 0 ? _currentHomographIndex : -1
        };

        using var fs = File.Create(dlg.FileName);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "text.txt", GetPlainText());
        WriteEntry(zip, "homographs.json", JsonSerializer.Serialize(_allHomographs, JsonOptions));
        WriteEntry(zip, "state.json", JsonSerializer.Serialize(state, JsonOptions));
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть проект",
            Filter = "Проект (*.zip)|*.zip"
        };
        if (dlg.ShowDialog() != true) return;

        using var zip = ZipFile.OpenRead(dlg.FileName);
        var text = ReadEntryText(zip, "text.txt") ?? string.Empty;
        var homographsJson = ReadEntryText(zip, "homographs.json");
        var stateJson = ReadEntryText(zip, "state.json");

        var homographs = string.IsNullOrWhiteSpace(homographsJson)
            ? new List<ResolvedHomograph>()
            : JsonSerializer.Deserialize<List<ResolvedHomograph>>(homographsJson, JsonOptions) ?? [];

        var state = string.IsNullOrWhiteSpace(stateJson)
            ? new ProjectState()
            : JsonSerializer.Deserialize<ProjectState>(stateJson, JsonOptions) ?? new();

        _allHomographs = homographs;
        ShowResolvedText(text, homographs,
            state.Threshold > 0 ? state.Threshold : _settings.Homograph.Threshold);

        StatusHomographCount.Text = homographs.Count.ToString();
        UpdateBasicStatistics(text);
        MainTabs.SelectedIndex = 0;

        if (_lowConfidence.Count > 0 && state.CurrentLowConfidenceIndex >= 0)
        {
            _currentHomographIndex = Math.Clamp(
                state.CurrentLowConfidenceIndex, 0, _lowConfidence.Count - 1);
            NavigateToCurrentHomograph();
        }
    }

    // ── Homograph resolution (progressive) ───────────────────────────────────

    private async void ResolveHomographs_Click(object sender, RoutedEventArgs e)
    {
        var text = GetPlainText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Нет текста для анализа.", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dicPath = ResolvePath(_settings.Homograph.DictionaryPath);
        if (!File.Exists(dicPath))
        {
            MessageBox.Show($"Словарь омографов не найден:\n{dicPath}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _resolutionCts = new CancellationTokenSource();
        CancelBtn.Visibility = Visibility.Visible;
        StatusInfo.Text = "Загрузка словаря…";

        try
        {
            var dictionary = await HomographDictionary.LoadAsync(dicPath);
            var matches = TextAnalyzer.FindHomographs(text, dictionary);

            if (matches.Count == 0)
            {
                StatusInfo.Text = "Омографов не найдено.";
                return;
            }

            StatusInfo.Text = $"Словарь: {dictionary.Count} слов. Найдено омографов: {matches.Count}";

            var llmClient = _llmClientFactory.CreateClient();
            if (llmClient is IFoundryLocalLlmClient foundry)
            {
                if (!await EnsureFoundryModelReadyAsync(foundry, _resolutionCts.Token))
                    return;
            }

            // Build document with "slots" for each homograph
            BuildDocumentWithSlots(text, matches);
            TextEditor.IsReadOnly = true;

            _allHomographs.Clear();
            int shift = 0;
            int i = 0;

            await foreach (var resolved in ResolutionService.ResolveAsync(
                matches, llmClient, OnLlmError, _resolutionCts.Token))
            {
                resolved.AbsolutePosition = resolved.OriginalPosition + shift;
                resolved.Length = resolved.StressedWord.Length;
                shift += resolved.StressedWord.Length - resolved.OriginalLength;

                _allHomographs.Add(resolved);

                // Update the Run for this homograph
                var run = _homographRuns[i];
                run.Text = resolved.StressedWord;
                run.Background = resolved.Confidence * 100 >= _settings.Homograph.Threshold
                    ? Brushes.LightGreen
                    : Brushes.Yellow;
                run.BringIntoView();

                StatusInfo.Text = $"Омографы: {i + 1}/{matches.Count} — {resolved.Reasoning}";
                i++;
            }

            // Clear backgrounds for any remaining (shouldn't happen unless cancelled)
            for (int j = i; j < _homographRuns.Count; j++)
                _homographRuns[j].Background = null;

            RebuildLowConfidenceList();
            StatusHomographCount.Text = _allHomographs.Count.ToString();

            StatusInfo.Text = _lowConfidence.Count > 0
                ? $"Низкая уверенность: {_lowConfidence.Count}. Используйте навигацию."
                : "Все омографы разрешены.";
        }
        catch (OperationCanceledException)
        {
            // Keep partial results
            RebuildLowConfidenceList();
            StatusHomographCount.Text = _allHomographs.Count.ToString();

            // Clear unresolved slots
            for (int j = _allHomographs.Count; j < _homographRuns.Count; j++)
                _homographRuns[j].Background = null;

            // Trim runs list to match resolved count
            if (_homographRuns.Count > _allHomographs.Count)
                _homographRuns = _homographRuns[.._allHomographs.Count];

            StatusInfo.Text = "Процесс прерван. Частичные результаты сохранены.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка:\n{ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusInfo.Text = "";
        }
        finally
        {
            TextEditor.IsReadOnly = false;
            CancelBtn.Visibility = Visibility.Collapsed;
            _resolutionCts?.Dispose();
            _resolutionCts = null;
        }
    }

    private void BuildDocumentWithSlots(string text, List<HomographMatch> matches)
    {
        _suppressTextChanged = true;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(6)
        };
        var para = new Paragraph();
        _homographRuns = [];

        int lastPos = 0;
        foreach (var m in matches)
        {
            if (m.Start > lastPos)
                para.Inlines.Add(new Run(text[lastPos..m.Start]));

            var run = new Run(text[m.Start..(m.Start + m.Length)])
            {
                Background = Brushes.LightGray
            };
            para.Inlines.Add(run);
            _homographRuns.Add(run);
            lastPos = m.Start + m.Length;
        }

        if (lastPos < text.Length)
            para.Inlines.Add(new Run(text[lastPos..]));

        doc.Blocks.Add(para);
        TextEditor.Document = doc;
        _suppressTextChanged = false;
    }

    private void ShowResolvedText(string text, List<ResolvedHomograph> homographs, double threshold)
    {
        _suppressTextChanged = true;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(6)
        };
        var para = new Paragraph();

        _lowConfidence.Clear();
        _homographRuns = [];
        _currentHomographIndex = -1;
        _highlightedRun = null;

        int lastPos = 0;
        foreach (var h in homographs)
        {
            if (h.AbsolutePosition > lastPos)
                para.Inlines.Add(new Run(text[lastPos..h.AbsolutePosition]));

            int end = Math.Min(h.AbsolutePosition + h.Length, text.Length);
            var run = new Run(text[h.AbsolutePosition..end]);

            if (h.Confidence * 100 < threshold)
            {
                run.Background = Brushes.Yellow;
                _lowConfidence.Add((h, run));
            }
            else
            {
                run.Background = Brushes.LightGreen;
            }

            para.Inlines.Add(run);
            _homographRuns.Add(run);
            lastPos = end;
        }

        if (lastPos < text.Length)
            para.Inlines.Add(new Run(text[lastPos..]));

        doc.Blocks.Add(para);
        TextEditor.Document = doc;
        _suppressTextChanged = false;

        PrevBtn.IsEnabled = NextBtn.IsEnabled = _lowConfidence.Count > 0;
        if (_lowConfidence.Count > 0)
        {
            _currentHomographIndex = _lowConfidence.Count - 1;
            NavigateToCurrentHomograph();
        }
        else
        {
            HomographIndexText.Text = "";
        }
    }

    private void RebuildLowConfidenceList()
    {
        if (_highlightedRun != null)
        {
            _highlightedRun.Background = Brushes.Yellow;
            _highlightedRun = null;
        }

        _lowConfidence.Clear();
        var threshold = _settings.Homograph.Threshold;

        for (int i = 0; i < _allHomographs.Count && i < _homographRuns.Count; i++)
        {
            var h = _allHomographs[i];
            var run = _homographRuns[i];

            if (h.Confidence * 100 < threshold)
            {
                run.Background = Brushes.Yellow;
                _lowConfidence.Add((h, run));
            }
            else
            {
                run.Background = Brushes.LightGreen;
            }
        }

        PrevBtn.IsEnabled = NextBtn.IsEnabled = _lowConfidence.Count > 0;
        _currentHomographIndex = _lowConfidence.Count > 0 ? 0 : -1;

        if (_lowConfidence.Count > 0)
            NavigateToCurrentHomograph();
        else
            HomographIndexText.Text = "";

        StatusInfo.Text = _lowConfidence.Count > 0
            ? $"Низкая уверенность: {_lowConfidence.Count}"
            : _allHomographs.Count > 0 ? "Все омографы разрешены." : "";
    }

    // ── Accent dictionaries ──────────────────────────────────────────────────

    private void ApplyStress_Click(object sender, RoutedEventArgs e)
    {
        var text = GetPlainText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Нет текста для обработки.", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settings.Homograph.DicAPath.Count == 0)
        {
            MessageBox.Show("Не задан путь к словарям ударений.", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wordSet = new HashSet<string>(
            TextAnalyzer.WordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant()));

        var stressMap = new Dictionary<string, StressEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in _settings.Homograph.DicAPath)
        {
            var path = ResolvePath(rawPath);
            if (!File.Exists(path)) continue;

            foreach (var (word, entry) in AccentService.LoadStressEntries(path, wordSet))
                stressMap.TryAdd(word, entry);
        }

        if (stressMap.Count == 0)
        {
            MessageBox.Show("Нет совпадений в словарях ударений.", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var updated = AccentService.ApplyStressMarks(text, stressMap);
        SetPlainText(updated);
        ResetHomographState();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void PrevHomograph_Click(object sender, RoutedEventArgs e)
    {
        if (_lowConfidence.Count == 0) return;
        _currentHomographIndex = (_currentHomographIndex - 1 + _lowConfidence.Count) % _lowConfidence.Count;
        NavigateToCurrentHomograph();
    }

    private void NextHomograph_Click(object sender, RoutedEventArgs e)
    {
        if (_lowConfidence.Count == 0) return;
        _currentHomographIndex = (_currentHomographIndex + 1) % _lowConfidence.Count;
        NavigateToCurrentHomograph();
    }

    private void NavigateToCurrentHomograph()
    {
        if (_currentHomographIndex < 0 || _currentHomographIndex >= _lowConfidence.Count)
            return;

        if (_highlightedRun != null)
            _highlightedRun.Background = Brushes.Yellow;

        var (h, run) = _lowConfidence[_currentHomographIndex];
        run.Background = Brushes.Orange;
        _highlightedRun = run;
        run.BringIntoView();

        HomographIndexText.Text = $"({_currentHomographIndex + 1}/{_lowConfidence.Count})";
        var variants = string.Join(" | ", h.Variants.Select(v => v.Target));
        var reasoning = string.IsNullOrWhiteSpace(h.Reasoning) ? "" : $" ({h.Reasoning})";
        StatusInfo.Text = $"[{h.Confidence:P0}] {h.OriginalWord}: {variants}{reasoning}";
    }

    // ── Key handling ─────────────────────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt) return;

        switch (key)
        {
            case Key.S:
                NextHomograph_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.W:
                PrevHomograph_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1: ApplyVariant(1); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: ApplyVariant(2); e.Handled = true; break;
            case Key.D3 or Key.NumPad3: ApplyVariant(3); e.Handled = true; break;
            case Key.D4 or Key.NumPad4: ApplyVariant(4); e.Handled = true; break;
            case Key.OemTilde:
                ConfirmCurrentVariant();
                e.Handled = true;
                break;
        }
    }

    private void ApplyVariant(int variantIndex)
    {
        if (_lowConfidence.Count == 0 || _currentHomographIndex < 0) return;

        var (homograph, run) = _lowConfidence[_currentHomographIndex];
        if (homograph.Variants.Count < variantIndex) return;

        var variant = homograph.Variants[variantIndex - 1];
        var oldLength = run.Text.Length;
        run.Text = variant.Target;
        homograph.StressedWord = variant.Target;
        homograph.ChosenIndex = variant.Index;
        homograph.Confidence = 1.0;
        homograph.Length = variant.Target.Length;

        var delta = homograph.Length - oldLength;
        if (delta != 0) UpdateOffsets(homograph.AbsolutePosition, delta);

        PromoteResolvedHomograph(homograph, run);
    }

    private void ConfirmCurrentVariant()
    {
        if (_lowConfidence.Count == 0 || _currentHomographIndex < 0) return;

        var (homograph, run) = _lowConfidence[_currentHomographIndex];
        homograph.Confidence = 1.0;

        PromoteResolvedHomograph(homograph, run);
    }

    private void PromoteResolvedHomograph(ResolvedHomograph homograph, Run run)
    {
        if (homograph.Confidence * 100 >= _settings.Homograph.Threshold)
        {
            run.Background = Brushes.LightGreen;
            _lowConfidence.RemoveAt(_currentHomographIndex);
            _highlightedRun = null;

            if (_lowConfidence.Count == 0)
            {
                PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
                HomographIndexText.Text = "";
                StatusInfo.Text = "Все омографы разрешены.";
                _currentHomographIndex = -1;
                return;
            }

            if (_currentHomographIndex >= _lowConfidence.Count)
                _currentHomographIndex = _lowConfidence.Count - 1;
        }

        NavigateToCurrentHomograph();
    }

    private void UpdateOffsets(int fromPosition, int delta)
    {
        if (delta == 0) return;
        foreach (var h in _allHomographs)
        {
            if (h.AbsolutePosition > fromPosition)
                h.AbsolutePosition += delta;
        }
    }

    // ── Search & replace ─────────────────────────────────────────────────────

    private void ToggleFindReplace(object sender, ExecutedRoutedEventArgs e)
    {
        FindReplaceBar.Visibility = FindReplaceBar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (FindReplaceBar.Visibility == Visibility.Visible)
            FindTextBox.Focus();
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindNext_Click(sender, e); e.Handled = true; }
    }

    private void ReplaceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ReplaceNext_Click(sender, e); e.Handled = true; }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        var pattern = FindTextBox.Text;
        if (string.IsNullOrEmpty(pattern)) return;

        var text = GetSearchText();
        int startIndex = GetCursorIndex();
        int index = text.IndexOf(pattern, startIndex, StringComparison.CurrentCulture);
        if (index < 0)
        {
            MessageBox.Show("Совпадения не найдены.", "Поиск",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectInText(index, pattern.Length);
    }

    private void ReplaceNext_Click(object sender, RoutedEventArgs e)
    {
        var pattern = FindTextBox.Text;
        if (string.IsNullOrEmpty(pattern)) return;

        var replacement = ReplaceTextBox.Text;
        var selection = TextEditor.Selection;
        if (selection.Text == pattern)
        {
            selection.Text = replacement;
            ResetHomographState();
            return;
        }

        var text = GetSearchText();
        int startIndex = GetCursorIndex();
        int index = text.IndexOf(pattern, startIndex, StringComparison.CurrentCulture);
        if (index < 0)
        {
            MessageBox.Show("Совпадения не найдены.", "Замена",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectInText(index, pattern.Length);
    }

    private string GetSearchText()
    {
        var range = new TextRange(TextEditor.Document.ContentStart, TextEditor.Document.ContentEnd);
        return range.Text;
    }

    private int GetCursorIndex()
    {
        var range = new TextRange(TextEditor.Document.ContentStart, TextEditor.CaretPosition);
        return range.Text.Length;
    }

    private void SelectInText(int index, int length)
    {
        var start = GetTextPointerAtOffset(TextEditor.Document.ContentStart, index);
        var end = GetTextPointerAtOffset(TextEditor.Document.ContentStart, index + length);
        TextEditor.Selection.Select(start, end);
        TextEditor.Focus();
        TextEditor.Selection.Start.Paragraph?.BringIntoView();
    }

    // ── Clean rules ──────────────────────────────────────────────────────────

    private void CleanRuleItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBoxItem ci && ci.DataContext is CleanRule rule)
        {
            rule.IsSelected = !rule.IsSelected;
            e.Handled = true;
            CleanRulesComboBox.IsDropDownOpen = true;
        }
    }

    private void LoadCleanRules()
    {
        foreach (var r in _cleanRules) r.PropertyChanged -= CleanRuleChanged;
        _cleanRules.Clear();

        var path = ResolvePath(_settings.Homograph.CleanRegexPath);
        if (!File.Exists(path)) return;

        string? pendingDesc = null;
        foreach (var rawLine in File.ReadLines(path))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith('#')) { pendingDesc = trimmed.TrimStart('#').Trim(); continue; }

            int sep = rawLine.IndexOf('=');
            if (sep < 0) continue;

            var rule = new CleanRule(
                string.IsNullOrWhiteSpace(pendingDesc) ? rawLine[..sep] : pendingDesc,
                rawLine[..sep],
                rawLine[(sep + 1)..]);
            rule.PropertyChanged += CleanRuleChanged;
            _cleanRules.Add(rule);
            pendingDesc = null;
        }
    }

    private void CleanRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanRule.IsSelected))
            CleanRulesComboBox.Text = "Очистка текста";
    }

    private async void ApplyCleanRules_Click(object sender, RoutedEventArgs e)
    {
        var selected = _cleanRules.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одно правило.", "Очистка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = GetPlainText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Нет текста для очистки.", "Очистка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            foreach (var rule in selected)
                text = Regex.Replace(text, rule.Pattern, rule.Replacement, RegexOptions.Multiline);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка:\n{ex.Message}", "Очистка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetPlainText(text);
        ResetHomographState();
        await UpdateStatisticsAsync(text);
    }

    // ── LLM settings UI ─────────────────────────────────────────────────────

    private void LlmProvider_Changed(object sender, RoutedEventArgs e) => SaveActiveProvider();

    private void ActiveOpenAiConfig_Changed(object sender, SelectionChangedEventArgs e) => SaveActiveProvider();

    private void ActiveFoundryModel_Changed(object sender, SelectionChangedEventArgs e) => SaveActiveProvider();

    private void SaveActiveProvider()
    {
        if (UseFoundry.IsChecked == true && ActiveFoundryModel.SelectedItem is FoundryModelItem fm)
        {
            _settings.Llm.SelectedProvider = $"foundry:{fm.Id}";
        }
        else if (ActiveOpenAiConfig.SelectedIndex >= 0)
        {
            _settings.Llm.SelectedProvider = $"openai:{ActiveOpenAiConfig.SelectedIndex}";
        }

        UpdateLlmStatusDisplay();
    }

    private void UpdateLlmStatusDisplay()
    {
        StatusInfo.Text = $"LLM: {_settings.Llm.GetActiveDisplayName()}";
    }

    private void OpenAiConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenAiConfigList.SelectedItem is OpenAiEndpoint ep)
            OaiApiKey.Password = ep.ApiKey;
    }

    private void AddOpenAiConfig_Click(object sender, RoutedEventArgs e)
    {
        var ep = new OpenAiEndpoint { Name = "Новый", Url = "https://", Model = "" };
        _openAiConfigs.Add(ep);
        OpenAiConfigList.SelectedItem = ep;
    }

    private void RemoveOpenAiConfig_Click(object sender, RoutedEventArgs e)
    {
        if (OpenAiConfigList.SelectedItem is OpenAiEndpoint ep && _openAiConfigs.Count > 1)
            _openAiConfigs.Remove(ep);
    }

    private async void RefreshFoundryModels_Click(object sender, RoutedEventArgs e)
    {
        FoundryModelList.Items.Clear();
        ActiveFoundryModel.Items.Clear();

        try
        {
            if (!Microsoft.AI.Foundry.Local.FoundryLocalManager.IsInitialized)
            {
                await Microsoft.AI.Foundry.Local.FoundryLocalManager.CreateAsync(
                    new Microsoft.AI.Foundry.Local.Configuration { AppName = "yara" },
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            }

            var catalog = await Microsoft.AI.Foundry.Local.FoundryLocalManager.Instance.GetCatalogAsync();
            var models = await catalog.ListModelsAsync();

            foreach (var m in models)
            {
                var isCached = await m.IsCachedAsync();
                var item = new FoundryModelItem { Id = m.Id, IsCached = isCached };
                FoundryModelList.Items.Add(item);
                ActiveFoundryModel.Items.Add(item);
            }

            // Restore selection
            var current = _settings.Llm.SelectedProvider ?? "";
            if (current.StartsWith("foundry:"))
            {
                var modelId = current[8..];
                foreach (FoundryModelItem item in ActiveFoundryModel.Items)
                {
                    if (item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActiveFoundryModel.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FoundryModelList.Items.Add(new FoundryModelItem
            {
                Id = $"Ошибка: {ex.Message}",
                IsCached = false
            });
        }
    }

    private async Task<bool> EnsureFoundryModelReadyAsync(
        IFoundryLocalLlmClient foundryClient, CancellationToken ct)
    {
        StatusInfo.Text = "Проверка модели Foundry Local…";
        var modelStatus = await foundryClient.GetModelStatusAsync(ct);
        if (modelStatus == null)
            throw new InvalidOperationException("Не удалось получить сведения о модели.");

        var modelName = string.IsNullOrWhiteSpace(modelStatus.Alias)
            ? modelStatus.Id : modelStatus.Alias;

        if (!modelStatus.IsCached)
        {
            var sizeText = modelStatus.SizeBytes.HasValue
                ? FormatSize(modelStatus.SizeBytes.Value)
                : "неизвестный размер";
            var result = MessageBox.Show(
                $"Модель \"{modelName}\" не загружена ({sizeText}). Скачать?",
                "Foundry Local", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                StatusInfo.Text = "Загрузка отменена.";
                return false;
            }
        }

        var prefix = modelStatus.IsCached ? "Подготовка модели" : "Загрузка модели";
        StatusInfo.Text = $"{prefix}…";
        var progress = new Progress<float>(p =>
            StatusInfo.Text = $"{prefix}: {p:F0}%…");
        await foundryClient.PrepareAsync(progress, ct);
        return true;
    }

    // ── Browse dialogs ───────────────────────────────────────────────────────

    private void BrowseDic_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Словарь омографов", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() == true) DicPath.Text = dlg.FileName;
    }

    private void BrowseDicA_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Словарь ударений", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() == true) DicAPath.Text += (Environment.NewLine + dlg.FileName);
    }

    private void BrowseCleanRegex_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Файл правил очистки",
            Filter = "Rex (*.rex)|*.rex|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) CleanRegexPath.Text = dlg.FileName;
    }

    // ── Statistics ───────────────────────────────────────────────────────────

    private void UpdateBasicStatistics(string text)
    {
        StatusWords.Text = TextAnalyzer.CountWords(text).ToString();
        StatusSentences.Text = TextAnalyzer.CountSentences(text).ToString();
    }

    private async Task UpdateStatisticsAsync(string text)
    {
        UpdateBasicStatistics(text);
        try
        {
            var dicPath = ResolvePath(_settings.Homograph.DictionaryPath);
            if (!File.Exists(dicPath)) { StatusHomographCount.Text = "0"; return; }
            var dictionary = await HomographDictionary.LoadAsync(dicPath);
            StatusHomographCount.Text = TextAnalyzer.CountHomographs(text, dictionary).ToString();
        }
        catch { StatusHomographCount.Text = "0"; }
    }

    // ── Font zoom ────────────────────────────────────────────────────────────

    private void Text_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var control = (Control)sender;
        control.FontSize = Math.Clamp(control.FontSize + (e.Delta > 0 ? 1 : -1), 8, 48);
        e.Handled = true;
    }

    private void TextEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        if (_suppressTextChanged) return;
        if (string.IsNullOrWhiteSpace(GetPlainText()))
            ResetHomographState();
    }

    private void UpdatePlaceholder()
    {
        var text = GetPlainText();
        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Прервать процесс? Частичные результаты будут сохранены.",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _resolutionCts?.Cancel();
    }

    // ── LLM error callback ──────────────────────────────────────────────────

    private async Task<bool> OnLlmError(int current, int total)
    {
        var result = MessageBox.Show(
            $"Ошибка LLM ({current}/{total}).\n\nПродолжить (случайный вариант)?\n\nДа — продолжить, Нет — прервать.",
            "Ошибка LLM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string GetPlainText()
    {
        var range = new TextRange(TextEditor.Document.ContentStart, TextEditor.Document.ContentEnd);
        return range.Text.TrimEnd('\r', '\n');
    }

    private void SetPlainText(string text)
    {
        _suppressTextChanged = true;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(6)
        };
        doc.Blocks.Add(new Paragraph(new Run(text)));
        TextEditor.Document = doc;
        _suppressTextChanged = false;
        UpdatePlaceholder();
    }

    private void ResetHomographState()
    {
        _allHomographs = [];
        _homographRuns = [];
        _lowConfidence.Clear();
        _currentHomographIndex = -1;
        _highlightedRun = null;
        PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
        HomographIndexText.Text = "";
        StatusHomographCount.Text = "0";
        UpdateLlmStatusDisplay();
    }

    private static TextPointer GetTextPointerAtOffset(TextPointer start, int offset)
    {
        int count = 0;
        var nav = start;
        while (nav != null)
        {
            var ctx = nav.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = nav.GetTextInRun(LogicalDirection.Forward);
                if (count + run.Length >= offset)
                    return nav.GetPositionAtOffset(offset - count) ?? nav;
                count += run.Length;
                nav = nav.GetPositionAtOffset(run.Length);
            }
            else if (ctx == TextPointerContext.ElementEnd && nav.Parent is Paragraph)
            {
                int nl = Environment.NewLine.Length;
                if (count + nl >= offset)
                    return nav.GetNextInsertionPosition(LogicalDirection.Forward) ?? nav;
                count += nl;
                nav = nav.GetNextContextPosition(LogicalDirection.Forward);
            }
            else
            {
                nav = nav.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        return start;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 Б";
        string[] suffixes = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = bytes;
        int order = 0;
        while (size >= 1024 && order < suffixes.Length - 1) { size /= 1024; order++; }
        return string.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", size, suffixes[order]);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string? ReadEntryText(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
