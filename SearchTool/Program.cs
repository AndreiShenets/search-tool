using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;

namespace SearchTool
{
    internal static class Program
    {
        private static void Main()
        {
            Logger logger;
            IConfiguration appConfiguration;
            IConfiguration searchConfiguration;

            try
            {
                appConfiguration = GetConfiguration();

                string configurationName = DisplayAndChooseConfiguration(appConfiguration);

                logger = SetupLogger(configurationName);

                searchConfiguration = GetSearchConfiguration(appConfiguration, configurationName);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.ReadLine();

                return;
            }

            try
            {
                Regex[] fileExcludePatterns = GetFileExcludePatterns(searchConfiguration);
                Regex[] fileIncludePatterns = GetFileIncludePatterns(searchConfiguration);
                Regex[] excludePatterns = GetExcludePatterns(searchConfiguration);
                string[] itemsToSearch = GetItemsToSearch(searchConfiguration);
                MapResults mapResults = new MapResults();
                ICollection<ICollection<MapPattern>> mapChains = GetMapChains(searchConfiguration, mapResults);

                IEnumerable<string> files = Directory
                    .EnumerateFiles(appConfiguration["searchRoot"],
                        "*",
                        SearchOption.AllDirectories
                    )
                    .Where(file =>
                        fileExcludePatterns.All(pattern => !pattern.IsMatch(file))
                        || fileIncludePatterns.Any(pattern => pattern.IsMatch(file))
                    );

                int foundItemCount = 0;
                FileCounter fileCounter = new FileCounter();

                foreach (string file in files)
                {
                    FileNameWriter fileNameWriter = new FileNameWriter(file, logger, fileCounter);

                    bool somethingReplaced = false;

                    string[] fileLines = File.ReadAllLines(file);

                    for (int lineIndex = 0; lineIndex < fileLines.Length; lineIndex++)
                    {
                        string fileLine = fileLines[lineIndex];

                        string currentLine = fileLine;

                        if (string.IsNullOrEmpty(currentLine))
                        {
                            continue;
                        }

                        if (itemsToSearch
                            .All(itemToSearch => !currentLine.Contains(itemToSearch, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        for (int index = 0; index < excludePatterns.Length; index++)
                        {
                            Regex excludePattern = excludePatterns[index];
                            currentLine = excludePattern.Replace(currentLine, string.Empty);
                        }

                        for (int index = 0; index < excludePatterns.Length; index++)
                        {
                            Regex excludePattern = excludePatterns[index];
                            currentLine = excludePattern.Replace(currentLine, string.Empty);
                        }

                        if (itemsToSearch
                            .All(itemToSearch => !currentLine.Contains(itemToSearch, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        fileNameWriter.Write();
                        logger.Information($"    {lineIndex} {fileLine}");

                        ++foundItemCount;

                        foreach (ICollection<MapPattern> mapChain in mapChains)
                        {
                            string processingLine = fileLine;
                            foreach (MapPattern mapPattern in mapChain)
                            {
                                (string outputLine, bool replaceOrigin) = mapPattern.Process(processingLine);

                                processingLine = outputLine;

                                if (replaceOrigin)
                                {
                                    logger.Information("Replaced with:");
                                    logger.Information($"    {lineIndex} {processingLine}");
                                    fileLines[lineIndex] = processingLine;
                                    somethingReplaced = true;
                                }
                            }
                        }
                    }

                    if (somethingReplaced)
                    {
                        File.WriteAllLines(file, fileLines);
                    }
                }

                logger.Information($"Found: {foundItemCount} items in {fileCounter.Count} files");

                foreach ((string FileName, ICollection<string> Values) mapResult
                    in mapResults
                        .GetFiles()
                        .Select(fileName => (FileName: fileName, Results: mapResults.GetResults(fileName)))
                )
                {
                    ICollection<string> collection = mapResult.Values;

                    if (!collection.Any())
                    {
                        continue;
                    }

                    logger.Information(string.Empty);
                    logger.Information($"Results for output '{mapResult.FileName}':");
                    foreach (string item in collection)
                    {
                        logger.Information(item);
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e.ToString());
                Console.ReadLine();
            }

            logger.Information(string.Empty);
            logger.Information("Done");
        }

        private static Regex[] GetExcludePatterns(IConfiguration configuration)
        {
            return GetPatternFromFiles(configuration, "excludePatternConfigurations")
                .Concat(GetPatternsFromConfiguration(configuration, "excludePatterns"))
                .ToArray();
        }

        private static ICollection<ICollection<MapPattern>> GetMapChains(IConfiguration configuration, MapResults mapResults)
        {
            int mapFlowIndex = 0;
            bool mapFlowFailed = false;

            ICollection<ICollection<MapPattern>> results = new List<ICollection<MapPattern>>();

            while (!mapFlowFailed)
            {
                bool mapPatternFailed = false;
                mapFlowFailed = true;
                int mapPatternIndex = 0;
                List<MapPattern> currentList = null;

                while (!mapPatternFailed)
                {
                    string patternFilesKey = $"mapConfigurations:{mapFlowIndex}:{mapPatternIndex}:patternFiles";
                    string patternsKey = $"mapConfigurations:{mapFlowIndex}:{mapPatternIndex}:patterns";

                    string[] patternFiles = configuration.GetSection(patternFilesKey).Get<string[]>();
                    string[] patterns = configuration.GetSection(patternsKey).Get<string[]>();

                    mapPatternFailed = patternFiles == null && patterns == null;

                    if (!mapPatternFailed)
                    {
                        if (currentList == null)
                        {
                            currentList = new List<MapPattern>();
                            results.Add(currentList);
                        }
                        mapFlowFailed = false;
                    }

                    if (patternFiles != null)
                    {
                        IEnumerable<MapPattern> mapPatterns = patternFiles
                            .Select(File.ReadAllLines)
                            .Select(lines => new MapPattern(mapResults, lines));

                        currentList.AddRange(mapPatterns);
                    }

                    if (patterns != null)
                    {
                        currentList.Add(new MapPattern(mapResults, patterns));
                    }

                    ++mapPatternIndex;
                }

                ++mapFlowIndex;
            }

            return results;
        }

        private static string[] GetItemsToSearch(IConfiguration configuration)
        {
            return configuration.GetSection("searchWords").Get<string[]>();
        }

        private static Regex[] GetFileIncludePatterns(IConfiguration configuration)
        {
            return GetPatternFromFiles(configuration, "includeFileConfigurations")
                .Concat(GetPatternsFromConfiguration(configuration, "includeFiles"))
                .ToArray();
        }

        private static Regex[] GetFileExcludePatterns(IConfiguration configuration)
        {
            return GetPatternFromFiles(configuration, "excludeFileConfigurations")
                .Concat(GetPatternsFromConfiguration(configuration, "excludeFiles"))
                .ToArray();
        }

        private static IEnumerable<Regex> GetPatternFromFiles(IConfiguration configuration, string settingName)
        {
            return configuration
                .GetSection(settingName)
                .Get<string[]>()
                ?.SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase))
                ?? Array.Empty<Regex>();
        }

        private static IEnumerable<Regex> GetPatternsFromConfiguration(IConfiguration configuration, string settingName)
        {
            return configuration
                .GetSection(settingName)
                .Get<string[]>()
                ?.Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase))
                ?? Array.Empty<Regex>();
        }

        private static string DisplayAndChooseConfiguration(IConfiguration configuration)
        {
            IConfigurationSection[] configurations = configuration
                .GetSection("configurations")
                .GetChildren()
                .ToArray();

            Console.WriteLine("Choose configuration from:");
            for (int index = 0; index < configurations.Length; index++)
            {
                IConfigurationSection section = configurations[index];

                Console.WriteLine($"{index}. {section.Key}");
            }
            Console.WriteLine("Type index to choose or any invalid value to terminate.");

            string choice = Console.ReadLine();
            if (!int.TryParse(choice, out int chosenConfiguration)
                || chosenConfiguration < 0
                || chosenConfiguration >= configurations.Length
            )
            {
                throw new Exception($"Invalid configuration index '{choice}' choosen. Terminating...");
            }

            return configurations[chosenConfiguration].Key;
        }

        private static IConfiguration GetConfiguration()
        {
            return new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "appsettings.json"))
                .Build();
        }

        private static IConfiguration GetSearchConfiguration(IConfiguration appConfiguration, string configurationName)
        {
            string configurationKey = $"configurations:{configurationName}";

            object configurationNode = appConfiguration[configurationKey];

            if (configurationNode is string configurationFilePath)
            {
                if (!File.Exists(configurationFilePath))
                {
                    throw new FileNotFoundException(
                        $"Configuration file '{configurationFilePath}' does not exist"
                    );
                }

                return new ConfigurationBuilder()
                    .AddJsonFile(configurationFilePath)
                    .Build();
            }

            return appConfiguration.GetSection(configurationKey);
        }

        private static Logger SetupLogger(string configurationName)
        {
            const string LOGGER_MESSAGE_TEMPLATE = "{Message}{NewLine}{Exception}";

            string logFileName = $"{AppDomain.CurrentDomain.FriendlyName}.{configurationName}.output.log";

            if (File.Exists(logFileName))
            {
                File.WriteAllText(logFileName, string.Empty);
            }

            return new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console(outputTemplate: LOGGER_MESSAGE_TEMPLATE)
                .WriteTo.File(
                    logFileName,
                    outputTemplate: LOGGER_MESSAGE_TEMPLATE,
                    retainedFileCountLimit: 1,
                    encoding: Encoding.UTF8
                )
                .CreateLogger();
        }

        private struct FileNameWriter
        {
            private readonly string _fileName;
            private readonly Logger _logger;
            private readonly FileCounter _fileCounter;
            private bool _fileNameWritten;

            public FileNameWriter(string fileName, Logger logger, FileCounter fileCounter)
            {
                _fileName = fileName;
                _logger = logger;
                _fileCounter = fileCounter;
                _fileNameWritten = false;
            }

            public void Write()
            {
                if (!_fileNameWritten)
                {
                    _logger.Information(string.Empty);
                    _logger.Information($"File: {_fileName}");
                    _fileNameWritten = true;
                    _fileCounter.Increase();
                }
            }
        }

        private class FileCounter
        {
            public int Count { get; private set; }

            public void Increase()
            {
                Count += 1;
            }
        }

        private class MapResults
        {
            private readonly Dictionary<string, (ICollection<string> Values, string Options)> _container
                = new Dictionary<string, (ICollection<string> Values, string Options)>();

            public ICollection<string> InitializeForFile(string file, HashSet<string> options)
            {
                string joinedOptions =
                    string.Join(",", options.OrderBy(item => StringComparison.OrdinalIgnoreCase));

                if (!_container.TryGetValue(file, out (ICollection<string> Values, string Options) results))
                {

                    results = options.Contains("Distinct")
                        ? (
                            (ICollection<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            joinedOptions
                        )
                        : ( new List<string>(), joinedOptions);
                    _container.Add(file, results);

                    return results.Values;
                }

                string collectionOptions = results.Options;

                if (!string.Equals(collectionOptions, joinedOptions, StringComparison.Ordinal))
                {
                    throw new Exception($"Distinction option does not match for '{file}' map file");
                }

                return results.Values;
            }

            public IReadOnlyCollection<string> GetFiles()
            {
                return _container.Keys;
            }

            public ICollection<string> GetResults(string file)
            {
                (ICollection<string> values, string optionsStr) = _container[file];

                HashSet<string> options = optionsStr.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);

                values = options.Contains("Sort")
                    ? values.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray()
                    : values;

                return values;
            }
        }

        private class MapPattern
        {
            private readonly MapProcessor[] _processors;

            public MapPattern(MapResults results, IEnumerable<string> patterns)
            {
                _processors = patterns
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(pattern => new MapProcessor(results, pattern))
                    .ToArray();
            }

            public (string OutputLine, bool ReplaceOrigin) Process(string line)
            {
                if (!_processors.Any())
                {
                    return (line, false);
                }

                (string OutputLine, bool ReplaceOrigin) result = default;

                for (int index = 0; index < _processors.Length; index++)
                {
                    MapProcessor processor = _processors[index];
                    result = processor.Process(line);
                }

                return (result.OutputLine, result.ReplaceOrigin);
            }
        }

        private class MapProcessor
        {
            private static readonly Regex _groupSubstitution = new Regex(@"([^$][$](\d+)|^[$](\d+))");
            private static readonly Regex _groupDeescaping = new Regex(@"[$]{2}(\d+)");
            private static readonly Dictionary<int, Regex> _deescapingRegexCache = new Dictionary<int, Regex>();
            private static readonly Dictionary<int, Regex> _substitutionRegexCache = new Dictionary<int, Regex>();

            private readonly ICollection<string> _results;
            private readonly Regex _pattern;
            private readonly bool _replaceOrigin;
            private readonly string _replacement;
            private readonly MatchCollection _replacementGroupMatches;

            public MapProcessor(MapResults results, string pattern)
            {
                string[] patternParts = pattern.Split(" > ");

                if (patternParts.Length < 4)
                {
                    throw new ArgumentException($"Invalid pattern '{pattern}'");
                }

                _pattern = new Regex(Unescape(patternParts[0]), RegexOptions.IgnoreCase);

                HashSet<string> options = patternParts[3]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _replacement = Unescape(patternParts[1]);
                _replacementGroupMatches = _groupSubstitution.Matches(_replacement);

                string outputFile = patternParts[2];
                if (!string.IsNullOrEmpty(outputFile))
                {
                    _results = results.InitializeForFile(outputFile, options);
                }

                _replaceOrigin = options.Contains("ReplaceOrigin");
            }

            public (string OutputLine, bool ReplaceOrigin) Process(string line)
            {
                Match match = _pattern.Match(line);
                string outputLine = line;

                if (match.Success)
                {
                    string replacement = _replacement;

                    if (_replacementGroupMatches.Any())
                    {
                        for (int index = 0; index < _replacementGroupMatches.Count; index++)
                        {
                            Match replacementGroupMatch = _replacementGroupMatches[index];

                            int groupIndex = int.Parse(
                                string.IsNullOrEmpty(replacementGroupMatch.Groups[2].Value)
                                    ? replacementGroupMatch.Groups[3].Value
                                    : replacementGroupMatch.Groups[2].Value
                            );

                            Regex regex = GetSubstitutionRegex(groupIndex);

                            replacement = regex.Replace(replacement, match.Groups[groupIndex].Value);
                        }

                        MatchCollection deescapingMatches = _groupDeescaping.Matches(replacement);

                        if (deescapingMatches.Any())
                        {
                            for (int index = 0; index < deescapingMatches.Count; index++)
                            {
                                Match deescapingMatch = deescapingMatches[index];
                                int groupIndex = int.Parse(deescapingMatch.Groups[1].Value);

                                Regex regex = GetDeescapingRegex(groupIndex);

                                replacement = regex.Replace(replacement, $"${groupIndex}");
                            }
                        }
                    }

                    outputLine = replacement;

                    if (_results != null)
                    {
                        _results.Add(outputLine);
                    }
                }

                return (outputLine, _replaceOrigin && match.Success);
            }

            private static Regex GetDeescapingRegex(int groupIndex)
            {
                if (!_deescapingRegexCache.TryGetValue(groupIndex, out Regex result))
                {
                    result = new Regex($@"[$]{2}{groupIndex}");
                    _deescapingRegexCache.Add(groupIndex, result);
                }

                return result;
            }

            private static Regex GetSubstitutionRegex(int groupIndex)
            {
                if (!_substitutionRegexCache.TryGetValue(groupIndex, out Regex result))
                {
                    result = new Regex($@"([^$][$]{groupIndex}|^[$]{groupIndex})");
                    _substitutionRegexCache.Add(groupIndex, result);
                }

                return result;
            }

            private static string Unescape(string str)
            {
                return str.Replace("&gt;", ">");
            }
        }
    }
}
