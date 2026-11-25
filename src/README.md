#  Scripts Manager

Локальное WPF-приложение для запуска параметризованных скриптов (T-SQL, PL/pgSQL, PowerShell, Python) — замена
Red Gate SQL Scripts Manager

## Сборка, тесты, запуск

Нужен .NET SDK 10. Команды — из папки `src`.

```
dotnet build ScriptsManager.slnx
dotnet test ScriptsManager.slnx
dotnet run --project ScriptsManager.App
```

Тесты (xUnit) обходятся без серверов БД: git проверяется на временных репозиториях, PowerShell и Python — через
установленные `powershell.exe` и `python`, T-SQL — разбор пакетов и подготовка параметров.

Настройки — `%APPDATA%\ScriptsManager\settings.json`; папки со скриптами добавляются в окне «Настройки».

## Структура

| Путь | Что |
|---|---|
| `ScriptsManager.Core` | вся логика без UI |
| `  Manifests/` | модель манифеста (`ScriptManifest`, `Kinds`), загрузка и проверка (`ManifestLoader`), разбор и проверка значений параметров (`ParameterValues`), пресеты (`PresetFile`, `PresetChanges`) |
| `  Catalog/` | раскладка корня (`ScriptLayout`: `.manifests`, `.presets`, `.output`), каталог скриптов, копирование и удаление |
| `  Runs/` | runners (`TSqlRunner`, `PlPgSqlRunner`, `ProcessRunner` для PowerShell и Python), контракты запуска (`RunContracts`), таблицы результатов, экспорт (xlsx, CSV, разметка) |
| `  Connections/` | строки подключения, недавние подключения, интерфейс хранилища паролей |
| `  Authoring/` | окно правки: поиск параметров в тексте (`ScriptAnalyzer`), блок `--#region Params` в T-SQL, объявление параметров в PowerShell и Python (`ScriptParamsWriter`), запись манифеста |
| `  Git/` | вызовы `git.exe`: status, fetch/pull, публикация через временный индекс |
| `  Settings/` | `settings.json` |
| `ScriptsManager.App` | WPF, MVVM на CommunityToolkit.Mvvm |
| `  ViewModels/` | `MainViewModel` (дерево, запуски, оповещения), `ScriptDetailsViewModel` (карточка), формы параметров и правки, `RunViewModel` |
| `  Views/ParameterFormView.xaml` | DataTemplate на каждый вид поля (выбор — `FieldTemplateSelector`) |
| `  Infrastructure/` | `RunRegistry` (запуски сеанса и выбор runner), конвертеры ячеек, Credential Manager, уведомление Windows и мигание на панели задач, диалоги |
| `ScriptsManager.Tests` | тесты Core |

Пакеты: Microsoft.Data.SqlClient, Npgsql, YamlDotNet, AvalonEdit, CommunityToolkit.Mvvm.

## Как устроено

- **Скрипт** — файл `.sql`/`.ps1`/`.py` в корне скриптов; манифест и пресеты — в `<корень>/.manifests/` и
  `<корень>/.presets/` по тому же относительному пути с добавленным `.yaml`. Папки с точкой в дерево не попадают.
- **Запуск**: форма → `ParameterValues.Resolve` (типизированные значения) → `RunRequest` → `IScriptRunner`.
  Runner пишет лог и таблицы в `IRunOutput`; запуск — `RunViewModel`, все запуски сеанса — `RunRegistry`.
- **T-SQL** — `SqlCommand` с параметрами `@имя` по пакетам между `GO`; блок `--#region Params` при запуске
  заменяется пустыми строками.
- **PL/pgSQL** — параметры `@имя` плюс `set_config('param.<имя>')` для `DO`-блоков.
- **PowerShell и Python** — дочерний процесс; параметры — аргументами, подключения, `SCRIPT_PARAMS`, `RESULT_DIR`,
  `OUTPUT_DIR` — переменными окружения.
- **Пароли** — в Windows Credential Manager, в файлах их нет.

## Как добавить…

- **Тип параметра**: `ParameterType` и имя в YAML (`Kinds.cs`) → проверка атрибутов в `ManifestLoader` → разбор и
  проверка в `ParameterValues` → передача в каждом runner (`RunValues`, `TSqlRunner`, `PlPgSqlRunner`,
  `ProcessRunner`) → поиск в тексте (`ScriptAnalyzer`) и объявление (`ScriptParamsWriter`, `TSqlDeclarations`) →
  поле формы (`FieldKind`, шаблон в `ParameterFormView.xaml`, `FieldTemplateSelector`) и выбор типа в
  `ParameterEditViewModel` → тесты → таблицы типов в `docs/spec.md`. Пример — тип `range`.
- **Язык скриптов**: `RunnerKind` и расширение (`Kinds.cs`, `ScriptLayout`), класс `IScriptRunner` в `Runs/`,
  выбор в `RunRegistry`, поиск параметров в `ScriptAnalyzer`.

## Особенности и грабли

- WinForms подключён только ради `NotifyIcon`; `System.Windows.Forms` и `System.Drawing` убраны из глобальных
  using в csproj, чтобы не конфликтовать с WPF.
- `ContextMenu` с обработчиками `Click` нельзя класть в `Setter` стиля (InvalidCastException `connectionId` в
  XAML) — такое меню держим в ресурсах окна с `x:Shared="False"`.
- Разбор `param()` PowerShell идёт через AST в `powershell.exe`, поэтому окну правки PowerShell-скриптов нужен
  Windows PowerShell.
- Файлы `.ps1` — в UTF-8 с BOM, иначе Windows PowerShell 5.1 читает их в ANSI.
- Неотслеживаемые файлы в `.output/` в git-статусе приложения не показываются.
