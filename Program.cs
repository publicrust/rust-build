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
    
    static async Task Main(string[] args)
    {
        try
        {
            // Определяем путь к конфигу: сначала в директории проекта, затем fallback
            string? configPath = null;
            
            // 1. Если указан путь к проекту/решению, ищем конфиг рядом с ним
            if (args.Length > 0)
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
            
            // 2. Если конфиг не найден, ищем в директории самого проекта (AppContext.BaseDirectory)
            if (configPath == null)
            {
                var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, "linter.config.json");
                if (File.Exists(baseDirCandidate))
                    configPath = baseDirCandidate;
            }
            
            // 3. Последний fallback: текущая рабочая директория
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
        if (args.Length == 0)
        {
            path = Directory.GetCurrentDirectory();
            Console.WriteLine($"No path provided, using current directory: {Path.GetFullPath(path)}");
        }
        else
        {
            path = args[0];
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
                    await AnalyzeProject(MSBuildWorkspace.Create(), proj);
                }
                PrintFinalReport(true);
                return;
            }
        }
        else if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
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
                    await AnalyzeProjectCompilation(project);
                }
                PrintFinalReport(true);
                return;
            }
        }
        else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            // Проверяем, что проект из plugins
            if (!path.Contains($"{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}"))
            {
                Console.WriteLine($"Error: Only projects from 'plugins' directory can be analyzed.");
                return;
            }
        }
        else if (!File.Exists(path))
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

            try
            {
                var isSolution = path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
                if (isSolution)
                {
                    await AnalyzeSolution(workspace, path);
                }
                else
                {
                    await AnalyzeProject(workspace, path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during analysis: {ex.Message}");
            }
        }
    }

    private static async Task AnalyzeSolution(MSBuildWorkspace workspace, string solutionPath)
    {
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        Console.WriteLine($"Solution loaded successfully. Projects: {solution.Projects.Count()}");
        Console.WriteLine("----------------------------------------------------");

        bool anyIssuesFound = false;

        foreach (var project in solution.Projects)
        {
            var (hasErrors, hasWarnings) = await AnalyzeProjectCompilation(project);
            if (hasErrors || hasWarnings)
            {
                anyIssuesFound = true;
            }
        }

        PrintFinalReport(anyIssuesFound);
    }

    private static async Task AnalyzeProject(MSBuildWorkspace workspace, string projectPath)
    {
        var project = await workspace.OpenProjectAsync(projectPath);
        Console.WriteLine($"Project loaded successfully: {project.Name}");
        Console.WriteLine("----------------------------------------------------");
        
        var (hasErrors, hasWarnings) = await AnalyzeProjectCompilation(project);

        PrintFinalReport(hasErrors || hasWarnings);
    }

    private static async Task<(bool hasErrors, bool hasWarnings)> AnalyzeProjectCompilation(Project project)
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
                return filePath != null && (filePath.Contains("/plugins/") || filePath.Contains("\\plugins\\"));
            })
            .ToList();

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
                Console.WriteLine("\nDisplaying unprioritized issues:");
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
        Console.WriteLine("\n----------------------------------------------------");
        if (issuesFound)
        {
             Console.ForegroundColor = ConsoleColor.Red;
             Console.WriteLine("Analysis finished. Errors or warnings found.");
             Console.ResetColor();
        }
        else
        {
             Console.ForegroundColor = ConsoleColor.Green;
             Console.WriteLine("Analysis finished. No errors or warnings found.");
             Console.ResetColor();
        }
    }
}
