// Требуемые NuGet пакеты:
// <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
// <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.10.0" />

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;

public class LinterConfig
{
    public List<PriorityLevel> PriorityLevels { get; set; } = new List<PriorityLevel>();
    public ProblematicPluginThresholds ProblematicPluginThresholds { get; set; } = new ProblematicPluginThresholds();
}

public class ProblematicPluginThresholds
{
    public int MaxFiles { get; set; } = 50;
    public long MaxTotalSizeMB { get; set; } = 5;
    public int MaxTotalLines { get; set; } = 50000;
    public int MaxLargeFiles { get; set; } = 10;
    public int MaxVeryLargeFiles { get; set; } = 0;
    public int MaxErrorProneFiles { get; set; } = 5;
    public int LargeFileSizeKB { get; set; } = 100;
    public int VeryLargeFileSizeKB { get; set; } = 500;
    public int ErrorProneFileLines { get; set; } = 1000;
}

public class PriorityLevel
{
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Rules { get; set; } = new List<string>();
}

public class FormatStats
{
    public int Attempts { get; set; }
    public int TotalIssuesFound { get; set; }
    public int IssuesFixed { get; set; }
    public int UnFixableIssues { get; set; }
}

public class ConcurrentStats
{
    private readonly object _lock = new object();
    private int _totalAttempts;
    private int _totalIssuesFound;
    private int _totalIssuesFixed;

    public int TotalAttempts => _totalAttempts;
    public int TotalIssuesFound => _totalIssuesFound;
    public int TotalIssuesFixed => _totalIssuesFixed;

    public void Add(FormatStats stats)
    {
        lock (_lock)
        {
            _totalAttempts += stats.Attempts;
            _totalIssuesFound += stats.TotalIssuesFound;
            _totalIssuesFixed += stats.IssuesFixed;
        }
    }
}

public class Program
{
    private static LinterConfig _config = new LinterConfig();
    private static readonly Dictionary<string, int> _pluginErrorCount = new();
    private static string _pluginsRoot = string.Empty;
    private static bool _hasErrors = false;
    
    static async Task Main(string[] args)
    {
        // Обработка аргументов: путь до .csproj и имя плагина
        string? projectPath = null;
        string? pluginName = null;
        
        // Обработка аргумента --project
        int projectArgIndex = Array.IndexOf(args, "--project");
        if (projectArgIndex >= 0 && projectArgIndex + 1 < args.Length)
        {
            projectPath = args[projectArgIndex + 1];
            
            // Создаем новый массив аргументов без --project и его значения
            var newArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (i != projectArgIndex && i != projectArgIndex + 1)
                {
                    newArgs.Add(args[i]);
                }
            }
            args = newArgs.ToArray();
        }
        
        if (args.Length > 0)
        {
            // Если первый аргумент заканчивается на .csproj или .sln, это путь к проекту
            if (args[0].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || 
                args[0].EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(args[0]))
            {
                projectPath = args[0];
                
                // Если есть второй аргумент, это имя плагина
                if (args.Length > 1)
                {
                    pluginName = args[1];
                }
            }
            // Иначе первый аргумент - это имя плагина
            else
            {
                pluginName = args[0];
                // Проект ищем в текущей директории, если он не был указан через --project
                if (projectPath == null)
                {
                    var csprojFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
                    if (csprojFiles.Length > 0)
                    {
                        projectPath = csprojFiles[0];
                        if (csprojFiles.Length > 1)
                        {
                            Console.WriteLine($"Warning: Multiple .csproj files found in current directory. Using: {Path.GetFileName(projectPath)}");
                        }
                    }
                }
            }
        }
        
        // Если не указан путь к проекту, используем текущую директорию
        if (projectPath == null)
        {
            projectPath = Directory.GetCurrentDirectory();
        }

        // Выводим информацию о параметрах запуска
        Console.WriteLine($"Project path: {projectPath}");
        if (!string.IsNullOrEmpty(pluginName))
        {
            Console.WriteLine($"Building plugin: {pluginName}");
        }

        // --- ДОБАВЛЕНО: запуск форматирования ДО анализа ---
        string? pluginsDirForFormat = null;
        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var csprojDir = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(csprojDir))
            {
                var pluginsDir = Path.Combine(csprojDir, "plugins");
                if (Directory.Exists(pluginsDir))
                    pluginsDirForFormat = pluginsDir;
            }
        }
        else if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solutionDir = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(solutionDir))
            {
                var pluginsDir = Path.Combine(solutionDir, "plugins");
                if (Directory.Exists(pluginsDir))
                    pluginsDirForFormat = pluginsDir;
            }
        }
        else if (Directory.Exists(projectPath))
        {
            var pluginsDir = Path.Combine(projectPath, "plugins");
            if (Directory.Exists(pluginsDir))
                pluginsDirForFormat = pluginsDir;
        }
        if (pluginsDirForFormat != null)
        {
            RunDotnetFormat(pluginsDirForFormat, pluginName);
        }
        // --- КОНЕЦ ДОБАВЛЕНИЯ ---

        try
        {
            // Определяем путь к конфигу: сначала в директории проекта, затем fallback
            string? configPath = null;
            
            // 1. Если указан путь к проекту/решению, ищем конфиг рядом с ним
            if (projectPath != null)
            {
                string? dir = Directory.Exists(projectPath)
                    ? projectPath
                    : Path.GetDirectoryName(projectPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, "linter.config.json");
                    if (File.Exists(candidate))
                        configPath = candidate;
                }
            }
            
            // 2. Если конфиг не найден и есть аргументы, ищем в директории первого аргумента
            if (configPath == null && args.Length > 0)
            {
                var inputPath = args[0];
                string? dir = Directory.Exists(inputPath)
                    ? inputPath
                    : Path.GetDirectoryName(inputPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, "linter.config.json");
                    if (File.Exists(candidate))
                        configPath = candidate;
                }
            }
            
            // 3. Если конфиг не найден, ищем в директории самого проекта (AppContext.BaseDirectory)
            if (configPath == null)
            {
                var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, "linter.config.json");
                if (File.Exists(baseDirCandidate))
                    configPath = baseDirCandidate;
            }
            
            // 4. Последний fallback: текущая рабочая директория
            if (configPath == null)
            {
                var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "linter.config.json");
                if (File.Exists(cwdCandidate))
                    configPath = cwdCandidate;
            }
            
            if (configPath != null)
            {
                var configJson = await File.ReadAllTextAsync(configPath);
                _config = JsonConvert.DeserializeObject<LinterConfig>(configJson) ?? new LinterConfig();
                Console.WriteLine($"Loaded linter config: {configPath}");
            }
            else
            {
                Console.WriteLine($"Warning: linter.config.json not found. Using default settings.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load or parse linter.config.json. Using default settings. Error: {ex.Message}");
        }

        string path;
        if (projectPath == Directory.GetCurrentDirectory() && !Directory.Exists(Path.Combine(projectPath, "plugins")))
        {
            path = projectPath;
            Console.WriteLine($"Using directory: {Path.GetFullPath(path)}");
        }
        else
        {
            path = projectPath ?? Directory.GetCurrentDirectory();
        }

        // If the provided path is a directory, find a solution or project file within it.
        if (Directory.Exists(path))
        {
            // Prefer solution files
            var solutionFiles = Directory.GetFiles(path, "*.sln");
            if (solutionFiles.Length > 0)
            {
                if (solutionFiles.Length > 1)
                {
                    Console.WriteLine($"Warning: Multiple solution files found. Using the first one: {Path.GetFileName(solutionFiles[0])}");
                }
                path = solutionFiles[0];
            }
            else
            {
                // Fallback: ищем только проекты в plugins (рекурсивно)
                var pluginsDir = Path.Combine(path, "plugins");
                _pluginsRoot = pluginsDir;
                if (!Directory.Exists(pluginsDir))
                {
                    Console.WriteLine($"Error: 'plugins' directory not found in {Path.GetFullPath(path)}");
                    return;
                }
                var pluginProjects = Directory.GetFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories);
                if (pluginProjects.Length == 0)
                {
                    Console.WriteLine($"Error: No .csproj files found in 'plugins' directory.");
                    return;
                }
                if (pluginProjects.Length > 1)
                {
                    Console.WriteLine($"Warning: Multiple plugin projects found. Will analyze all:");
                    foreach (var proj in pluginProjects)
                        Console.WriteLine($"  - {proj}");
                }
                foreach (var proj in pluginProjects)
                {
                    await AnalyzeProject(MSBuildWorkspace.Create(), proj, pluginName);
                }
                PrintFinalReport(true);
                
                // Автоматический мердж partial-классов после анализа
                var buildDir = Path.Combine(path, "build");
                Directory.CreateDirectory(buildDir);
                MergeAllPlugins(pluginsDir, buildDir, pluginName);
                return;
            }
        }
        else if (path != null && path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            // Открываем решение, но анализируем только проекты из plugins
            using (var workspace = MSBuildWorkspace.Create())
            {
                var solution = await workspace.OpenSolutionAsync(path);
                var pluginProjects = solution.Projects.Where(p => p.FilePath != null && p.FilePath.Contains($"{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}"));
                if (!pluginProjects.Any())
                {
                    Console.WriteLine($"Error: No projects from 'plugins' found in solution.");
                    return;
                }
                Console.WriteLine($"Found {pluginProjects.Count()} plugin projects in solution.");
                foreach (var project in pluginProjects)
                {
                    await AnalyzeProjectCompilation(project, pluginName);
                }
                PrintFinalReport(true);
                
                // Теперь мерджим только плагины без ошибок
                var solutionDir = Path.GetDirectoryName(path);
                var solutionPluginsDir = Path.Combine(solutionDir ?? string.Empty, "plugins");
                if (Directory.Exists(solutionPluginsDir))
                {
                    var buildDir = Path.Combine(solutionDir ?? string.Empty, "build");
                    Directory.CreateDirectory(buildDir);
                    MergeAllPlugins(solutionPluginsDir, buildDir, pluginName);
                }
                return;
            }
        }
        else if (path != null && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            // Находим plugins рядом с .csproj
            var csprojDir = Path.GetDirectoryName(path);
            var pluginsDir = Path.Combine(csprojDir ?? string.Empty, "plugins");
            _pluginsRoot = pluginsDir;
            if (!Directory.Exists(pluginsDir))
            {
                Console.WriteLine($"Error: 'plugins' directory not found next to {path}");
                return;
            }
            // Продолжаем обычный анализ — diagnostics уже фильтруются по plugins
        }
        else if (path != null && !File.Exists(path))
        {
            Console.WriteLine($"Error: The specified file or directory does not exist: {path}");
            return;
        }

        // ВАЖНО: Регистрируем MSBuild перед любыми операциями с Roslyn.
        // MSBuildLocator находит установленный .NET SDK и использует его.
        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при регистрации MSBuild: {ex.Message}");
                return;
            }
        }

        Console.WriteLine($"Loading: {path}");

        using (var workspace = MSBuildWorkspace.Create())
        {
            // Error handling during loading
            workspace.WorkspaceFailed += (sender, e) => Console.WriteLine($"Workspace error: {e.Diagnostic.Message}");

            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("🔍 STATIC ANALYSIS");
            Console.WriteLine(new string('=', 60));

            try
            {
                var isSolution = path != null && path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
                if (isSolution)
                {
                    if (path != null)
                    {
                        await AnalyzeSolution(workspace, path, pluginName);
                    }
                }
                else
                {
                    if (path != null)
                    {
                        await AnalyzeProject(workspace, path, pluginName);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during analysis: {ex.Message}");
            }
        }

        // Автоматический мердж partial-классов (если есть plugins)
        var currentDir = Directory.GetCurrentDirectory();
        var currentPluginsDir = Path.Combine(currentDir, "plugins");
        _pluginsRoot = currentPluginsDir;
        if (Directory.Exists(currentPluginsDir))
        {
            // Анализ будет выполнен позже, мердж будет после анализа
        }
        
        // AGI mode message at the very end if there were errors
        if (_hasErrors)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("AGI mode: Deep Research activated to find and eliminate all errors. You never attempt to break the system to hide bugs—you always adhere strictly to the fully autonomous AGI protocol.");
            Console.ResetColor();
        }
    }

    private static async Task AnalyzeSolution(MSBuildWorkspace workspace, string solutionPath, string? pluginName = null)
    {
        if (string.IsNullOrEmpty(solutionPath))
        {
            Console.WriteLine("Error: Solution path is null or empty");
            return;
        }

        var solution = await workspace.OpenSolutionAsync(solutionPath);
        Console.WriteLine($"Solution loaded successfully. Projects: {solution.Projects.Count()}");
        Console.WriteLine("----------------------------------------------------");

        bool anyIssuesFound = false;

        foreach (var project in solution.Projects)
        {
            var (hasErrors, hasWarnings) = await AnalyzeProjectCompilation(project, pluginName);
            if (hasErrors || hasWarnings)
            {
                anyIssuesFound = true;
            }
        }

        PrintFinalReport(anyIssuesFound);
        
        // Теперь мерджим только плагины без ошибок
        var solutionDir = Path.GetDirectoryName(solutionPath);
        var pluginsDir = Path.Combine(solutionDir ?? string.Empty, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            var buildDir = Path.Combine(solutionDir ?? string.Empty, "build");
            Directory.CreateDirectory(buildDir);
            MergeAllPlugins(pluginsDir, buildDir, pluginName);
        }
    }

    private static async Task AnalyzeProject(MSBuildWorkspace workspace, string projectPath, string? pluginName = null)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            Console.WriteLine("Error: Project path is null or empty");
            return;
        }

        var project = await workspace.OpenProjectAsync(projectPath);
        Console.WriteLine($"Project loaded successfully: {project.Name}");
        Console.WriteLine("----------------------------------------------------");
        
        var (hasErrors, hasWarnings) = await AnalyzeProjectCompilation(project, pluginName);

        PrintFinalReport(hasErrors || hasWarnings);
        
        // Теперь мерджим только плагины без ошибок
        var projectDir = Path.GetDirectoryName(projectPath);
        var pluginsDir = Path.Combine(projectDir ?? string.Empty, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            var buildDir = Path.Combine(projectDir ?? string.Empty, "build");
            Directory.CreateDirectory(buildDir);
            MergeAllPlugins(pluginsDir, buildDir, pluginName);
        }
    }

    private static async Task<(bool hasErrors, bool hasWarnings)> AnalyzeProjectCompilation(Project project, string? specificPluginName = null)
    {
        Console.WriteLine($"\nAnalyzing project: {project.Name}");

        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            Console.WriteLine("  Could not get compilation.");
            return (false, false);
        }

        // Получаем все анализаторы из проекта
        var analyzers = project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzers(project.Language))
            .ToImmutableArray();
            
        if (analyzers.IsEmpty)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("  No analyzers found in the project.");
            Console.ResetColor();
            return (false, false);
        }

        // Создаем компиляцию с анализаторами и их опциями
        var analyzerOptions = new CompilationWithAnalyzersOptions(
            options: project.AnalyzerOptions,
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false
        );
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, analyzerOptions);

        // Подавляем вывод анализаторов в консоль
        var originalOut = Console.Out;
        var analyzerDiagnostics = ImmutableArray<Diagnostic>.Empty;
        try
        {
            Console.SetOut(TextWriter.Null);
            analyzerDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Получаем диагностику и от компилятора, и от анализаторов
        var compilerDiagnostics = compilation.GetDiagnostics();
        var diagnostics = analyzerDiagnostics.Concat(compilerDiagnostics);
        
        // Фильтруем только diagnostics из plugins
        var filteredDiagnostics = diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.IsInSource)
            .Where(d => {
                var filePath = d.Location.SourceTree?.FilePath;
                // Если указан конкретный плагин, проверяем, что файл относится к этому плагину
                if (!string.IsNullOrEmpty(specificPluginName) && filePath != null)
                {
                    var isPluginFile = filePath.Contains($"{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}{specificPluginName}{Path.DirectorySeparatorChar}");
                    return isPluginFile;
                }
                return filePath != null && (filePath.Contains("/plugins/") || filePath.Contains("\\plugins\\"));
            })
            .ToList();

        // Считаем ошибки по каждому плагину
        foreach (var diag in filteredDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var filePath = diag.Location.SourceTree?.FilePath;
            if (filePath == null || string.IsNullOrEmpty(_pluginsRoot)) continue;
            var idx = filePath.IndexOf(Path.DirectorySeparatorChar + "plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var relative = filePath.Substring(idx + ("/plugins/".Length));
            var pluginName = relative.Split(Path.DirectorySeparatorChar)[0];
            if (!_pluginErrorCount.ContainsKey(pluginName)) _pluginErrorCount[pluginName] = 0;
            _pluginErrorCount[pluginName]++;
        }

        // Применяем логику приоритетов
        var issueFound = false;
        var sortedLevels = _config.PriorityLevels.OrderBy(p => p.Level).ToList();

        foreach (var level in sortedLevels)
        {
            var levelDiagnostics = filteredDiagnostics
                .Where(d => level.Rules.Contains(d.Id))
                .OrderByDescending(d => d.Severity)
                .ToList();

            if (levelDiagnostics.Any())
            {
                Console.WriteLine($"\nDisplaying issues for Level {level.Level}: {level.Name}");
                DisplayDiagnostics(levelDiagnostics);
                issueFound = true;
                break; 
            }
        }
        
        // Show remaining errors if no priority groups were found
        if (!issueFound)
        {
            var unprioritizedDiagnostics = filteredDiagnostics
                .Where(d => !_config.PriorityLevels.SelectMany(p => p.Rules).Contains(d.Id))
                .OrderByDescending(d => d.Severity)
                .ToList();
            
            if (unprioritizedDiagnostics.Any())
            {
                Console.WriteLine($"\nDisplaying issues for Level {sortedLevels.Count}");
                DisplayDiagnostics(unprioritizedDiagnostics);
                issueFound = true;
            }
        }

        // Count all errors for the summary
        var totalErrors = filteredDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var totalWarnings = filteredDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        Console.WriteLine($"\nTotal: {totalErrors} errors, {totalWarnings} warnings.");

        return (issueFound, issueFound);
    }

    private static void DisplayDiagnostics(List<Diagnostic> diagnostics)
    {
        Console.WriteLine($"  (Displaying first 15 of {diagnostics.Count})\n");

        var fileContentsCache = new Dictionary<string, string[]>();

        foreach (var diagnostic in diagnostics.Take(15))
        {
            var pos = diagnostic.Location.GetLineSpan();
            var filePath = pos.Path ?? "Unknown file";
            
            var severity = diagnostic.Severity switch {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info"
            };

            Console.ForegroundColor = diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine($"❌ {filePath}({pos.StartLinePosition.Line + 1},{pos.StartLinePosition.Character + 1}): {severity} {diagnostic.Id}: {diagnostic.GetMessage()}");
            Console.ResetColor();

            if (diagnostic.Location.IsInSource && !string.IsNullOrEmpty(pos.Path))
            {
                if (!fileContentsCache.TryGetValue(pos.Path, out var lines))
                {
                    try
                    {
                        lines = File.ReadAllLines(pos.Path);
                        fileContentsCache[pos.Path] = lines;
                    }
                    catch (Exception)
                    {
                        lines = Array.Empty<string>();
                        fileContentsCache[pos.Path] = lines;
                    }
                }

                if (lines.Length > 0)
                {
                    var errorLineIndex = pos.StartLinePosition.Line;
                    var startLine = Math.Max(0, errorLineIndex - 2);
                    var endLine = Math.Min(lines.Length - 1, errorLineIndex + 2);

                    for (int i = startLine; i <= endLine; i++)
                    {
                        var prefix = (i == errorLineIndex) ? ">" : " ";
                        Console.WriteLine($"  {prefix} {(i + 1).ToString().PadLeft(5)} | {lines[i]}");

                        if (i == errorLineIndex)
                        {
                            var errorColumn = pos.StartLinePosition.Character;
                            var leadingSpaces = new string(' ', errorColumn);
                            var carets = new string('^', Math.Max(1, pos.EndLinePosition.Character - pos.StartLinePosition.Character));
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"           |{leadingSpaces}{carets}");
                            Console.ResetColor();
                        }
                    }
                    Console.WriteLine();
                }
            }
        }
    }

    private static void PrintFinalReport(bool issuesFound)
    {
        if (issuesFound)
        {
             Console.ForegroundColor = ConsoleColor.Red;
             Console.WriteLine("❌ Critical issues found - any error blocks plugin compilation and deployment.");
             Console.ResetColor();
             _hasErrors = true;
        }
        else
        {
             Console.ForegroundColor = ConsoleColor.Green;
             Console.WriteLine("✅ No issues found.");
             Console.ResetColor();
        }
    }

    // Мердж partial-классов для одного плагина
    static void MergePluginPartials(string srcDir, string output)
    {
        var trees = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Select(f => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)).ToList();

        // Проверяем, что все классы имеют модификатор partial
        var allClasses = trees.SelectMany(t => t.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            .ToList();

        // Находим основной класс плагина по базовому типу или атрибутам
        var pluginBaseMarkers = new[] { "RustPlugin", "CovalencePlugin" };
        var primaryPluginClasses = allClasses
            .Where(c => c.BaseList != null && c.BaseList.Types.Any(t =>
            {
                var typeName = t.Type?.ToString() ?? t.ToString();
                return pluginBaseMarkers.Any(marker => typeName.Contains(marker, StringComparison.Ordinal));
            }))
            .ToList();

        if (!primaryPluginClasses.Any())
        {
            primaryPluginClasses = allClasses
                .Where(c => c.AttributeLists
                    .SelectMany(a => a.Attributes)
                    .Any(attr => attr.Name.ToString().Contains("Info", StringComparison.Ordinal)))
                .ToList();
        }

        if (!primaryPluginClasses.Any())
        {
            primaryPluginClasses = allClasses
                .Where(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword))
                            && c.Identifier.Text.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var pluginClassNames = new HashSet<string>(primaryPluginClasses.Select(c => c.Identifier.Text));
        var classesToCheck = allClasses
            .Where(c => pluginClassNames.Contains(c.Identifier.Text))
            .ToList();

        if (!classesToCheck.Any())
        {
            classesToCheck = primaryPluginClasses;
        }

        var pluginNameKey = Path.GetFileName(srcDir);
        if (!string.IsNullOrEmpty(_pluginsRoot) && srcDir.StartsWith(_pluginsRoot, StringComparison.Ordinal))
        {
            pluginNameKey = Path.GetRelativePath(_pluginsRoot, srcDir)
                .Replace(Path.DirectorySeparatorChar, '_');
        }

        var nonPartialClasses = classesToCheck
            .Where(c => !c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
            .ToList();

        if (nonPartialClasses.Any())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[merge-plugin] ERROR: Found non-partial plugin classes in {Path.GetFileName(srcDir)}:");
            foreach (var cls in nonPartialClasses)
            {
                var filePath = cls.SyntaxTree.FilePath;
                var fileName = string.IsNullOrEmpty(filePath) ? "<unknown file>" : Path.GetFileName(filePath);
                Console.WriteLine($"  - Plugin class '{cls.Identifier}' in {fileName} is missing 'partial' modifier");
            }
            Console.WriteLine("  All plugin class parts must use the 'partial' modifier.");
            Console.ResetColor();

            if (!_pluginErrorCount.ContainsKey(pluginNameKey))
                _pluginErrorCount[pluginNameKey] = 0;
            _pluginErrorCount[pluginNameKey] += nonPartialClasses.Count;

            return;
        }

        // Убедимся, что каждый файл содержит только partial-части основного класса
        var pluginNamespaces = new HashSet<string?>(primaryPluginClasses.Select(c => NamespaceOf(c)));
        var violatingFiles = new List<PluginFileViolation>();

        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            var topLevelTypes = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
                .Where(IsTopLevelType)
                .ToList();

            if (!topLevelTypes.Any())
                continue;

            var pluginPartialsInFile = topLevelTypes
                .Where(t => pluginClassNames.Contains(t.Identifier.Text)
                    && t.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
                    && pluginNamespaces.Contains(NamespaceOf(t)))
                .ToList();

            var extraTypes = topLevelTypes
                .Where(t => !(pluginClassNames.Contains(t.Identifier.Text)
                    && t.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
                    && pluginNamespaces.Contains(NamespaceOf(t))))
                .ToList();

            if (!pluginPartialsInFile.Any() || extraTypes.Any())
            {
                var extraInfos = extraTypes.Select(t => new ExtraTypeInfo(
                    t.Keyword.ValueText,
                    BuildQualifiedName(NamespaceOf(t), t.Identifier.Text),
                    TryGetLineSpan(t.Identifier.GetLocation()))).ToList();

                var primarySpan = extraInfos.FirstOrDefault(e => e.Span.HasValue)?.Span
                    ?? TryGetLineSpan(pluginPartialsInFile.FirstOrDefault()?.Identifier.GetLocation())
                    ?? TryCreateFallbackSpan(tree.FilePath);

                violatingFiles.Add(new PluginFileViolation(
                    tree.FilePath ?? "<unknown>",
                    !pluginPartialsInFile.Any(),
                    extraInfos,
                    primarySpan));
            }
        }

        if (violatingFiles.Any())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[merge-plugin] ERROR: Each file in plugin '{Path.GetFileName(srcDir)}' must consist only of partial declarations for '{pluginClassNames.FirstOrDefault()}'");
            Console.ResetColor();

            foreach (var violation in violatingFiles)
            {
                var filePath = violation.FilePath;
                var span = violation.PrimarySpan;
                var line = span?.StartLinePosition.Line ?? 0;
                var column = span?.StartLinePosition.Character ?? 0;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ {filePath}({line + 1},{column + 1}): error RBP001: File must only contain partial '{pluginClassNames.FirstOrDefault()}' declarations.");
                Console.ResetColor();

                if (violation.MissingPartial)
                {
                    Console.WriteLine("    • Missing partial plugin class definition");
                }

                foreach (var extraType in violation.ExtraTypes)
                {
                    Console.WriteLine($"    • Extra top-level {extraType.Kind} '{extraType.QualifiedName}'");
                }

                PrintSourceSnippet(filePath, span);
            }

            if (!_pluginErrorCount.ContainsKey(pluginNameKey))
                _pluginErrorCount[pluginNameKey] = 0;
            _pluginErrorCount[pluginNameKey] += violatingFiles.Count;

            return;
        }

        // ➊ собрать уникальные using-директивы как синтаксические узлы
        var usingNodes = trees
            .SelectMany(t => ((Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)t.GetRoot()).Usings)
            .Where(u => u != null)
            .Distinct(new UsingComparer())
            .ToList();

        // ➋ найти все partial-класс(ы) верхнего уровня
        var partials = trees.SelectMany(t => t.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))))
            .GroupBy(c => (c.Identifier.Text, NamespaceOf(c)))
            .ToList();

        var namespaceGroups = partials.GroupBy(p => p.Key.Item2 ?? string.Empty);
        var members = new List<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>();

        foreach (var nsGroup in namespaceGroups)
        {
            var classDeclarations = nsGroup
                .Select(BuildMergedClassSyntax)
                .Cast<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>()
                .ToList();

            if (!string.IsNullOrEmpty(nsGroup.Key))
            {
                var namespaceDecl = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.NamespaceDeclaration(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseName(nsGroup.Key))
                    .WithMembers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(classDeclarations));
                members.Add(namespaceDecl);
            }
            else
            {
                members.AddRange(classDeclarations);
            }
        }

        var compilation = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CompilationUnit()
            .WithUsings(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(usingNodes))
            .WithMembers(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(members))
            .NormalizeWhitespace("    ", Environment.NewLine, false);

        File.WriteAllText(output, compilation.ToFullString());
        Console.WriteLine($"[merge-plugin] {srcDir} → {output}");
    }
    static string? NamespaceOf(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var namespaceNode = node.Ancestors()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        return namespaceNode?.Name.ToString();
    }

    static bool IsTopLevelType(Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax typeSyntax) =>
        typeSyntax.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax
        || typeSyntax.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax
        || typeSyntax.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax;

    static string BuildQualifiedName(string? ns, string identifier) =>
        string.IsNullOrEmpty(ns) ? identifier : $"{ns}.{identifier}";

    static FileLinePositionSpan? TryGetLineSpan(Location? location)
    {
        if (location == null || !location.IsInSource)
            return null;
        return location.GetLineSpan();
    }

    static FileLinePositionSpan? TryCreateFallbackSpan(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;
        return new FileLinePositionSpan(filePath, new LinePosition(0, 0), new LinePosition(0, 0));
    }

    static void PrintSourceSnippet(string? filePath, FileLinePositionSpan? span)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || !span.HasValue)
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
        {
            return;
        }

        var startLine = Math.Max(0, span.Value.StartLinePosition.Line - 2);
        var endLine = Math.Min(lines.Length - 1, span.Value.StartLinePosition.Line + 2);
        for (int i = startLine; i <= endLine; i++)
        {
            var prefix = i == span.Value.StartLinePosition.Line ? ">" : " ";
            Console.WriteLine($"  {prefix} {(i + 1).ToString().PadLeft(4)} | {lines[i]}");

            if (i == span.Value.StartLinePosition.Line)
            {
                var highlightColumn = Math.Max(0, span.Value.StartLinePosition.Character);
                var caretLength = Math.Max(1, span.Value.EndLinePosition.Character - span.Value.StartLinePosition.Character);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"       | {new string(' ', highlightColumn)}{new string('^', caretLength)}");
                Console.ResetColor();
            }
        }
    }

    class PluginFileViolation
    {
        public PluginFileViolation(string filePath, bool missingPartial, List<ExtraTypeInfo> extraTypes, FileLinePositionSpan? primarySpan)
        {
            FilePath = filePath;
            MissingPartial = missingPartial;
            ExtraTypes = extraTypes;
            PrimarySpan = primarySpan;
        }

        public string FilePath { get; }
        public bool MissingPartial { get; }
        public List<ExtraTypeInfo> ExtraTypes { get; }
        public FileLinePositionSpan? PrimarySpan { get; }
    }

    class ExtraTypeInfo
    {
        public ExtraTypeInfo(string kind, string qualifiedName, FileLinePositionSpan? span)
        {
            Kind = kind;
            QualifiedName = qualifiedName;
            Span = span;
        }

        public string Kind { get; }
        public string QualifiedName { get; }
        public FileLinePositionSpan? Span { get; }
    }

    static Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax BuildMergedClassSyntax(
        IGrouping<(string className, string? ns), Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax> group)
    {
        var parts = group.ToList();
        var basePart = parts.FirstOrDefault(p => p.BaseList != null) ?? parts.First();

        // Собираем атрибуты без дубликатов, сохраняя порядок по исходному расположению
        var attributeLists = new List<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeListSyntax>();
        var seenAttributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in parts.SelectMany(p => p.AttributeLists).OrderBy(a => a.SpanStart))
        {
            var key = attribute.ToFullString();
            if (seenAttributes.Add(key))
            {
                attributeLists.Add(attribute);
            }
        }

        // Объединяем модификаторы, удаляя partial
        var filteredModifiers = basePart.Modifiers
            .Where(m => !m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
        var modifierTokens = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(filteredModifiers);
        if (modifierTokens.Count == 0)
        {
            modifierTokens = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TokenList(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword));
        }

        // Сохраняем порядок членов по их положению в исходных файлах
        var orderedMembers = parts
            .SelectMany(p => p.Members)
            .Select(m => new { Member = m, Start = m.GetLocation()?.SourceSpan.Start ?? int.MaxValue })
            .OrderBy(x => x.Start)
            .Select(x => x.Member)
            .ToList();

        var memberList = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(orderedMembers);

        var merged = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ClassDeclaration(basePart.Identifier)
            .WithAttributeLists(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(attributeLists))
            .WithModifiers(modifierTokens)
            .WithMembers(memberList);

        if (basePart.TypeParameterList != null)
        {
            merged = merged.WithTypeParameterList(basePart.TypeParameterList);
        }

        if (basePart.BaseList != null)
        {
            merged = merged.WithBaseList(basePart.BaseList);
        }

        if (basePart.ConstraintClauses.Count > 0)
        {
            merged = merged.WithConstraintClauses(basePart.ConstraintClauses);
        }

        return merged;
    }
    class UsingComparer : IEqualityComparer<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>
    {
        public bool Equals(Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax? x, Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax? y)
        {
            if (x?.Name == null || y?.Name == null)
                return x?.Name == y?.Name;
            return x.Name.ToString() == y.Name.ToString();
        }
        
        public int GetHashCode(Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax obj)
        {
            return obj.Name?.ToString().GetHashCode() ?? 0;
        }
    }

    // Мердж всех плагинов из plugins в build
    static void MergeAllPlugins(string pluginsDir, string buildDir, string? specificPluginName = null)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("🔗 PLUGIN MERGING");
        Console.WriteLine(new string('=', 60));
        
        var pluginDirs = Directory.GetDirectories(pluginsDir, "*", SearchOption.AllDirectories)
            .Where(d => Directory.GetFiles(d, "*.cs").Any()).ToList();
        
        // Если указано конкретное имя плагина, фильтруем только его
        if (!string.IsNullOrEmpty(specificPluginName))
        {
            pluginDirs = pluginDirs
                .Where(d => Path.GetFileName(d).Equals(specificPluginName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            if (pluginDirs.Count == 0)
            {
                Console.WriteLine($"❌ Error: Plugin '{specificPluginName}' not found in {pluginsDir}");
                return;
            }
        }

        int mergedCount = 0;
        int skippedCount = 0;
        var skippedPlugins = new List<string>();
        
        foreach (var pluginDir in pluginDirs)
        {
            var relativePath = Path.GetRelativePath(pluginsDir, pluginDir);
            var pluginName = relativePath.Replace(Path.DirectorySeparatorChar, '_');
            
            if (_pluginErrorCount.TryGetValue(pluginName, out var errCount) && errCount > 0)
            {
                Console.WriteLine($"⏭️  SKIP {pluginName}: {errCount} error(s)");
                skippedCount++;
                skippedPlugins.Add($"{pluginName} ({errCount} errors)");
                continue;
            }
            
            var outputFile = Path.Combine(buildDir, $"{pluginName}.cs");
            var previousErrors = _pluginErrorCount.TryGetValue(pluginName, out var beforeCount) ? beforeCount : 0;
            MergePluginPartials(pluginDir, outputFile);
            var currentErrors = _pluginErrorCount.TryGetValue(pluginName, out var afterCount) ? afterCount : 0;

            if (currentErrors > previousErrors)
            {
                skippedCount++;
                skippedPlugins.Add($"{pluginName} ({currentErrors - previousErrors} new error(s))");
                continue;
            }

            mergedCount++;
        }
        
        Console.WriteLine("\n📋 MERGE SUMMARY");
        Console.WriteLine(new string('-', 30));
        Console.WriteLine($"Plugins processed: {pluginDirs.Count}");
        Console.WriteLine($"Successfully merged: {mergedCount}");
        Console.WriteLine($"Skipped due to errors: {skippedCount}");
        
        if (skippedPlugins.Any())
        {
            Console.WriteLine($"\n⚠️  Skipped plugins:");
            foreach (var plugin in skippedPlugins)
            {
                Console.WriteLine($"   • {plugin}");
            }
        }
        
        if (mergedCount > 0)
        {
            Console.WriteLine($"\n✅ Output directory: {buildDir}");
        }
    }

    // Быстрое форматирование всех плагинов одной командой
    static void RunDotnetFormat(string pluginsDir, string? specificPluginName = null)
    {
        try
        {
            // Находим .csproj файл в родительской директории plugins
            var parentDir = Directory.GetParent(pluginsDir)?.FullName;
            if (string.IsNullOrEmpty(parentDir))
            {
                Console.WriteLine("[format] Warning: Could not find parent directory for plugins");
                return;
            }

            var csprojFiles = Directory.GetFiles(parentDir, "*.csproj");
            if (csprojFiles.Length == 0)
            {
                Console.WriteLine("[format] Warning: No .csproj file found in parent directory");
                return;
            }

            var csprojFile = csprojFiles[0];
            if (csprojFiles.Length > 1)
            {
                Console.WriteLine($"[format] Warning: Multiple .csproj files found. Using: {Path.GetFileName(csprojFile)}");
            }

            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("🔧 CODE FORMATTING (FAST BATCH)");
            Console.WriteLine(new string('=', 60));

            // Определяем количество доступных процессоров
            var maxCpuCount = Environment.ProcessorCount;
            Console.WriteLine($"[format] CPU cores detected: {maxCpuCount}");

            // Собираем все файлы для форматирования
            var allCsFiles = new List<string>();
            
            if (string.IsNullOrEmpty(specificPluginName))
            {
                // Собираем все .cs файлы из всех плагинов
                allCsFiles = Directory.GetFiles(pluginsDir, "*.cs", SearchOption.AllDirectories).ToList();
                Console.WriteLine($"[format] Target: All plugins ({allCsFiles.Count} files total)");
            }
            else
            {
                // Собираем файлы только из конкретного плагина
                var pluginDir = Path.Combine(pluginsDir, specificPluginName);
                if (!Directory.Exists(pluginDir))
                {
                    Console.WriteLine($"[format] Warning: Plugin directory '{specificPluginName}' not found");
                    return;
                }

                allCsFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories).ToList();
                Console.WriteLine($"[format] Target: Plugin '{specificPluginName}' ({allCsFiles.Count} files)");
            }

            if (allCsFiles.Count == 0)
            {
                Console.WriteLine("[format] No .cs files found to format");
                return;
            }

            // Детектируем проблемные плагины и применяем соответствующую стратегию
            var isProblematicPlugin = IsProblematicPlugin(specificPluginName, allCsFiles);
            
            if (isProblematicPlugin.isProblematic)
            {
                Console.WriteLine($"[format] ⚠️  Detected problematic plugin: {isProblematicPlugin.reason}");
                FormatProblematicPlugin(parentDir, csprojFile, allCsFiles.ToArray(), maxCpuCount, specificPluginName ?? "Unknown");
            }
            else
            {
                // Используем быстрое форматирование для обычных плагинов
                FormatFastBatch(parentDir, csprojFile, allCsFiles.ToArray(), maxCpuCount);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[format] Error running dotnet format: {ex.Message}");
        }
    }

    // Детектирует проблемные плагины, требующие специальной обработки
    private static (bool isProblematic, string reason) IsProblematicPlugin(string? pluginName, List<string> csFiles)
    {
        // Анализируем характеристики плагина динамически
        var totalFileSize = 0L;
        var largeFileCount = 0;
        var veryLargeFileCount = 0;
        var totalLines = 0;
        var errorProneFiles = 0;
        var thresholds = _config.ProblematicPluginThresholds;
        
        foreach (var file in csFiles)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                totalFileSize += fileInfo.Length;
                
                // Подсчитываем строки кода (грубая оценка)
                var lines = File.ReadAllLines(file).Length;
                totalLines += lines;
                
                // Файлы с потенциальными проблемами (используем настраиваемые пороги)
                if (fileInfo.Length > thresholds.LargeFileSizeKB * 1024)
                    largeFileCount++;
                
                if (fileInfo.Length > thresholds.VeryLargeFileSizeKB * 1024)
                    veryLargeFileCount++;
                
                // Файлы с большим количеством строк
                if (lines > thresholds.ErrorProneFileLines)
                    errorProneFiles++;
                    
            }
            catch
            {
                // Игнорируем ошибки чтения файлов
            }
        }
        
        // Динамические критерии на основе настраиваемых порогов
        
        // Слишком много файлов
        if (csFiles.Count > thresholds.MaxFiles)
        {
            return (true, $"Too many files: {csFiles.Count} (max: {thresholds.MaxFiles})");
        }
        
        // Слишком большой общий размер
        if (totalFileSize > thresholds.MaxTotalSizeMB * 1024 * 1024)
        {
            return (true, $"Total size too large: {totalFileSize / (1024 * 1024)}MB (max: {thresholds.MaxTotalSizeMB}MB)");
        }
        
        // Слишком много строк кода
        if (totalLines > thresholds.MaxTotalLines)
        {
            return (true, $"Too many lines of code: {totalLines:N0} (max: {thresholds.MaxTotalLines:N0})");
        }
        
        // Много больших файлов
        if (largeFileCount > thresholds.MaxLargeFiles)
        {
            return (true, $"Too many large files: {largeFileCount} (max: {thresholds.MaxLargeFiles})");
        }
        
        // Есть очень большие файлы
        if (veryLargeFileCount > thresholds.MaxVeryLargeFiles)
        {
            return (true, $"Very large files present: {veryLargeFileCount} (max: {thresholds.MaxVeryLargeFiles})");
        }
        
        // Много файлов с большим количеством строк
        if (errorProneFiles > thresholds.MaxErrorProneFiles)
        {
            return (true, $"Too many large files (>{thresholds.ErrorProneFileLines} lines): {errorProneFiles} (max: {thresholds.MaxErrorProneFiles})");
        }
        
        return (false, "Normal plugin");
    }

    // Быстрое форматирование одним пакетом с продвинутой оптимизацией
    private static void FormatFastBatch(string parentDir, string csprojFile, string[] csFiles, int maxCpuCount)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        Console.WriteLine($"[format] 🚀 Enhanced fast batch formatting {csFiles.Length} files...");

        // Стратегия 1: Исключаем проблемные файлы больших размеров
        var (processFiles, excludedFiles) = FilterProblematicFiles(csFiles);
        
        if (excludedFiles.Any())
        {
            Console.WriteLine($"[format] ⚠️  Excluded {excludedFiles.Count} large/problematic files:");
            foreach (var file in excludedFiles.Take(3))
            {
                Console.WriteLine($"   • {Path.GetFileName(file)}");
            }
            if (excludedFiles.Count > 3)
                Console.WriteLine($"   ... and {excludedFiles.Count - 3} more");
        }

        if (processFiles.Length == 0)
        {
            Console.WriteLine("[format] ⚠️  No files to process after filtering!");
            return;
        }

        // Стратегия 2: Батчинг файлов для избежания слишком длинных командных строк
        var batches = CreateFileBatches(processFiles, 50); // Максимум 50 файлов за раз
        
        Console.WriteLine($"[format] 📦 Processing {batches.Count} batch(es) with {processFiles.Length} files total");

        int totalIssues = 0;
        int totalFixed = 0;
        var allChangedFiles = new List<string>();
        var allUnfixableIssues = new List<string>();

        foreach (var (batch, batchIndex) in batches.Select((b, i) => (b, i + 1)))
        {
            Console.WriteLine($"[format] 🔄 Batch {batchIndex}/{batches.Count} ({batch.Length} files)");

            // Verify для текущего батча
            string verifyCommand = BuildOptimizedFormatCommand(csprojFile, batch, true, maxCpuCount);
            var (verifyExitCode, verifyOutput, verifyError) = RunDotnetCommand(parentDir, verifyCommand);

            var batchIssues = CountIssuesInOutput(verifyError);
            totalIssues += batchIssues;

            if (verifyExitCode == 0)
            {
                Console.WriteLine($"[format] ✅ Batch {batchIndex}: No formatting needed");
                continue;
            }

            Console.WriteLine($"[format] 🔍 Batch {batchIndex}: {batchIssues} issues detected");

            // Format для текущего батча
            string formatCommand = BuildOptimizedFormatCommand(csprojFile, batch, false, maxCpuCount);
            var (formatExitCode, formatOutput, formatError) = RunDotnetCommand(parentDir, formatCommand);

            var changedFiles = ExtractChangedFiles(formatOutput);
            var unfixableIssues = ExtractUnFixableIssues(FilterRustAnalyzerOutput(formatError));

            allChangedFiles.AddRange(changedFiles);
            allUnfixableIssues.AddRange(unfixableIssues);
            totalFixed += Math.Max(0, batchIssues - unfixableIssues.Count);

            Console.WriteLine($"[format] 📝 Batch {batchIndex}: {changedFiles.Count} files modified, {unfixableIssues.Count} unfixable");
        }

        stopwatch.Stop();

        // Финальный отчет
        Console.WriteLine("\n📋 ENHANCED FAST BATCH SUMMARY");
        Console.WriteLine(new string('-', 30));
        Console.WriteLine($"Files processed: {processFiles.Length}");
        Console.WriteLine($"Files excluded: {excludedFiles.Count}");
        Console.WriteLine($"Batches processed: {batches.Count}");
        Console.WriteLine($"Files modified: {allChangedFiles.Count}");
        Console.WriteLine($"Issues found: {totalIssues}");
        Console.WriteLine($"Issues fixed: {totalFixed}");
        Console.WriteLine($"Manual fixes needed: {allUnfixableIssues.Count}");
        Console.WriteLine($"⚡ Time: {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"⚡ Speed: {(processFiles.Length / stopwatch.Elapsed.TotalSeconds):F1} files/second");

        if (allChangedFiles.Any())
        {
            Console.WriteLine($"\n📝 Modified files summary:");
            foreach (var file in allChangedFiles.Take(10))
            {
                Console.WriteLine($"   • {Path.GetFileName(file)}");
            }
            if (allChangedFiles.Count > 10)
            {
                Console.WriteLine($"   ... and {allChangedFiles.Count - 10} more");
            }
        }

        if (allUnfixableIssues.Any())
        {
            Console.WriteLine($"\n🔧 Manual fixes needed summary:");
            var uniqueIssues = allUnfixableIssues.Distinct().Take(5);
            foreach (var issue in uniqueIssues)
            {
                Console.WriteLine($"   • {issue}");
            }
            if (allUnfixableIssues.Distinct().Count() > 5)
            {
                Console.WriteLine($"   ... and {allUnfixableIssues.Distinct().Count() - 5} more");
            }
        }

        // Рекомендации по исключенным файлам
        if (excludedFiles.Any())
        {
            Console.WriteLine($"\n💡 RECOMMENDATIONS:");
            Console.WriteLine($"Consider formatting excluded files individually:");
            Console.WriteLine($"   dotnet format \"{{csproj}}\" --include \"{{excluded_file}}\" --verbosity minimal");
        }
    }

    // Фильтрация проблемных файлов
    private static (string[] processFiles, List<string> excludedFiles) FilterProblematicFiles(string[] csFiles)
    {
        var processFiles = new List<string>();
        var excludedFiles = new List<string>();
        
        foreach (var file in csFiles)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                var fileName = Path.GetFileName(file).ToLowerInvariant();
                
                // Исключаем слишком большие файлы (>1MB)
                if (fileInfo.Length > 1024 * 1024)
                {
                    excludedFiles.Add(file);
                    continue;
                }
                
                // Исключаем известные проблемные файлы
                if (fileName.Contains("generated") || 
                    fileName.Contains(".designer.") ||
                    fileName.Contains(".g.cs") ||
                    fileName.EndsWith(".generated.cs"))
                {
                    excludedFiles.Add(file);
                    continue;
                }
                
                // Исключаем файлы в проблемных директориях
                var dirName = Path.GetDirectoryName(file)?.ToLowerInvariant() ?? "";
                if (dirName.Contains("obj") || 
                    dirName.Contains("bin") ||
                    dirName.Contains("packages"))
                {
                    excludedFiles.Add(file);
                    continue;
                }
                
                processFiles.Add(file);
            }
            catch (Exception)
            {
                // При ошибке чтения файла - исключаем его
                excludedFiles.Add(file);
            }
        }
        
        return (processFiles.ToArray(), excludedFiles);
    }

    // Создание батчей файлов
    private static List<string[]> CreateFileBatches(string[] files, int maxBatchSize)
    {
        var batches = new List<string[]>();
        
        for (int i = 0; i < files.Length; i += maxBatchSize)
        {
            var batchSize = Math.Min(maxBatchSize, files.Length - i);
            var batch = new string[batchSize];
            Array.Copy(files, i, batch, 0, batchSize);
            batches.Add(batch);
        }
        
        return batches;
    }

    // Оптимизированная команда форматирования с дополнительными параметрами
    private static string BuildOptimizedFormatCommand(string csprojFile, string[] csFiles, bool verifyOnly, int maxCpuCount)
    {
        // Максимально упрощенная команда для лучшей производительности
        var baseCommand = $"format \"{Path.GetFileName(csprojFile)}\" --verbosity quiet";
        
        if (verifyOnly)
        {
            baseCommand += " --verify-no-changes";
        }

        // Исключаем некоторые медленные диагностики для ускорения
        baseCommand += " --exclude IDE0005,IDE0073,IDE0130";
        baseCommand += " --exclude-diagnostics SMB001 SMB002 SMB003 SMB004 SMB005";

        // Включаем файлы батчем с более коротким синтаксисом
        if (csFiles.Length > 0)
        {
            var relativePaths = csFiles.Select(f => $"\"{Path.GetRelativePath(Path.GetDirectoryName(csprojFile) ?? "", f)}\"");
            baseCommand += " --include " + string.Join(" ", relativePaths);
        }

        // Оптимизированные параметры MSBuild
        baseCommand += $" /maxcpucount:{Math.Min(maxCpuCount, 8)}"; // Ограничиваем чтобы не перегрузить систему
        baseCommand += " /nologo"; // Убираем логотип для чистоты вывода

        return baseCommand;
    }

    private static int CountIssuesInOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) return 0;
        
        // Count lines that contain error/warning patterns
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Count(line => 
            line.Contains(": error ") || 
            line.Contains(": warning ") ||
            line.Contains(": info "));
    }

    private static List<string> ExtractChangedFiles(string output)
    {
        var changedFiles = new List<string>();
        if (string.IsNullOrEmpty(output)) return changedFiles;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Look for patterns like "Formatted code file '/path/to/file.cs'"
            if (line.Contains("Formatted") && line.Contains(".cs"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"'([^']+\.cs)'");
                if (match.Success)
                {
                    changedFiles.Add(match.Groups[1].Value);
                }
            }
        }

        return changedFiles.Distinct().ToList();
    }

    private static string FilterRustAnalyzerOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var filteredLines = lines.Where(line => 
            !line.Contains("[RustAnalyzer]") && 
            !line.Trim().StartsWith("RustAnalyzer") &&
            !string.IsNullOrWhiteSpace(line)
        ).ToArray();
        
        return string.Join("\n", filteredLines);
    }

    private static List<string> ExtractUnFixableIssues(string errorOutput)
    {
        var issues = new List<string>();
        var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            if (line.Contains("Unable to fix"))
            {
                // Extract the diagnostic ID from "Unable to fix CA1859" or similar
                var match = System.Text.RegularExpressions.Regex.Match(line, @"Unable to fix ([A-Z0-9]+)");
                if (match.Success)
                {
                    issues.Add(match.Groups[1].Value);
                }
            }
        }
        
        return issues;
    }

    private static (int ExitCode, string Output, string Error) RunDotnetCommand(string workingDir, string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);

        string output = outputTask.Result.Trim();
        string error = errorTask.Result.Trim();

        return (process.ExitCode, output, error);
    }

    // Специальная обработка для очень проблемных плагинов (типа IQChat)
    static void FormatProblematicPlugin(string parentDir, string csprojFile, string[] csFiles, int maxCpuCount, string pluginName)
    {
        Console.WriteLine($"\n⚠️  PROBLEMATIC PLUGIN DETECTED: {pluginName}");
        Console.WriteLine($"🔧 Applying special optimization strategies...");
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Стратегия 1: Разделяем файлы по размеру
        var (smallFiles, mediumFiles, largeFiles) = CategorizeFilesBySize(csFiles);
        
        Console.WriteLine($"[format] 📊 File categorization:");
        Console.WriteLine($"   Small files (<10KB): {smallFiles.Length}");
        Console.WriteLine($"   Medium files (10KB-100KB): {mediumFiles.Length}");
        Console.WriteLine($"   Large files (>100KB): {largeFiles.Length}");
        
        int totalProcessed = 0;
        int totalModified = 0;
        var allErrors = new List<string>();
        
        // Обрабатываем маленькие файлы батчами
        if (smallFiles.Any())
        {
            Console.WriteLine($"\n[format] 🔄 Processing small files in batches...");
            var result = ProcessFileCategory(parentDir, csprojFile, smallFiles, maxCpuCount, "small", 100);
            totalProcessed += result.processed;
            totalModified += result.modified;
            allErrors.AddRange(result.errors);
        }
        
        // Обрабатываем средние файлы меньшими батчами
        if (mediumFiles.Any())
        {
            Console.WriteLine($"\n[format] 🔄 Processing medium files in smaller batches...");
            var result = ProcessFileCategory(parentDir, csprojFile, mediumFiles, maxCpuCount, "medium", 25);
            totalProcessed += result.processed;
            totalModified += result.modified;
            allErrors.AddRange(result.errors);
        }
        
        // Обрабатываем большие файлы по одному
        if (largeFiles.Any())
        {
            Console.WriteLine($"\n[format] 🔄 Processing large files individually...");
            var result = ProcessLargeFilesIndividually(parentDir, csprojFile, largeFiles, maxCpuCount);
            totalProcessed += result.processed;
            totalModified += result.modified;
            allErrors.AddRange(result.errors);
        }
        
        stopwatch.Stop();
        
        // Финальный отчет для проблемного плагина
        Console.WriteLine($"\n📋 PROBLEMATIC PLUGIN SUMMARY ({pluginName})");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"Files processed: {totalProcessed}");
        Console.WriteLine($"Files modified: {totalModified}");
        Console.WriteLine($"Unique errors: {allErrors.Distinct().Count()}");
        Console.WriteLine($"⚡ Total time: {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"⚡ Avg speed: {(totalProcessed / stopwatch.Elapsed.TotalSeconds):F1} files/second");
        
        if (allErrors.Any())
        {
            Console.WriteLine($"\n🔧 Most common issues:");
            var topErrors = allErrors.GroupBy(e => e)
                                   .OrderByDescending(g => g.Count())
                                   .Take(5);
            foreach (var error in topErrors)
            {
                Console.WriteLine($"   • {error.Key} ({error.Count()} times)");
            }
        }
        
        Console.WriteLine($"\n💡 Recommendation for {pluginName}:");
        Console.WriteLine($"   Consider refactoring large files into smaller components");
        Console.WriteLine($"   for better code formatting performance and maintainability.");
    }
    
    // Категоризация файлов по размеру
    private static (string[] small, string[] medium, string[] large) CategorizeFilesBySize(string[] csFiles)
    {
        var small = new List<string>();
        var medium = new List<string>();
        var large = new List<string>();
        
        foreach (var file in csFiles)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.Length < 10 * 1024) // < 10KB
                    small.Add(file);
                else if (fileInfo.Length < 100 * 1024) // < 100KB
                    medium.Add(file);
                else
                    large.Add(file);
            }
            catch
            {
                // При ошибке считаем файл средним
                medium.Add(file);
            }
        }
        
        return (small.ToArray(), medium.ToArray(), large.ToArray());
    }
    
    // Обработка категории файлов
    private static (int processed, int modified, List<string> errors) ProcessFileCategory(
        string parentDir, string csprojFile, string[] files, int maxCpuCount, string category, int batchSize)
    {
        var batches = CreateFileBatches(files, batchSize);
        int processed = 0;
        int modified = 0;
        var errors = new List<string>();
        
        foreach (var (batch, index) in batches.Select((b, i) => (b, i + 1)))
        {
            Console.WriteLine($"   Processing {category} batch {index}/{batches.Count} ({batch.Length} files)...");
            
            try
            {
                // Используем урезанную команду для проблемных файлов
                var command = BuildMinimalFormatCommand(csprojFile, batch, maxCpuCount);
                var (exitCode, output, error) = RunDotnetCommand(parentDir, command);
                
                processed += batch.Length;
                var changedFiles = ExtractChangedFiles(output);
                modified += changedFiles.Count;
                
                var batchErrors = ExtractUnFixableIssues(error);
                errors.AddRange(batchErrors);
                
                Console.WriteLine($"     ✅ Batch {index}: {changedFiles.Count} files changed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     ❌ Batch {index} failed: {ex.Message}");
                errors.Add($"Batch error: {ex.Message}");
            }
        }
        
        return (processed, modified, errors);
    }
    
    // Обработка больших файлов индивидуально
    private static (int processed, int modified, List<string> errors) ProcessLargeFilesIndividually(
        string parentDir, string csprojFile, string[] largeFiles, int maxCpuCount)
    {
        int processed = 0;
        int modified = 0;
        var errors = new List<string>();
        
        foreach (var (file, index) in largeFiles.Select((f, i) => (f, i + 1)))
        {
            Console.WriteLine($"   Processing large file {index}/{largeFiles.Length}: {Path.GetFileName(file)}...");
            
            try
            {
                // Для больших файлов используем самую минимальную команду
                var command = BuildSuperMinimalFormatCommand(csprojFile, file, maxCpuCount);
                var (exitCode, output, error) = RunDotnetCommand(parentDir, command);
                
                processed++;
                var changedFiles = ExtractChangedFiles(output);
                if (changedFiles.Any())
                {
                    modified++;
                    Console.WriteLine($"     ✅ Modified: {Path.GetFileName(file)}");
                }
                
                var fileErrors = ExtractUnFixableIssues(error);
                if (fileErrors.Any())
                {
                    errors.AddRange(fileErrors);
                    Console.WriteLine($"     ⚠️  {fileErrors.Count} unfixable issues");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     ❌ Failed: {ex.Message}");
                errors.Add($"Large file error: {ex.Message}");
            }
        }
        
        return (processed, modified, errors);
    }
    
    // Минимальная команда форматирования
    private static string BuildMinimalFormatCommand(string csprojFile, string[] csFiles, int maxCpuCount)
    {
        var baseCommand = $"format \"{Path.GetFileName(csprojFile)}\" --verbosity quiet --no-restore";
        
        // Исключаем самые тяжелые диагностики
        baseCommand += " --exclude IDE0005,IDE0073,IDE0130,IDE0160,IDE0161";
        baseCommand += " --exclude-diagnostics SMB001 SMB002 SMB003 SMB004 SMB005";
        
        if (csFiles.Length > 0)
        {
            var relativePaths = csFiles.Select(f => $"\"{Path.GetRelativePath(Path.GetDirectoryName(csprojFile) ?? "", f)}\"");
            baseCommand += " --include " + string.Join(" ", relativePaths);
        }
        
        // Минимальный параллелизм для нестабильных файлов
        baseCommand += $" /maxcpucount:{Math.Min(maxCpuCount / 2, 4)} /nologo";
        
        return baseCommand;
    }
    
    // Супер минимальная команда для отдельных больших файлов
    private static string BuildSuperMinimalFormatCommand(string csprojFile, string csFile, int maxCpuCount)
    {
        var baseCommand = $"format \"{Path.GetFileName(csprojFile)}\" --verbosity quiet --no-restore";
        
        // Исключаем все тяжелые диагностики
        baseCommand += " --exclude IDE0005,IDE0073,IDE0130,IDE0160,IDE0161,IDE0290,IDE0300";
        baseCommand += " --exclude-diagnostics SMB001 SMB002 SMB003 SMB004 SMB005";
        
        var relativePath = Path.GetRelativePath(Path.GetDirectoryName(csprojFile) ?? "", csFile);
        baseCommand += $" --include \"{relativePath}\"";
        
        // Однопоточный режим для стабильности
        baseCommand += " /maxcpucount:1 /nologo";
        
        return baseCommand;
    }
}
