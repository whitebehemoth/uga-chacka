# Yara — Решения, принятые при реализации

## Архитектура

### WhiteBehemoth.Resolver (class library)
- **TextAnalyzer** — статический класс: поиск омографов в тексте, подсчёт слов/предложений, разбиение на предложения. Контекст (предложение) формируется тут же и передаётся вместе с `HomographMatch`.
- **ResolutionService** — возвращает `IAsyncEnumerable<ResolvedHomograph>`, что позволяет UI обновляться после каждого омографа (progressive rendering).
- **AccentService** — вынесена логика загрузки словарей ударений (streaming JSON reader) и расстановки ударений. Раньше это было в MainWindow.xaml.cs.

### WhiteBehemoth.Yara (WPF)
- **Services/LlmSettingsProvider** — адаптер `IOptionsMonitor<LlmSettings>`, который транслирует новый формат `LlmConfig` (со списком OpenAI-конфигов) в `LlmSettings`, ожидаемый `OpenAiLlmClient` / `FoundryLocalLlmClient` из HomographResolver. Позволяет не менять библиотеку HomographResolver.
- **Models/** — `CleanRule`, `ProjectState`, `FoundryModelItem` — каждый класс в отдельном файле.

## UX-решения (не указанные явно)

| Что | Решение |
|---|---|
| Формат `SelectedProvider` | `"openai:N"` (индекс в массиве) или `"foundry:model-id"` |
| UI выбора провайдера | Два RadioButton (OpenAI / FoundryLocal) + ComboBox для конкретного конфига/модели |
| Редактирование OpenAI-конфигов | ListBox + детальная панель справа. Binding через `DataContext="{Binding SelectedItem}"`. PasswordBox — через code-behind. |
| Foundry Local — обновление списка | Кнопка «Обновить список моделей». Показывает ✓/✗ рядом с каждой моделью. |
| Плейсхолдер | Наложенный `TextBlock` с `IsHitTestVisible="False"`, скрывается при наличии текста. |
| Progressive resolution | Перед резолвом строится FlowDocument с «слотами» (Run для каждого омографа, серый фон). По мере резолва Run обновляется: текст → stressed word, фон → зелёный/жёлтый. `BringIntoView()` прокручивает к текущему. |
| Alt+` | Подтверждает текущий омограф (Confidence → 1.0) без смены слова. |
| Формат проекта (.zip) | Упрощён: `text.txt` (вместо original.txt + result.txt), `homographs.json`, `state.json`. RTF убран для простоты. |
| Shortcut для поиска | `Ctrl+F` переключает видимость тулбара поиска/замены (toggle). |
| Cancel resolution | Частичные результаты сохраняются. Неразрешённые слоты теряют серый фон. ReadOnly снимается. |
| Температура и SystemPrompt | Общие для всех провайдеров (не per-config). |
| API Key | Хранится в `appsettings.json` в каждом `OpenAiEndpoint`. Для безопасности можно использовать User Secrets (подключение сохранено). |
| Размер шрифта | Ctrl+Scroll для зума (как было). |
