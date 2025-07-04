# Rust-Build

Инструмент для анализа C# проектов и решений с использованием Roslyn анализаторов и системы приоритетов.

## Описание

Rust-Build - это консольное приложение, которое анализирует C# проекты и решения (.sln, .csproj), используя:
- Microsoft Build (MSBuild) для загрузки проектов
- Roslyn анализаторы для статического анализа кода
- Систему приоритетов для группировки и отображения ошибок

## Возможности

- Автоматическое обнаружение файлов решений и проектов
- Анализ всех проектов в решении
- Поддержка пользовательских анализаторов
- Система приоритетов для группировки ошибок
- Детальный вывод с контекстом кода
- Цветной вывод в консоль

## Требования

- .NET 8.0 SDK
- Установленный MSBuild (обычно поставляется с .NET SDK)

## Установка и запуск

1. Клонируйте репозиторий:
```bash
git clone <repository-url>
cd rust-build
```

2. Восстановите зависимости:
```bash
dotnet restore
```

3. Соберите проект:
```bash
dotnet build
```

4. Запустите анализ:
```bash
# Анализ текущей директории
dotnet run

# Анализ конкретного файла или директории
dotnet run /path/to/project.csproj
dotnet run /path/to/solution.sln
dotnet run /path/to/directory
```

## Конфигурация

Файл `linter_config.json` содержит настройки приоритетов для различных типов ошибок:

```json
{
  "PriorityLevels": [
    {
      "Level": 1,
      "Name": "Critical Compiler Errors",
      "Rules": ["SMB002"]
    },
    {
      "Level": 2,
      "Name": "Unused Code and Hooks",
      "Rules": ["RUST003", "RUST007"]
    }
  ]
}
```

### Структура конфигурации

- `Level`: Числовой приоритет (меньше = выше приоритет)
- `Name`: Описательное название уровня
- `Rules`: Массив идентификаторов правил анализаторов

## Использование

### Базовое использование

```bash
# Анализ текущей директории
dotnet run

# Анализ конкретного проекта
dotnet run ./MyProject.csproj

# Анализ решения
dotnet run ./MySolution.sln
```

### Выходные данные

Программа выводит:
- Информацию о загруженных проектах
- Ошибки и предупреждения с приоритетами
- Контекст кода для каждой ошибки
- Итоговую сводку

### Пример вывода

```
Loading: /path/to/project.csproj
Project loaded successfully: MyProject
----------------------------------------------------

Analyzing project: MyProject

Displaying issues for Level 1: Critical Compiler Errors

❌ /path/to/file.cs(15,10): error SMB002: Some error message
     >    13 | public class MyClass
     >    14 | {
     >    15 |     private string _field;
     >    16 | }
     >    17 |
           |          ^

Total: 1 errors, 0 warnings.

----------------------------------------------------
Analysis finished. Errors or warnings found.
```

## Зависимости

- `Microsoft.Build.Locator` (1.7.8) - для регистрации MSBuild
- `Microsoft.CodeAnalysis.Workspaces.MSBuild` (4.10.0) - для работы с MSBuild workspace
- `Newtonsoft.Json` (13.0.3) - для парсинга конфигурации

## Лицензия

[Укажите лицензию проекта] 