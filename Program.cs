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
using System.Collections.Immutable;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;

public class LinterConfig
{
    public List<PriorityLevel> PriorityLevels { get; set; } = new List<PriorityLevel>();
}

public class PriorityLevel
{
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Rules { get; set; } = new List<string>();
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
            .Select(f => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(f))).ToList();

        // Проверяем, что все классы имеют модификатор partial
        var allClasses = trees.SelectMany(t => t.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            .ToList();

        // Находим только основные классы плагинов (наследуемые от RustPlugin или CovalencePlugin)
        var pluginClasses = allClasses
            .Where(c => c.BaseList != null && c.BaseList.Types.Any(t => 
                t.ToString().Contains("RustPlugin") || t.ToString().Contains("CovalencePlugin")))
            .ToList();
        
        // Если не нашли ни одного класса плагина, берем все public классы
        if (!pluginClasses.Any())
        {
            pluginClasses = allClasses
                .Where(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)))
                .ToList();
        }
        
        // Проверяем, что основные классы плагинов имеют модификатор partial
        var nonPartialClasses = pluginClasses
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
            Console.WriteLine("  All plugin classes must have 'partial' modifier to be merged properly.");
            Console.ResetColor();
            
            // Добавляем ошибку для этого плагина
            var pluginName = Path.GetFileName(srcDir);
            if (!_pluginErrorCount.ContainsKey(pluginName)) _pluginErrorCount[pluginName] = 0;
            _pluginErrorCount[pluginName] += nonPartialClasses.Count;
            
            return;
        }
        
        // ➊ собрать уникальные using-директивы
        var usings = trees.SelectMany(t => ((Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)t.GetRoot()).Usings)
            .Distinct(new UsingComparer())
            .Select(u => u.ToFullString());
        // ➋ найти все partial-класс(ы) верхнего уровня
        var partials = trees.SelectMany(t => t.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))))
            .GroupBy(c => (c.Identifier.Text, NamespaceOf(c)))
            .ToList();
        // ➌ объединить тела
        var result = string.Join(Environment.NewLine, usings) + "\n\n";
        foreach (var group in partials)
        {
            var (className, ns) = group.Key;
            result += $"namespace {ns}\n{{\n    public class {className}\n    {{\n";
            foreach (var part in group)
                result += string.Join("", part.Members.Select(m => m.ToFullString())) + "\n";
            result += "    }\n}\n";
        }
        File.WriteAllText(output, result);
        Console.WriteLine($"[merge-plugin] {srcDir} → {output}");
    }
    static string? NamespaceOf(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
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
            MergePluginPartials(pluginDir, outputFile);
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

    // Запуск dotnet format для плагинов
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
            Console.WriteLine("🔧 CODE FORMATTING");
            Console.WriteLine(new string('=', 60));

            if (string.IsNullOrEmpty(specificPluginName))
            {
                // Форматируем все плагины
                Console.WriteLine("[format] Target: All plugins");
                FormatWithLoop(parentDir, csprojFile, null);
            }
            else
            {
                // Форматируем конкретный плагин
                var pluginDir = Path.Combine(pluginsDir, specificPluginName);
                if (!Directory.Exists(pluginDir))
                {
                    Console.WriteLine($"[format] Warning: Plugin directory '{specificPluginName}' not found");
                    return;
                }

                var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
                if (csFiles.Length == 0)
                {
                    Console.WriteLine($"[format] Warning: No .cs files found in plugin '{specificPluginName}'");
                    return;
                }

                Console.WriteLine($"[format] Target: Plugin '{specificPluginName}' ({csFiles.Length} files)");
                FormatWithLoop(parentDir, csprojFile, csFiles);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[format] Error running dotnet format: {ex.Message}");
        }
    }

    private static void FormatWithLoop(string parentDir, string csprojFile, string[]? csFiles)
    {
        const int maxAttempts = 3;
        int attempts = 0;
        bool needsFormatting = true;
        var unFixableIssues = new HashSet<string>();
        var totalIssuesFound = 0;
        var issuesFixed = 0;
        var previousUnFixableIssues = new HashSet<string>();

        while (needsFormatting && attempts < maxAttempts)
        {
            attempts++;
            Console.WriteLine($"\n┌─ Attempt {attempts}/{maxAttempts} " + new string('─', 40));

            // Step 1: Verify if changes are needed
            string verifyCommand = $"format \"{Path.GetFileName(csprojFile)}\" --verify-no-changes --exclude-diagnostics IDE0005 --verbosity diagnostic";
            if (csFiles != null)
            {
                verifyCommand += " --include " + string.Join(" ", csFiles.Select(f => $"\"{Path.GetRelativePath(parentDir, f)}\""));
            }
            var (verifyExitCode, verifyOutput, verifyError) = RunDotnetCommand(parentDir, verifyCommand);

            // Count issues found in this attempt
            var currentIssuesCount = CountIssuesInOutput(verifyError);
            if (attempts == 1)
            {
                totalIssuesFound = currentIssuesCount;
                Console.WriteLine($"│ 🔍 Issues detected: {totalIssuesFound}");
            }

            if (verifyExitCode == 0)
            {
                Console.WriteLine("│ ✅ No more formatting needed");
                needsFormatting = false;
                break;
            }

            Console.WriteLine($"│ 📊 Remaining issues: {currentIssuesCount}");

            // Step 2: Perform formatting
            string formatCommand = $"format \"{Path.GetFileName(csprojFile)}\" --exclude-diagnostics IDE0005 --verbosity diagnostic";
            if (csFiles != null)
            {
                formatCommand += " --include " + string.Join(" ", csFiles.Select(f => $"\"{Path.GetRelativePath(parentDir, f)}\""));
            }
            var (formatExitCode, formatOutput, formatError) = RunDotnetCommand(parentDir, formatCommand);

            // Extract changed files from output
            var changedFiles = ExtractChangedFiles(formatOutput);
            if (changedFiles.Any())
            {
                Console.WriteLine($"│ 📝 Modified files: {string.Join(", ", changedFiles.Select(Path.GetFileName))}");
            }
            else
            {
                Console.WriteLine("│ 📝 No files were modified");
            }

            // Filter out RustAnalyzer noise and extract unfixable issues
            var filteredFormatError = FilterRustAnalyzerOutput(formatError);
            if (!string.IsNullOrEmpty(filteredFormatError)) 
            {
                var unFixableInThisAttempt = ExtractUnFixableIssues(filteredFormatError);
                foreach (var issue in unFixableInThisAttempt)
                {
                    unFixableIssues.Add(issue);
                }
                
                if (unFixableInThisAttempt.Any())
                {
                    Console.WriteLine($"│ ❌ Auto-fix failed: {string.Join(", ", unFixableInThisAttempt)}");
                    
                    // Check if unfixable issues are the same as previous attempt
                    if (attempts > 1 && new HashSet<string>(unFixableInThisAttempt).SetEquals(previousUnFixableIssues))
                    {
                        Console.WriteLine("│ ⚠️  Same issues persist - stopping early");
                        needsFormatting = false;
                        break;
                    }
                    
                    previousUnFixableIssues = new HashSet<string>(unFixableInThisAttempt);
                }
            }

            Console.WriteLine("└" + new string('─', 50));
        }

        // Calculate statistics
        issuesFixed = Math.Max(0, totalIssuesFound - unFixableIssues.Count);

        // Final summary
        Console.WriteLine("\n📋 FORMATTING SUMMARY");
        Console.WriteLine(new string('-', 30));
        Console.WriteLine($"Total attempts: {attempts}");
        Console.WriteLine($"Issues found: {totalIssuesFound}");
        Console.WriteLine($"Issues fixed: {issuesFixed}");
        Console.WriteLine($"Manual fixes needed: {unFixableIssues.Count}");

        if (needsFormatting && unFixableIssues.Any())
        {
            Console.WriteLine($"\n🔧 Requires manual attention:");
            foreach (var issue in unFixableIssues.OrderBy(x => x))
            {
                Console.WriteLine($"   • {issue}");
            }
        }
        else if (!needsFormatting)
        {
            Console.WriteLine("\n✅ All formatting issues resolved!");
        }
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

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();

        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
