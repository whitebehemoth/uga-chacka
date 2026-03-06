using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HomographResolver;
using Microsoft.Win32;
using Microsoft.Extensions.Configuration;

namespace uga_chacka
{
    public partial class MainWindow : Window
    {
        private string? _currentFilePath;
        private AppSettings _settings = new();
        private List<ResolvedHomograph> _allHomographs = [];
        private List<(ResolvedHomograph Homograph, Run Run)> _lowConfidence = [];
        private int _currentHomographIndex = -1;
        private Run? _highlightedRun;

        private static readonly string SettingsPath = Path.Combine(
            AppContext.BaseDirectory, "appsettings.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        // ── Settings persistence ─────────────────────────────────────────────

        private void LoadSettings()
        {
            _settings = App.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();

            LlmType.SelectedIndex = _settings.Llm.Type == "FoundryLocal" ? 1 : 0;
            LlmUrl.Text = _settings.Llm.Url;
            LlmModel.Text = _settings.Llm.Model;
            LlmApiKey.Password = _settings.Llm.ApiKey;
            LlmTemperature.Value = _settings.Llm.Temperature;
            LlmSystemPrompt.Text = _settings.Llm.SystemPrompt;

            TtsUrl.Text = _settings.Tts.Url;
            TtsVoicePath.Text = _settings.Tts.VoicePath;

            HomographThreshold.Value = _settings.Homograph.Threshold;
            DicPath.Text = _settings.Homograph.DictionaryPath;
            DicAPath.Text = _settings.Homograph.DicAPath;
            DicA2Path.Text = _settings.Homograph.DicA2Path;

            DefaultFontSize.Value = _settings.General.DefaultFontSize;
        }

        private void CollectSettings()
        {
            _settings.Llm.Type = LlmType.SelectedIndex == 1 ? "FoundryLocal" : "OpenAI";
            _settings.Llm.Url = LlmUrl.Text;
            _settings.Llm.Model = LlmModel.Text;
            _settings.Llm.ApiKey = LlmApiKey.Password;
            _settings.Llm.Temperature = LlmTemperature.Value;
            _settings.Llm.SystemPrompt = LlmSystemPrompt.Text;

            _settings.Tts.Type = "F5 TTS";
            _settings.Tts.Url = TtsUrl.Text;
            _settings.Tts.VoicePath = TtsVoicePath.Text;

            _settings.Homograph.Threshold = HomographThreshold.Value;
            _settings.Homograph.DictionaryPath = DicPath.Text;
            _settings.Homograph.DicAPath = DicAPath.Text;
            _settings.Homograph.DicA2Path = DicA2Path.Text;

            _settings.General.DefaultFontSize = DefaultFontSize.Value;
        }

        private void PersistSettings()
        {
            CollectSettings();
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(SettingsPath, json);
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PersistSettings();
                MessageBox.Show("Настройки сохранены.", "Настройки",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── File menu ────────────────────────────────────────────────────────

        private void Open_Click(object sender, ExecutedRoutedEventArgs e)
        {
            OpenFile(null);
        }

        private void OpenNonUnicode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string tag && int.TryParse(tag, out int cp))
                OpenFile(Encoding.GetEncoding(cp));
        }

        private void OpenFile(Encoding? forced)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Открыть текстовый файл",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string text;
            if (forced != null)
            {
                text = File.ReadAllText(dlg.FileName, forced);
            }
            else
            {
                text = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                if (!text.Contains('а'))
                {
                    text = File.ReadAllText(dlg.FileName, Encoding.GetEncoding(1251));
                    if (!text.Contains('а'))
                        text = File.ReadAllText(dlg.FileName, Encoding.GetEncoding(866));
                }
            }

            OriginalText.Text = text;
            _currentFilePath = dlg.FileName;
            Title = $"Озвучка книг — {Path.GetFileName(dlg.FileName)}";
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
            File.WriteAllText(_currentFilePath, OriginalText.Text, Encoding.UTF8);
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

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

            CollectSettings();

            var dicPath = ResolvePath(_settings.Homograph.DictionaryPath);
            if (!File.Exists(dicPath))
            {
                MessageBox.Show($"Словарь омографов не найден:\n{dicPath}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsEnabled = false;
            StatusCurrentHomograph.Text = "Загрузка словаря…";

            try
            {
                var dictionary = await HomographDictionary.LoadAsync(dicPath);
                StatusCurrentHomograph.Text = $"Словарь: {dictionary.Count} слов. Разрешение…";

                using var llmClient = new LlmClient(_settings.Llm);

                var progress = new Progress<(int Current, int Total)>(p =>
                    StatusCurrentHomograph.Text = $"Омографы: {p.Current}/{p.Total}…");

                var (resultText, homographs) = await Resolver.ResolveAsync(
                    text, dictionary, llmClient, progress);

                _allHomographs = homographs;
                ShowResult(resultText, homographs, _settings.Homograph.Threshold);

                StatusHomographCount.Text = homographs.Count.ToString();
                MainTabs.SelectedIndex = 1;

                StatusCurrentHomograph.Text = _lowConfidence.Count > 0
                    ? $"Низкая уверенность: {_lowConfidence.Count}. Используйте навигацию."
                    : "Все омографы разрешены.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusCurrentHomograph.Text = "";
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private void ShowResult(string text, List<ResolvedHomograph> homographs, double threshold)
        {
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

            var variants = string.Join(" | ", h.Variants.Select(v =>
                $"{v.Index}. {v.Target}" +
                (v.LemmatDef is { Count: > 0 } ? $" ({string.Join(", ", v.LemmatDef)})" : "")));
            StatusCurrentHomograph.Text = $"[{h.Confidence:P0}] {h.OriginalWord}: {variants}";
        }

        // ── Font size: Ctrl + Mouse Wheel ────────────────────────────────────

        private void Text_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var control = (Control)sender;
            control.FontSize = Math.Clamp(control.FontSize + (e.Delta > 0 ? 1 : -1), 8, 48);
            e.Handled = true;
        }

        // ── Browse dialogs ───────────────────────────────────────────────────

        private void BrowseVoice_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбрать референсный аудиофайл",
                Filter = "Аудиофайлы (*.wav;*.mp3)|*.wav;*.mp3|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TtsVoicePath.Text = dlg.FileName;
        }

        private void BrowseDic_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать словарь омографов", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) DicPath.Text = dlg.FileName;
        }

        private void BrowseDicA_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать словарь ударений 1", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) DicAPath.Text = dlg.FileName;
        }

        private void BrowseDicA2_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Выбрать словарь ударений 2", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) DicA2Path.Text = dlg.FileName;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(AppContext.BaseDirectory, path);
        }
    }
}
