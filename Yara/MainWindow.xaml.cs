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
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using WhiteBehemoth.Resolver;
using WhiteBehemoth.Resolver.Llm;
using WhiteBehemoth.Resolver.Models;
using WhiteBehemoth.Yara.Models;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara;

public partial class MainWindow : Window
{
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly AppSettings _settings;

    private string? _currentFilePath;
    private List<ResolvedHomograph> _allHomographs = [];
    private List<ResolvedHomograph> _lowConfidence = [];
    private int _currentHomographIndex = -1;
    private bool _suppressTextChanged;

    private HomographColorizer? _colorizer;

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
        if (TextEditor.Document != null)
            TextEditor.Document.Changed += TextDocument_Changed;

        // OpenAI configs
        _openAiConfigs = new ObservableCollection<OpenAiEndpoint>(_settings.Llm.OpenAiEndpoints);
        ActiveOpenAiConfig.ItemsSource = _openAiConfigs;
        OpenAiConfigList.ItemsSource = _openAiConfigs;

        _openAiConfigs.CollectionChanged += (_, _) =>
            _settings.Llm.OpenAiEndpoints = [.. _openAiConfigs];

        // Load known foundry models from config into the dropdown
        foreach (var modelId in _settings.Llm.KnownFoundryModels)
            ActiveFoundryModel.Items.Add(new FoundryModelItem { Id = modelId, IsCached = true });

        // Restore active provider selection
        var provider = _settings.Llm.SelectedProvider ?? "";
        if (provider.StartsWith("foundry:"))
        {
            UseFoundry.IsChecked = true;
            // Restore selection in the foundry model dropdown
            var modelId = provider[8..];
            foreach (FoundryModelItem item in ActiveFoundryModel.Items)
            {
                if (item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveFoundryModel.SelectedItem = item;
                    break;
                }
            }
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

        string tarrget;
        if (_currentFilePath == null)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Сохранить текстовый файл",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            tarrget = dlg.FileName;
        }
        else
        {
            if (!Directory.Exists(_settings.General.TargetFolder))
            {
                Directory.CreateDirectory(_settings.General.TargetFolder);
            }
            tarrget = Path.Combine(_settings.General.TargetFolder, Path.GetFileName(_currentFilePath));
        }
        File.WriteAllText(tarrget, GetPlainText(), Encoding.UTF8);
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

            // Prepare AvalonEdit text and colorizers
            _suppressTextChanged = true;
            TextEditor.Text = text;
            _suppressTextChanged = false;
            TextEditor.IsReadOnly = true;

            _allHomographs.Clear();
            TextEditor.TextArea.TextView.LineTransformers.Clear();
            
            var docMatches = matches.Select(m => new DocumentMatch { Start = m.Start, Length = m.Length }).ToList();
            var matchColorizer = new MatchColorizer(docMatches);
            _colorizer = new HomographColorizer(_allHomographs, () => _settings.Homograph.Threshold);
            TextEditor.TextArea.TextView.LineTransformers.Add(matchColorizer);
            TextEditor.TextArea.TextView.LineTransformers.Add(_colorizer);

            int shift = 0;
            int i = 0;

            await foreach (var resolved in ResolutionService.ResolveAsync(
                matches, llmClient, OnLlmError, _resolutionCts.Token))
            {
                resolved.AbsolutePosition = resolved.OriginalPosition + shift;
                resolved.Length = resolved.StressedWord.Length;

                _allHomographs.Add(resolved);

                // Update text
                _suppressTextChanged = true;
                TextEditor.Document.Replace(resolved.AbsolutePosition, resolved.OriginalLength, resolved.StressedWord);
                _suppressTextChanged = false;

                // Shift subsequent matches
                int currentShift = resolved.StressedWord.Length - resolved.OriginalLength;
                for (int j = i + 1; j < docMatches.Count; j++)
                {
                    docMatches[j].Start += currentShift;
                }
                shift += currentShift;
                
                // Hide from match colorizer
                docMatches[i].Length = 0;

                TextEditor.TextArea.TextView.Redraw(resolved.AbsolutePosition, resolved.Length);
                TextEditor.ScrollToLine(TextEditor.Document.GetLineByOffset(resolved.AbsolutePosition).LineNumber);

                StatusInfo.Text = $"Омографы: {i + 1}/{matches.Count} — {resolved.Reasoning}";
                i++;
            }

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

    private void ShowResolvedText(string text, List<ResolvedHomograph> homographs, double threshold)
    {
        _suppressTextChanged = true;
        TextEditor.Text = text;
        _suppressTextChanged = false;
        
        TextEditor.TextArea.TextView.LineTransformers.Clear();
        _colorizer = new HomographColorizer(homographs, () => _settings.Homograph.Threshold);
        TextEditor.TextArea.TextView.LineTransformers.Add(_colorizer);

        _lowConfidence.Clear();
        _currentHomographIndex = -1;

        foreach (var h in homographs)
        {
            if (h.Confidence * 100 < threshold)
            {
                _lowConfidence.Add(h);
            }
        }

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
        _lowConfidence.Clear();
        var threshold = _settings.Homograph.Threshold;

        for (int i = 0; i < _allHomographs.Count; i++)
        {
            var h = _allHomographs[i];

            if (h.Confidence * 100 < threshold)
            {
                _lowConfidence.Add(h);
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
        
        TextEditor.TextArea.TextView.Redraw();
    }

    // ── Accent dictionaries ──────────────────────────────────────────────────

    private void ApplyStress_Click(object sender, RoutedEventArgs e)
    {
        var text = GetPlainText();
        if (string.IsNullOrWhiteSpace(text))
        {
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

        var stressMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in _settings.Homograph.DicAPath)
        {
            var path = ResolvePath(rawPath);
            if (!File.Exists(path)) continue;

            foreach (var (word, entry) in AccentService.LoadStressEntries(path, wordSet))
                stressMap.TryAdd(word, entry);
        }

        if (stressMap.Count == 0)
        {
            MessageBox.Show("Нет coincidences в словарях ударений.", "Внимание",
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

        var h = _lowConfidence[_currentHomographIndex];
        if (!TryAlignHomographPosition(h))
        {
            var result = MessageBox.Show(
                "Не удалось сопоставить текущий омограф с ожидаемыми вариантами.\n" +
                "Навигация сбита из-за изменений текста.\n\n" +
                "Удалить выделение омографов?",
                "Навигация сбита",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
                ResetHomographState();

            return;
        }
        
        _colorizer?.SetHighlighted(h);
        TextEditor.TextArea.TextView.Redraw();
        
        int selectStart = Math.Clamp(h.AbsolutePosition, 0, TextEditor.Document.TextLength);
        int maxLength = TextEditor.Document.TextLength - selectStart;
        int selectLength = Math.Clamp(h.Length, 0, maxLength);

        TextEditor.Select(selectStart, selectLength);
        var line = TextEditor.Document.GetLineByOffset(selectStart);
        TextEditor.ScrollToLine(line.LineNumber);

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

        var homograph = _lowConfidence[_currentHomographIndex];
        if (homograph.Variants.Count < variantIndex) return;

        var variant = homograph.Variants[variantIndex - 1];
        var oldLength = homograph.Length;
        
        _suppressTextChanged = true;
        TextEditor.Document.Replace(homograph.AbsolutePosition, homograph.Length, variant.Target);
        _suppressTextChanged = false;
        
        homograph.StressedWord = variant.Target;
        homograph.ChosenIndex = variant.Ref;
        homograph.Confidence = 1.0;
        homograph.Length = variant.Target.Length;

        var delta = homograph.Length - oldLength;
        if (delta != 0) UpdateOffsets(homograph.AbsolutePosition, delta);

        PromoteResolvedHomograph(homograph);
    }

    private void ConfirmCurrentVariant()
    {
        if (_lowConfidence.Count == 0 || _currentHomographIndex < 0) return;

        var homograph = _lowConfidence[_currentHomographIndex];
        homograph.Confidence = 1.0;

        PromoteResolvedHomograph(homograph);
    }

    private void PromoteResolvedHomograph(ResolvedHomograph homograph)
    {
        if (homograph.Confidence * 100 >= _settings.Homograph.Threshold)
        {
            _lowConfidence.RemoveAt(_currentHomographIndex);
            _colorizer?.SetHighlighted(null);

            if (_lowConfidence.Count == 0)
            {
                PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
                HomographIndexText.Text = "";
                StatusInfo.Text = "Все омографы разрешены.";
                _currentHomographIndex = -1;
                TextEditor.TextArea.TextView.Redraw();
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
        var selection = TextEditor.SelectedText;
        if (selection == pattern)
        {
            TextEditor.SelectedText = replacement;
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
        return TextEditor.Text ?? "";
    }

    private int GetCursorIndex()
    {
        return TextEditor.CaretOffset;
    }

    private void SelectInText(int index, int length)
    {
        TextEditor.Select(index, length);
        var line = TextEditor.Document.GetLineByOffset(index);
        TextEditor.ScrollToLine(line.LineNumber);
        TextEditor.Focus();
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
        // Selection drives DataContext binding for the details panel
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
            var knownIds = new List<string>();

            foreach (var m in models)
            {
                var isCached = await m.IsCachedAsync();
                ActiveFoundryModel.Items.Add(new FoundryModelItem { Id = m.Id, IsCached = isCached });
                knownIds.Add(m.Id);
            }

            // Persist known models for next startup
            _settings.Llm.KnownFoundryModels = knownIds;

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
            ActiveFoundryModel.Items.Add(new FoundryModelItem
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
    private void BrowseTargetFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TargetFolder.Text = dlg.SelectedPath;
        }
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

    private void TextEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;
        if (string.IsNullOrWhiteSpace(GetPlainText()))
            ResetHomographState();
    }

    private void TextDocument_Changed(object? sender, DocumentChangeEventArgs e)
    {
        if (_suppressTextChanged || _allHomographs.Count == 0) return;

        int delta = e.InsertionLength - e.RemovalLength;
        if (delta == 0 && e.RemovalLength == 0) return;

        int changeStart = e.Offset;
        int changeEnd = e.Offset + e.RemovalLength;

        foreach (var h in _allHomographs)
        {
            int hStart = h.AbsolutePosition;
            int hEnd = hStart + h.Length;

            if (hEnd <= changeStart)
                continue;

            if (hStart >= changeEnd)
            {
                h.AbsolutePosition += delta;
                continue;
            }

            h.AbsolutePosition = Math.Min(hStart, changeStart);
            h.Length = Math.Max(0, h.Length + delta);
        }
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
        return TextEditor.Text;
    }

    private void SetPlainText(string text)
    {
        _suppressTextChanged = true;
        TextEditor.Text = text;
        TextEditor.TextArea.TextView.LineTransformers.Clear();
        _suppressTextChanged = false;
    }

    private void ResetHomographState()
    {
        _allHomographs = [];
        _lowConfidence.Clear();
        _currentHomographIndex = -1;
        PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
        HomographIndexText.Text = "";
        StatusHomographCount.Text = "0";
        TextEditor.TextArea.TextView.LineTransformers.Clear();
        UpdateLlmStatusDisplay();
    }

    private bool TryAlignHomographPosition(ResolvedHomograph homograph)
    {
        string text = GetPlainText();
        if (string.IsNullOrEmpty(text))
            return false;

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(homograph.StressedWord))
            expected.Add(homograph.StressedWord);

        foreach (var variant in homograph.Variants)
        {
            if (!string.IsNullOrWhiteSpace(variant.Target))
                expected.Add(variant.Target);
        }

        if (expected.Count == 0)
            return false;

        if (TryMatchAt(homograph.AbsolutePosition, expected, text, out int matchedLength))
        {
            homograph.Length = matchedLength;
            return true;
        }

        const int searchRadius = 200;
        int from = Math.Max(0, homograph.AbsolutePosition - searchRadius);
        int to = Math.Min(text.Length, homograph.AbsolutePosition + searchRadius);
        int bestIndex = -1;
        int bestLength = 0;
        int bestDistance = int.MaxValue;

        foreach (var token in expected)
        {
            int index = from;
            while (index < to)
            {
                int found = text.IndexOf(token, index, to - index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;

                int distance = Math.Abs(found - homograph.AbsolutePosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = found;
                    bestLength = token.Length;
                }

                index = found + 1;
            }
        }

        if (bestIndex < 0)
            return false;

        homograph.AbsolutePosition = bestIndex;
        homograph.Length = bestLength;
        return true;
    }

    private static bool TryMatchAt(int position, HashSet<string> expected, string text, out int matchedLength)
    {
        matchedLength = 0;
        if (position < 0 || position >= text.Length)
            return false;

        foreach (var token in expected)
        {
            if (position + token.Length > text.Length)
                continue;

            if (string.Compare(text, position, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                matchedLength = token.Length;
                return true;
            }
        }

        return false;
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
