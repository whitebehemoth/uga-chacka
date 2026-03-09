using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HomographResolver;
using Microsoft.Win32;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace uga_chacka
{
    public partial class MainWindow : Window
    {
        private readonly ILlmClientFactory _llmClientFactory;
        private string? _currentFilePath;
        private readonly AppSettings _settings;
        private List<ResolvedHomograph> _allHomographs = [];
        private List<(ResolvedHomograph Homograph, Run Run)> _lowConfidence = [];
        private int _currentHomographIndex = -1;
        private Run? _highlightedRun;
        private bool _suppressResultTextChanged;
        private readonly ObservableCollection<CleanRule> _cleanRules = [];
        private ComboBox? _cleanRulesComboBox;

        private CancellationTokenSource? _resolutionCts;

        private static readonly string SettingsPath = Path.Combine(
            AppContext.BaseDirectory, "appsettings.json");

        private static readonly Regex WordRegex = new("[а-яА-ЯёЁ]+", RegexOptions.Compiled);
        private static readonly Regex SentenceEndRegex = new("[.!?…]+", RegexOptions.Compiled);

        public MainWindow(ILlmClientFactory llmClientFactory, IOptionsMonitor<AppSettings> optionsMonitor)
        {
            _llmClientFactory = llmClientFactory;
            _settings = optionsMonitor.CurrentValue;
            InitializeComponent();
            DataContext = _settings;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Set PasswordBox password from settings
            LlmApiKey.Password = _settings.Llm.ApiKey;

            // Subscribe to password change
            LlmApiKey.PasswordChanged += (s, args) =>
            {
                if (_settings.Llm.ApiKey != LlmApiKey.Password)
                {
                    _settings.Llm.ApiKey = LlmApiKey.Password;
                }
            };

            _settings.Homograph.PropertyChanged += HomographSettingsChanged;
            _cleanRulesComboBox = FindName("CleanRulesComboBox") as ComboBox;
            if (_cleanRulesComboBox != null)
            {
                _cleanRulesComboBox.ItemsSource = _cleanRules;
            }

            LoadCleanRules();

            _settings.EnableAutoSave();
        }

        // ── File menu ────────────────────────────────────────────────────────

        private void Open_Click(object sender, ExecutedRoutedEventArgs e)
        {
            OpenFile(Encoding.UTF8);
        }

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
                Filter = "Текстовые файлы (*.txt;*.fb2)|*.txt;*.fb2|fb2 файлы (*.fb2)|*.fb2|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string text;
            text = File.ReadAllText(dlg.FileName, encoding);
            if (Path.GetExtension(dlg.FileName) == ".fb2")
            {
                Fb2.Document.Fb2Document fd = new Fb2.Document.Fb2Document();
                await fd.LoadAsync(text, new Fb2.Document.LoadingOptions.Fb2LoadingOptions() { });
                text = string.Join(Environment.NewLine, fd.Bodies);
            }
            
            OriginalText.Text = text;
            ResultText.Document = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(6)
            };

            _currentFilePath = dlg.FileName;
            Title = $"Озвучка книг — {Path.GetFileName(dlg.FileName)}";

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
            File.WriteAllText(_currentFilePath, GetResultPlainText(), Encoding.UTF8);
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Сохранить проект",
                Filter = "Проект (*.zip)|*.zip|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var state = new ProjectState
            {
                Threshold = _settings.Homograph.Threshold,
                CurrentLowConfidenceIndex = _lowConfidence.Count > 0 ? _currentHomographIndex : -1
            };

            using var fileStream = File.Create(dlg.FileName);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Create);

            WriteEntry(zip, "original.txt", OriginalText.Text);
            WriteEntry(zip, "result.txt", GetResultPlainText());
            WriteEntry(zip, "homographs.json", JsonSerializer.Serialize(_allHomographs, JsonOptions));
            WriteEntry(zip, "state.json", JsonSerializer.Serialize(state, JsonOptions));
            WriteEntry(zip, "result.rtf", GetResultRtfBytes());
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Открыть проект",
                Filter = "Проект (*.zip)|*.zip|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            using var zip = ZipFile.OpenRead(dlg.FileName);
            var original = ReadEntryText(zip, "original.txt") ?? string.Empty;
            var resultText = ReadEntryText(zip, "result.txt") ?? string.Empty;
            var homographsJson = ReadEntryText(zip, "homographs.json");
            var stateJson = ReadEntryText(zip, "state.json");

            var homographs = string.IsNullOrWhiteSpace(homographsJson)
                ? new List<ResolvedHomograph>()
                : JsonSerializer.Deserialize<List<ResolvedHomograph>>(homographsJson, JsonOptions) ?? [];

            var state = string.IsNullOrWhiteSpace(stateJson)
                ? new ProjectState()
                : JsonSerializer.Deserialize<ProjectState>(stateJson, JsonOptions) ?? new ProjectState();

            OriginalText.Text = original;
            _allHomographs = homographs;
            ShowResult(resultText, homographs, state.Threshold > 0 ? state.Threshold : _settings.Homograph.Threshold);

            StatusHomographCount.Text = homographs.Count.ToString();
            UpdateBasicStatistics(original);
            MainTabs.SelectedIndex = 1;

            if (_lowConfidence.Count > 0 && state.CurrentLowConfidenceIndex >= 0)
            {
                _currentHomographIndex = Math.Clamp(state.CurrentLowConfidenceIndex, 0, _lowConfidence.Count - 1);
                NavigateToCurrentHomograph();
            }
        }

        private async Task<bool> EnsureFoundryModelReadyAsync(IFoundryLocalLlmClient foundryClient, CancellationToken ct)
        {
            StatusCurrentHomograph.Text = "Проверка модели Foundry Local…";
            var modelStatus = await foundryClient.GetModelStatusAsync(ct);
            if (modelStatus == null)
                throw new InvalidOperationException("Не удалось получить сведения о модели Foundry Local.");

            var modelName = string.IsNullOrWhiteSpace(modelStatus.Alias) ? modelStatus.Id : modelStatus.Alias;
            if (!modelStatus.IsCached)
            {
                var sizeText = modelStatus.SizeBytes.HasValue
                    ? FormatSize(modelStatus.SizeBytes.Value)
                    : "неизвестный размер";
                var message = modelStatus.SizeBytes.HasValue
                    ? $"Модель \"{modelName}\" еще не загружена и занимает около {sizeText}. Скачать сейчас?"
                    : $"Модель \"{modelName}\" еще не загружена. Скачать сейчас?";

                var result = MessageBox.Show(
                    message,
                    "Загрузка модели Foundry Local",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    StatusCurrentHomograph.Text = "Загрузка модели отменена. Выберите другую модель.";
                    return false;
                }
            }

            var statusPrefix = modelStatus.IsCached ? "Подготовка модели" : "Загрузка модели";
            StatusCurrentHomograph.Text = $"{statusPrefix}…";
            var progress = new Progress<float>(p =>
                StatusCurrentHomograph.Text = $"{statusPrefix}: {p:F0}%…");
            await foundryClient.PrepareAsync(progress, ct);
            return true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftAlt or Key.RightAlt) return;

            switch (key)
            {
                case Key.F:
                    FindNext_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.R:
                    ReplaceNext_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S:
                    NextHomograph_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.W:
                    PrevHomograph_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    ApplyVariant(1);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    ApplyVariant(2);
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    ApplyVariant(3);
                    e.Handled = true;
                    break;
                case Key.D4:
                case Key.NumPad4:
                    ApplyVariant(4);
                    e.Handled = true;
                    break;
            }
        }

        // ── Search & replace ───────────────────────────────────────────────

        private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            FindNext_Click(sender, e);
        }

        private void ReplaceTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ReplaceNext_Click(sender, e);
        }


        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            var pattern = FindTextBox.Text;
            if (string.IsNullOrEmpty(pattern)) return;

            if (!TryFindNextFromCursor(pattern, out var matchIndex))
            {
                MessageBox.Show("Совпадения не найдены.", "Поиск",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectInResultText(matchIndex, pattern.Length);
        }

        private void ReplaceNext_Click(object sender, RoutedEventArgs e)
        {
            var pattern = FindTextBox.Text;
            if (string.IsNullOrEmpty(pattern)) return;

            var replacement = ReplaceTextBox.Text;
            if (TryGetResultSelection(out var selectionStart, out var selectionLength, out var selectedText)
                && selectionLength > 0
                && selectedText == pattern)
            {
                ReplaceSelection(selectionStart, selectionLength, replacement, true);
                SelectInResultText(selectionStart, replacement.Length);
                return;
            }

            if (!TryFindNextFromCursor(pattern, out var matchIndex))
            {
                MessageBox.Show("Совпадения не найдены.", "Замена",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReplaceSelection(matchIndex, pattern.Length, replacement, true);
            SelectInResultText(matchIndex, replacement.Length);
        }

        private bool TryFindNextFromCursor(string pattern, out int matchIndex)
        {
            var text = GetResultSearchText();
            matchIndex = -1;
            if (string.IsNullOrEmpty(text)) return false;

            int startIndex = GetResultCursorIndex();
            int index = text.IndexOf(pattern, startIndex, StringComparison.CurrentCulture);
            if (index < 0) return false;

            matchIndex = index;
            return true;
        }

        private (string Text, bool IsResult) GetActiveText()
        {
            bool isResult = MainTabs.SelectedIndex == 1;
            string text = isResult ? GetResultPlainText() : OriginalText.Text;
            return (text, isResult);
        }

        private string GetResultSearchText()
        {
            var range = new TextRange(ResultText.Document.ContentStart, ResultText.Document.ContentEnd);
            return range.Text;
        }

        private int GetResultCursorIndex()
        {
            var range = new TextRange(ResultText.Document.ContentStart, ResultText.CaretPosition);
            return range.Text.Length;
        }

        private void SelectInResultText(int index, int length)
        {
            var start = GetTextPointerAtOffset(ResultText.Document.ContentStart, index);
            var end = GetTextPointerAtOffset(ResultText.Document.ContentStart, index + length);
            ResultText.Selection.Select(start, end);
            ResultText.Focus();
            ResultText.Selection.Start.Paragraph?.BringIntoView();
        }

        private bool TryGetResultSelection(out int start, out int length, out string selectedText)
        {
            var selection = ResultText.Selection;
            selectedText = selection.Text;
            start = new TextRange(ResultText.Document.ContentStart, selection.Start).Text.Length;
            length = selection.Text.Length;
            return true;
        }

        private void SetActiveText(string text, bool isResult)
        {
            if (isResult)
            {
                SetResultPlainText(text);
                MarkResultManualEdit();
            }
            else
            {
                OriginalText.Text = text;
                UpdateBasicStatistics(text);
            }
        }


        private void ReplaceSelection(int start, int length, string replacement, bool isResult)
        {
            if (!isResult)
            {
                var text = OriginalText.Text;
                var updated = text.Remove(start, length).Insert(start, replacement);
                OriginalText.Text = updated;
                OriginalText.Select(start, replacement.Length);
                UpdateBasicStatistics(updated);
                return;
            }

            var rangeStart = GetTextPointerAtOffset(ResultText.Document.ContentStart, start);
            var rangeEnd = GetTextPointerAtOffset(ResultText.Document.ContentStart, start + length);
            ResultText.Selection.Select(rangeStart, rangeEnd);
            ResultText.Selection.Text = replacement;
            MarkResultManualEdit();
        }

        private static TextPointer GetTextPointerAtOffset(TextPointer start, int offset)
        {
            int count = 0;
            var navigator = start;
            while (navigator != null)
            {
                var context = navigator.GetPointerContext(LogicalDirection.Forward);
                if (context == TextPointerContext.Text)
                {
                    var textRun = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (count + textRun.Length >= offset)
                        return navigator.GetPositionAtOffset(offset - count) ?? navigator;

                    count += textRun.Length;
                    navigator = navigator.GetPositionAtOffset(textRun.Length);
                }
                else if (context == TextPointerContext.ElementEnd && navigator.Parent is Paragraph)
                {
                    int newlineLength = Environment.NewLine.Length;
                    if (count + newlineLength >= offset)
                        return navigator.GetNextInsertionPosition(LogicalDirection.Forward) ?? navigator;

                    count += newlineLength;
                    navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
                }
                else
                {
                    navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
                }
            }

            return start;
        }

        private void SetResultPlainText(string text)
        {
            _suppressResultTextChanged = true;
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(6)
            };
            doc.Blocks.Add(new Paragraph(new Run(text)));
            ResultText.Document = doc;
            _suppressResultTextChanged = false;
        }

        private void MarkResultManualEdit()
        {
            ResetHomographState();
        }

        // ── Accent dictionaries ─────────────────────────────────────────────

        private void ApplyStress_Click(object sender, RoutedEventArgs e)
        {
            var (text, isResult) = GetActiveText();
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
                WordRegex.Matches(text).Select(m => m.Value.ToLowerInvariant()));

            var stressMap = new Dictionary<string, StressEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in _settings.Homograph.DicAPath)
            {
                var path = ResolvePath(rawPath);
                if (!File.Exists(path))
                    continue;

                foreach (var (word, entry) in LoadStressEntries(path, wordSet))
                    if (!stressMap.ContainsKey(word))
                        stressMap[word] = entry;
            }

            if (stressMap.Count == 0)
            {
                MessageBox.Show("Нет совпадений в словарях ударений.", "Внимание",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var updated = ApplyStressMarks(text, stressMap);
            SetActiveText(updated, isResult);
        }

        // ── Homograph resolution ─────────────────────────────────────────────

        private async void ResolveHomographs_Click(object sender, RoutedEventArgs e)
        {
            var text = OriginalText.Text;
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

            MainTabs.IsEnabled = false;
            _resolutionCts = new CancellationTokenSource();
            UpdateResolutionUI(true);
            StatusCurrentHomograph.Text = "Загрузка словаря…";

            try
            {
                var dictionary = await HomographDictionary.LoadAsync(dicPath);
                StatusCurrentHomograph.Text = $"Словарь: {dictionary.Count} слов. Разрешение…";

                var llmClient = _llmClientFactory.CreateClient();

                if (llmClient is IFoundryLocalLlmClient foundryClient)
                {
                    if (!await EnsureFoundryModelReadyAsync(foundryClient, _resolutionCts.Token))
                        return;
                }

                var progress = new Progress<(int Current, int Total)>(p =>
                    StatusCurrentHomograph.Text = $"Омографы: {p.Current}/{p.Total}…");

                var (resultText, homographs) = await Resolver.ResolveAsync(
                    text, dictionary, llmClient, progress, _resolutionCts.Token, OnLlmError);

                _allHomographs = homographs;
                ShowResult(resultText, homographs, _settings.Homograph.Threshold);

                StatusHomographCount.Text = homographs.Count.ToString();
                MainTabs.SelectedIndex = 1;

                StatusCurrentHomograph.Text = _lowConfidence.Count > 0
                    ? $"Низкая уверенность: {_lowConfidence.Count}. Используйте навигацию."
                    : "Все омографы разрешены.";
            }
            catch (OperationCanceledException)
            {
                StatusCurrentHomograph.Text = "Процесс прерван пользователем.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusCurrentHomograph.Text = "";
            }
            finally
            {
                MainTabs.IsEnabled = true;
                UpdateResolutionUI(false);
                _resolutionCts?.Dispose();
                _resolutionCts = null;
            }
        }

        private void ShowResult(string text, List<ResolvedHomograph> homographs, double threshold)
        {
            _suppressResultTextChanged = true;
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(6)
            };
            var para = new Paragraph();

            _lowConfidence.Clear();
            _currentHomographIndex = -1;
            _highlightedRun = null;

            int lastPos = 0;
            foreach (var h in homographs)
            {
                if (h.AbsolutePosition > lastPos)
                    para.Inlines.Add(new Run(text[lastPos..h.AbsolutePosition]));

                var end = Math.Min(h.AbsolutePosition + h.Length, text.Length);
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
                lastPos = end;
            }

            if (lastPos < text.Length)
                para.Inlines.Add(new Run(text[lastPos..]));

            doc.Blocks.Add(para);
            ResultText.Document = doc;
            _suppressResultTextChanged = false;

            bool hasLow = _lowConfidence.Count > 0;
            PrevHomographBtn.IsEnabled = hasLow;
            NextHomographBtn.IsEnabled = hasLow;

            if (hasLow)
            {
                _currentHomographIndex = _lowConfidence.Count - 1;
                NavigateToCurrentHomograph();
            }
            else
            {
                HomographIndexText.Text = "";
            }
        }

        // ── Result tab navigation ────────────────────────────────────────────

        private void PrevHomograph_Click(object sender, RoutedEventArgs e)
        {
            if (_lowConfidence.Count == 0) return;
            _currentHomographIndex--;
            if (_currentHomographIndex < 0)
                _currentHomographIndex = _lowConfidence.Count - 1;
            NavigateToCurrentHomograph();
        }

        private void NextHomograph_Click(object sender, RoutedEventArgs e)
        {
            if (_lowConfidence.Count == 0) return;
            _currentHomographIndex++;
            if (_currentHomographIndex >= _lowConfidence.Count)
                _currentHomographIndex = 0;
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
            //if (!h.Variants.Any(v => v.Target == HomographIndexText.Text))
            //{
            //    Text = "Омографы были модифицированы вручную, навигация может сбоить" 
            //}
            var variants = string.Join(" | ", h.Variants.Select(v => v.Target));
            var reasoning = string.IsNullOrWhiteSpace(h.Reasoning) ? "" : $" ({h.Reasoning})";
            StatusCurrentHomograph.Text = $"[{h.Confidence:P0}] {h.OriginalWord}: {variants}{reasoning}";
        }

        // ── Font size: Ctrl + Mouse Wheel ────────────────────────────────────

        private void Text_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var control = (Control)sender;
            control.FontSize = Math.Clamp(control.FontSize + (e.Delta > 0 ? 1 : -1), 8, 48);
            e.Handled = true;
        }

        private void ResultText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressResultTextChanged) return;

            var text = GetResultPlainText();
            if (string.IsNullOrWhiteSpace(text))
                ResetHomographState();
        }

        // ── Clean rules ───────────────────────────────────────────────────

        private void HomographSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomographConfig.CleanRegexPath))
                LoadCleanRules();
        }

        private void LoadCleanRules()
        {
            foreach (var rule in _cleanRules)
                rule.PropertyChanged -= CleanRuleChanged;

            _cleanRules.Clear();

            var path = ResolvePath(_settings.Homograph.CleanRegexPath);
            if (!File.Exists(path))
                return;

            string? pendingDescription = null;
            foreach (var rawLine in File.ReadLines(path))
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.StartsWith('#'))
                {
                    pendingDescription = trimmed.TrimStart('#').Trim();
                    continue;
                }

                int separatorIndex = rawLine.IndexOf('=');
                if (separatorIndex < 0)
                    continue;

                var pattern = rawLine[..separatorIndex];
                var replacement = rawLine[(separatorIndex + 1)..];
                var description = string.IsNullOrWhiteSpace(pendingDescription)
                    ? pattern
                    : pendingDescription;

                var rule = new CleanRule(description, pattern, replacement);
                rule.PropertyChanged += CleanRuleChanged;
                _cleanRules.Add(rule);
                pendingDescription = null;
            }
        }

        private void CleanRuleChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CleanRule.IsSelected) && _cleanRulesComboBox != null)
                _cleanRulesComboBox.Text = "Правила очистки";
        }

        private async void ApplyCleanRules_Click(object sender, RoutedEventArgs e)
        {
            var selectedRules = _cleanRules.Where(r => r.IsSelected).ToList();
            if (selectedRules.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одно правило очистки.", "Очистка текста",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var text = GetResultPlainText();
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("Нет текста в результате для очистки.", "Очистка текста",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                foreach (var rule in selectedRules)
                    text = Regex.Replace(text, rule.Pattern, rule.Replacement, RegexOptions.Multiline);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка применения правил очистки:\n{ex.Message}", "Очистка текста",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetResultText(text);
            ResetHomographState();
            await UpdateStatisticsAsync(text);
        }

        // ── Browse dialogs ───────────────────────────────────────────────────

        private void BrowseVoice_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать референсный аудиофайл",
                Filter = "Аудиофайлы (*.wav;*.mp3;*.ogg)|*.wav;*.mp3;*.ogg|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsVoicePath.Text = dlg.FileName;
        }

        private void BrowseF5TtsCli_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать F5-TTS CLI",
                Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsCliPath.Text = dlg.FileName;
        }

        private void BrowseModelCfg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать конфиг модели",
                Filter = "YAML файлы (*.yaml;*.yml)|*.yaml;*.yml|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsModelCfg.Text = dlg.FileName;
        }

        private void BrowseCkptFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать файл весов",
                Filter = "Safetensors (*.safetensors)|*.safetensors|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsCkptFile.Text = dlg.FileName;
        }

        private void BrowseVocabFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать файл словаря",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsVocabFile.Text = dlg.FileName;
        }

        private void BrowseDic_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать словарь омографов", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) DicPath.Text = dlg.FileName;
        }

        private void BrowseDicA_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать словарь ударений", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) DicAPath.Text += (Environment.NewLine + dlg.FileName);
        }

        private void BrowseCleanRegex_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать файл правил очистки", Filter = "Rex файлы (*.rex)|*.rex|Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true ) CleanRulesComboBox.Text = dlg.FileName;
        }

        // ── Statistics ──────────────────────────────────────────────────────

        private void UpdateBasicStatistics(string text)
        {
            StatusWords.Text = WordRegex.Matches(text).Count.ToString();
            StatusSentences.Text = CountSentences(text).ToString();
        }

        private async Task UpdateStatisticsAsync(string text)
        {
            UpdateBasicStatistics(text);

            try
            {
                var dicPath = ResolvePath(_settings.Homograph.DictionaryPath);
                if (!File.Exists(dicPath))
                {
                    StatusHomographCount.Text = "0";
                    return;
                }

                var dictionary = await HomographDictionary.LoadAsync(dicPath);
                int count = 0;
                foreach (Match match in WordRegex.Matches(text))
                {
                    if (dictionary.TryGetVariants(match.Value.ToLowerInvariant(), out _))
                        count++;
                }

                StatusHomographCount.Text = count.ToString();
            }
            catch
            {
                StatusHomographCount.Text = "0";
            }
        }

        private static int CountSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            int count = 0;
            foreach (Match m in SentenceEndRegex.Matches(text))
            {
                int end = m.Index + m.Length;
                if (end >= text.Length || char.IsWhiteSpace(text[end]))
                    count++;
            }

            return count == 0 ? 1 : count;
        }

        private readonly record struct StressEntry(int StressPos, int? StressPos2);

        private static IEnumerable<KeyValuePair<string, StressEntry>> LoadStressEntries(
            string path, HashSet<string> words)
        {
            var results = new List<KeyValuePair<string, StressEntry>>();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var state = new JsonReaderState();
            int bytesInBuffer = 0;
            bool isFinalBlock = false;

            try
            {
                using var stream = File.OpenRead(path);
                while (!isFinalBlock)
                {
                    int bytesRead = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                    if (bytesRead == 0)
                        isFinalBlock = true;

                    bytesInBuffer += bytesRead;
                    var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, bytesInBuffer), isFinalBlock, state);

                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            continue;

                        var word = reader.GetString();
                        if (!reader.Read())
                            break;

                        if (word != null && words.Contains(word))
                        {
                            if (!TryReadStressEntry(ref reader, out var entry))
                                break;
                            if (entry.StressPos > 0)
                                results.Add(new KeyValuePair<string, StressEntry>(word, entry));
                        }
                        else
                        {
                            if (!reader.TrySkip())
                                break;
                        }
                    }

                    state = reader.CurrentState;
                    int consumed = (int)reader.BytesConsumed;
                    bytesInBuffer -= consumed;
                    if (bytesInBuffer > 0)
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, bytesInBuffer);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return results;
        }

        private static bool TryReadStressEntry(ref Utf8JsonReader reader, out StressEntry entry)
        {
            entry = default;
            if (reader.TokenType != JsonTokenType.StartObject)
                return true;

            int stressPos = 0;
            int? stressPos2 = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (stressPos > 0)
                        entry = new StressEntry(stressPos, stressPos2);
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                var property = reader.GetString();
                if (!reader.Read())
                    return false;

                if (property == "stress_pos" && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var stressValue))
                    stressPos = stressValue;
                else if (property == "stress_pos2" && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var stressValue2))
                    stressPos2 = stressValue2;
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                        return false;
                }
            }

            return false;
        }

        private static string ApplyStressMarks(string text, Dictionary<string, StressEntry> stressMap)
        {
            var matches = WordRegex.Matches(text);
            if (matches.Count == 0) return text;

            var sb = new StringBuilder(text);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                if (match.Value.Contains('+'))
                    continue;

                var key = match.Value.ToLowerInvariant();
                if (!stressMap.TryGetValue(key, out var entry))
                    continue;

                int pos = entry.StressPos;
                if (pos <= 0 || pos > match.Value.Length)
                    continue;

                sb.Insert(match.Index + pos - 1, "+");
            }

            return sb.ToString();
        }


        // ── Helpers ──────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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
            if (delta != 0)
                UpdateOffsets(homograph.AbsolutePosition, delta);

            if (homograph.Confidence * 100 >= HomographThreshold.Value)
            {
                run.Background = Brushes.LightGreen;
                _lowConfidence.RemoveAt(_currentHomographIndex);
                _highlightedRun = null;

                if (_lowConfidence.Count == 0)
                {
                    PrevHomographBtn.IsEnabled = false;
                    NextHomographBtn.IsEnabled = false;
                    HomographIndexText.Text = "";
                    StatusCurrentHomograph.Text = "";
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

        private string GetResultPlainText()
        {
            var range = new TextRange(ResultText.Document.ContentStart, ResultText.Document.ContentEnd);
            return range.Text.TrimEnd('\r', '\n');
        }

        private void SetResultText(string text)
        {
            _suppressResultTextChanged = true;
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(6)
            };
            doc.Blocks.Add(new Paragraph(new Run(text)));
            ResultText.Document = doc;
            _suppressResultTextChanged = false;
        }

        private byte[] GetResultRtfBytes()
        {
            var range = new TextRange(ResultText.Document.ContentStart, ResultText.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.Rtf);
            return stream.ToArray();
        }

        private static void WriteEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        private static void WriteEntry(ZipArchive zip, string entryName, byte[] content)
        {
            var entry = zip.CreateEntry(entryName);
            using var entryStream = entry.Open();
            entryStream.Write(content, 0, content.Length);
        }

        private static string? ReadEntryText(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(AppContext.BaseDirectory, path);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "0 Б";

            string[] suffixes = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
            double size = bytes;
            int order = 0;
            while (size >= 1024 && order < suffixes.Length - 1)
            {
                size /= 1024;
                order++;
            }

            return string.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", size, suffixes[order]);
        }

        private void ResetHomographState()
        {
            _allHomographs = [];
            _lowConfidence.Clear();
            _currentHomographIndex = -1;
            _highlightedRun = null;
            PrevHomographBtn.IsEnabled = false;
            NextHomographBtn.IsEnabled = false;
            HomographIndexText.Text = "";
            StatusCurrentHomograph.Text = "";
            StatusHomographCount.Text = "0";
        }

        private void UpdateResolutionUI(bool isProcessing)
        {
            if (CancelResolutionBtn != null)
                CancelResolutionBtn.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CancelResolutionBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Остановиться и прервать процесс?",
                "Подтверждение отмены",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _resolutionCts?.Cancel();
                UpdateResolutionUI(false);
            }
        }

        private async void LlmFoundryModel_DropDownOpened(object sender, EventArgs e)
        {
            if (LlmFoundryModel.Items.Count > 0)
                return;

            try
            {
                var models = await GetFoundryModelsAsync();
                if (models.Count == 0)
                {
                    LlmFoundryModel.Items.Add("FoundryLocal недоступен");
                }
                else
                {
                    foreach (var model in models)
                        LlmFoundryModel.Items.Add(model);

                    if (!string.IsNullOrEmpty(_settings.Llm.FoundryModel))
                        LlmFoundryModel.SelectedItem = _settings.Llm.FoundryModel;
                    else if (LlmFoundryModel.Items.Count > 0)
                        LlmFoundryModel.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                LlmFoundryModel.Items.Add($"Ошибка: {ex.Message}");
            }
        }

        private async Task<List<string>> GetFoundryModelsAsync()
        {
            try
            {
                if (!Microsoft.AI.Foundry.Local.FoundryLocalManager.IsInitialized)
                {
                    await Microsoft.AI.Foundry.Local.FoundryLocalManager.CreateAsync(
                        new Microsoft.AI.Foundry.Local.Configuration { AppName = "uga-chacka" },
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                }

                var catalog = await Microsoft.AI.Foundry.Local.FoundryLocalManager.Instance.GetCatalogAsync();
                var models = await catalog.ListModelsAsync();
                return models.Select(m => m.Id).ToList();
            }
            catch
            {
                return [];
            }
        }

        private async Task<bool> OnLlmError(int current, int total)
        {
            IsEnabled = true;
            try
            {
                var result = MessageBox.Show(
                    $"Ошибка ответа от LLM ({current}/{total}).\n\nЕсли продолжить, будет выбран случайный вариант омографа.\n\nПрервать?",
                    "Ошибка LLM",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                return result == MessageBoxResult.No;
            }
            finally
            {
                IsEnabled = false;
            }
        }

        private class ProjectState
        {
            public double Threshold { get; set; }
            public int CurrentLowConfidenceIndex { get; set; } = -1;
        }

        private sealed class CleanRule : INotifyPropertyChanged
        {
            private bool _isSelected;

            public CleanRule(string description, string pattern, string replacement)
            {
                Description = description;
                Pattern = pattern;
                Replacement = replacement;
            }

            public string Description { get; }
            public string Pattern { get; }
            public string Replacement { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        // ── TTS voiceover ────────────────────────────────────────────────────

        private async void Voiceover_Click(object sender, RoutedEventArgs e)
        {
            var genText = GetResultPlainText();
            if (string.IsNullOrWhiteSpace(genText))
                genText = OriginalText.Text;

            if (string.IsNullOrWhiteSpace(genText))
            {
                MessageBox.Show("Нет текста для озвучки.", "Внимание",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cliPath = _settings.Tts.F5TtsCliPath;
            if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
            {
                MessageBox.Show(
                    "Не указан или не найден путь к F5-TTS CLI.\nНастройте в разделе Настройки → TTS.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var refAudio = _settings.Tts.VoicePath;
            if (string.IsNullOrWhiteSpace(refAudio) || !File.Exists(refAudio))
            {
                MessageBox.Show(
                    "Не указан или не найден референсный аудиофайл.\nНастройте в разделе Настройки → TTS.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var saveDlg = new SaveFileDialog
            {
                Title = "Сохранить озвучку",
                Filter = "WAV файлы (*.wav)|*.wav|Все файлы (*.*)|*.*",
                FileName = "output.wav"
            };
            if (saveDlg.ShowDialog() != true) return;

            var outputDir = Path.GetDirectoryName(saveDlg.FileName)!;
            var outputFile = Path.GetFileName(saveDlg.FileName);

            var tomlPath = Path.Combine(Path.GetTempPath(), $"f5tts_{Guid.NewGuid():N}.toml");
            try
            {
                var toml = new StringBuilder();
                toml.AppendLine($"ref_audio = \"{EscapeTomlString(refAudio)}\"");
                toml.AppendLine($"ref_text = \"{EscapeTomlString(_settings.Tts.RefText)}\"");
                toml.AppendLine("gen_text = '''");
                toml.AppendLine(genText);
                toml.AppendLine("'''");
                toml.AppendLine("remove_silence = false");
                File.WriteAllText(tomlPath, toml.ToString(), Encoding.UTF8);

                var args = new StringBuilder();
                args.Append($"-c \"{tomlPath}\"");

                if (!string.IsNullOrWhiteSpace(_settings.Tts.ModelCfg))
                    args.Append($" --model_cfg \"{_settings.Tts.ModelCfg}\"");
                if (!string.IsNullOrWhiteSpace(_settings.Tts.CkptFile))
                    args.Append($" --ckpt_file \"{_settings.Tts.CkptFile}\"");
                if (!string.IsNullOrWhiteSpace(_settings.Tts.VocabFile))
                    args.Append($" --vocab_file \"{_settings.Tts.VocabFile}\"");
                if (!string.IsNullOrWhiteSpace(_settings.Tts.Device))
                    args.Append($" --device {_settings.Tts.Device}");

                args.Append($" --output_dir \"{outputDir}\"");
                args.Append($" --output_file \"{outputFile}\"");

                MainTabs.IsEnabled = false;
                StatusCurrentHomograph.Text = "Запуск озвучки F5-TTS…";

                var psi = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };
                var stderr = new StringBuilder();

                process.OutputDataReceived += (_, a) =>
                {
                    if (a.Data != null)
                        Dispatcher.BeginInvoke(() =>
                            StatusCurrentHomograph.Text = $"F5-TTS: {a.Data}");
                };
                process.ErrorDataReceived += (_, a) =>
                {
                    if (a.Data != null) stderr.AppendLine(a.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    StatusCurrentHomograph.Text = "Озвучка завершена.";
                    var result = MessageBox.Show(
                        $"Файл сохранён:\n{saveDlg.FileName}\n\nОткрыть файл?",
                        "Озвучка завершена",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = saveDlg.FileName,
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    var errorText = stderr.ToString();
                    MessageBox.Show(
                        $"F5-TTS завершился с ошибкой (код {process.ExitCode}):\n\n{errorText}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusCurrentHomograph.Text = "Ошибка озвучки.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска F5-TTS:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusCurrentHomograph.Text = "";
            }
            finally
            {
                MainTabs.IsEnabled = true;
                try { File.Delete(tomlPath); } catch { }
            }
        }

        private static string EscapeTomlString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
