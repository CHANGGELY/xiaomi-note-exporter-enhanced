using System.Drawing;
using System.Reflection;
using System.Text;
using OpenQA.Selenium.Chrome;
using Pastel;

using xiaomiNoteExporter.Extensions;

namespace xiaomiNoteExporter;

class Program
{
    public static Version? appVersion = Assembly.GetExecutingAssembly().GetName().Version;

    static string baseDir = AppContext.BaseDirectory;
    static string? outputDir = null;

    static bool _shouldAskForDomain = true;
    public static string defaultDomain = "us.i.mi.com";

    static bool _shouldSplit = false;
    static string _timestampFormat = "dd-MM-yyyy_HH-mm-ss";

    static bool _disableImages = false;

    static bool _useStaticDriver = false;
    static bool _driverOnly = false;

    static bool isConvertingJson = false;
    static string convertPath = string.Empty;

    static bool isVerifying = false;
    static string verifyPath = string.Empty;
    static int? verifyExpectedTotal = null;

    static bool isNormalizing = false;
    static string normalizePath = string.Empty;

    static bool isReclaimingDuplicates = false;
    static string reclaimDuplicatesPath = string.Empty;

    static ChromeDriver? driver;

    static void ShutdownHandler_Handler()
    {
        var toClose = driver;
        driver = null;
        if (toClose is not null)
        {
            try
            {
                toClose.Close();
            }
            catch
            {
                // ignore
            }

            try
            {
                toClose.Quit();
            }
            catch
            {
                // ignore
            }

            try
            {
                toClose.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (NormalizeArgToken(args[0]).Includes("-h", "--help"))
            {
                ShowHelp();
                return;
            }
            else
            {
                ParseArgs(args);
            }
        }

        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            baseDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(baseDir);
        }

        if (isVerifying)
        {
            TrySetConsoleTitle($"Xiaomi Note Exporter {appVersion?.GetVersionString()} - Verify");
            Environment.Exit(ExportVerifier.Verify(ResolvePath(verifyPath), verifyExpectedTotal));
        }

        if (isNormalizing)
        {
            TrySetConsoleTitle($"Xiaomi Note Exporter {appVersion?.GetVersionString()} - Normalize");
            Environment.Exit(ExportDirectoryNormalizer.Normalize(ResolvePath(normalizePath), _timestampFormat, verifyExpectedTotal));
        }

        if (isReclaimingDuplicates)
        {
            TrySetConsoleTitle($"Xiaomi Note Exporter {appVersion?.GetVersionString()} - Reclaim Duplicates");
            Environment.Exit(ExportDirectoryNormalizer.ReclaimDuplicates(ResolvePath(reclaimDuplicatesPath), _timestampFormat, verifyExpectedTotal));
        }

        if (isConvertingJson)
        {
            TrySetConsoleTitle($"Xiaomi Note Exporter {appVersion?.GetVersionString()} - Converting to JSON");

            try
            {
                new JsonConverter(ResolvePath(convertPath), baseDir).Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} An error occurred during JSON conversion: {ex.Message}");
            }
        }
        else
        {
            driver = new Driver(Array.Empty<string>(), baseDir).Prepare(_useStaticDriver); // prepare driver after parsing arguments from command line

            TrySetConsoleTitle($"Xiaomi Note Exporter {appVersion?.GetVersionString()}");

            Console.WriteLine(
                $"{"Xiaomi Note Exporter".Pastel(Color.FromArgb(252, 106, 0))} - Export your notes to {"Markdown".Pastel(Color.SkyBlue)}!\n"
                );

            Console.CancelKeyPress += (_, __) => ShutdownHandler_Handler();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => ShutdownHandler_Handler();

            try
            {
                if (_driverOnly)
                {
                    Console.WriteLine("ChromeDriver started successfully.");
                    return;
                }

                string? domain = _shouldAskForDomain
                    ? new Prompt(
                        $"{"[OPTIONAL]".Pastel(Color.DimGray)} Input Mi Notes domain that you were redirected to (default \"{defaultDomain}\"):",
                        defaultDomain
                        ).Ask()
                    : defaultDomain;

                new Scraper(driver, ShutdownHandler_Handler, baseDir).Start(domain, _timestampFormat, _shouldSplit, !_disableImages);
            }
            finally
            {
                ShutdownHandler_Handler();
            }
        }
    }

    private static void TrySetConsoleTitle(string title)
    {
        try
        {
            Console.Title = title ?? string.Empty;
        }
        catch
        {
        }
    }

    private static void ShowHelp() => new ConsoleHelp(appVersion).Print();

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = NormalizeArgToken(args[i]);

            if (ArgIs(arg, "-j", "--json"))
            {
                if (TryGetArgValue(args, i, out var path) && !string.IsNullOrEmpty(path))
                {
                    isConvertingJson = true;
                    convertPath = path;
                    i++;
                    continue;
                } 
                else
                {
                    Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path is required for json convert flag.");
                    Environment.Exit(1);
                }
            }
            else if (ArgIs(arg, "-v", "--verify"))
            {
                if (TryGetArgValue(args, i, out var path) && !string.IsNullOrEmpty(path))
                {
                    isVerifying = true;
                    verifyPath = path;
                    i++;
                    continue;
                }

                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path is required for verify flag.");
                Environment.Exit(1);
            }
            else if (ArgIs(arg, "-e", "--expected", "--expected-count", "--expected-total"))
            {
                if (TryGetArgValue(args, i, out var countText) && !string.IsNullOrEmpty(countText) && int.TryParse(countText, out var count) && count >= 0)
                {
                    verifyExpectedTotal = count;
                    i++;
                    continue;
                }

                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Expected count must be a non-negative integer.");
                Environment.Exit(1);
            }
            else if (ArgIs(arg, "-o", "--output", "--output-dir", "--out"))
            {
                if (TryGetArgValue(args, i, out var outPath) && !string.IsNullOrEmpty(outPath))
                {
                    outputDir = outPath;
                    i++;
                    continue;
                }

                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path is required for output flag.");
                Environment.Exit(1);
            }
            else if (ArgIs(arg, "-n", "--normalize"))
            {
                if (TryGetArgValue(args, i, out var path) && !string.IsNullOrEmpty(path))
                {
                    isNormalizing = true;
                    normalizePath = path;
                    i++;
                    continue;
                }

                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path is required for normalize flag.");
                Environment.Exit(1);
            }
            else if (ArgIs(arg, "--reclaim-duplicates", "--reclaim-dupes"))
            {
                if (TryGetArgValue(args, i, out var path) && !string.IsNullOrEmpty(path))
                {
                    isReclaimingDuplicates = true;
                    reclaimDuplicatesPath = path;
                    i++;
                    continue;
                }

                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path is required for reclaim-duplicates flag.");
                Environment.Exit(1);
            }
            else if (ArgIs(arg, "-d", "--domain"))
            {
                if (TryGetArgValue(args, i, out var domain) && !string.IsNullOrEmpty(domain))
                {
                    defaultDomain = domain; // set global domain to the provided value
                    _shouldAskForDomain = false; // shouldn't ask for domain, since it was provided as argument

                    i++; // skip next argument - this was a value
                    continue;
                }
                else
                {
                    Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Domain address is required with domain flag.");
                    Environment.Exit(1);
                }
            }
            else if (ArgIs(arg, "-s", "--split"))
            {
                if (TryGetArgValue(args, i, out var timestampFormat) && !string.IsNullOrEmpty(timestampFormat))
                {
                    try
                    {
                        DateTime.Now.ToString(timestampFormat);
                    }
                    catch (FormatException)
                    {
                        Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Invalid timestamp format.");
                        Environment.Exit(1);
                    }

                    _timestampFormat = timestampFormat;

                    i++; // skip next argument - this was a value
                }

                _shouldSplit = true; // if flag is present, split is enabled (even if no value is provided)
            }
            else if (ArgIs(arg, "-md", "--manual-driver"))
            {
                _useStaticDriver = true;
            }
            else if (ArgIs(arg, "-di", "--disable-images"))
            {
                _disableImages = true;
            }
            else if (ArgIs(arg, "--driver-only", "--check-driver"))
            {
                _driverOnly = true;
            }
        }
    }

    private static bool TryGetArgValue(string[] args, int index, out string value)
    {
        if (index + 1 < args.Length)
        {
            var next = args[index + 1];
            if (LooksLikeFlagToken(next))
            {
                value = string.Empty;
                return false;
            }

            value = next;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string NormalizeArgToken(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        return s.Trim()
            .Replace('—', '-')
            .Replace('–', '-')
            .Replace('−', '-')
            .Replace('－', '-')
            .Replace('‑', '-');
    }

    private static bool ArgIs(string arg, params string[] values)
    {
        foreach (var v in values)
        {
            if (string.Equals(arg, v, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeFlagToken(string s)
    {
        var t = NormalizeArgToken(s);
        return t.Length >= 2 && t[0] == '-';
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(baseDir, path);
    }

    private static string NormalizeNewlines(string s) => (s ?? string.Empty).Replace("\r\n", "\n");

    private static string BuildRawHashKey(string raw) => "hash:" + ComputeSha256Hex(NormalizeNewlines(raw));

    private static string BuildStructuredHashKey(DateTime createdDate, string title, string content)
    {
        var payload = $"{createdDate:O}\n{NormalizeNewlines(title)}\n{NormalizeNewlines(content)}";
        return "hash:" + ComputeSha256Hex(payload);
    }

    private static string ComputeSha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static class ExportVerifier
    {
        public static int Verify(string path, int? expectedTotal)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Verify path is empty.");
                return 1;
            }

            if (Directory.Exists(path))
            {
                return VerifySplitDirectory(path, expectedTotal);
            }

            if (File.Exists(path))
            {
                return VerifyMarkdownFile(path, expectedTotal);
            }

            Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path not found: {path}");
            return 1;
        }

        private static int VerifySplitDirectory(string dir, int? expectedTotal)
        {
            var (manifestPath, legacyName) = FindManifestPath(
                Path.Combine(dir, "manifest.txt"),
                Path.Combine(dir, "manifest.json")
                );
            var noteFiles = Directory.EnumerateFiles(dir, "note_*", SearchOption.TopDirectoryOnly).ToList();

            var (exportedInManifest, unsupportedInManifest) = TryReadManifestCounts(manifestPath);
            var expectedFromManifest = TryReadManifestExpectedTotal(manifestPath);
            if (expectedTotal is null && expectedFromManifest >= 0)
            {
                expectedTotal = expectedFromManifest;
            }
            var parsedCount = ParseNotesFromFiles(noteFiles);

            Console.WriteLine($"{"[VERIFY]".Pastel(Color.Cyan)} Directory: {dir}");
            Console.WriteLine($"Note files: {noteFiles.Count}");
            Console.WriteLine($"Parsed notes from files: {parsedCount.Total} (exported: {parsedCount.Exported}, unsupported: {parsedCount.Unsupported})");
            Console.WriteLine($"Unique notes (by content hash): {parsedCount.Unique} (duplicates: {parsedCount.Duplicate})");
            if (expectedTotal is not null)
            {
                var diff = parsedCount.Total - expectedTotal.Value;
                Console.WriteLine($"Expected total: {expectedTotal.Value} (diff: {diff})");
            }

            if (exportedInManifest >= 0)
            {
                Console.WriteLine($"Manifest: exported {exportedInManifest}, unsupported {unsupportedInManifest}");
                if (exportedInManifest + unsupportedInManifest != parsedCount.Total)
                {
                    Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Manifest count != parsed count. Consider re-run export once to regenerate manifest.");
                    return 2;
                }

                if (expectedTotal is not null && !MatchesExpected(parsedCount, expectedTotal.Value))
                {
                    Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Export total != expected total.");
                    return 2;
                }

                return 0;
            }

            if (expectedTotal is not null && !MatchesExpected(parsedCount, expectedTotal.Value))
            {
                Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Export total != expected total.");
                return 2;
            }

            var missingName = legacyName ? "manifest.json" : "manifest.txt";
            Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} {missingName} not found in this directory. Export run will auto-build it from existing notes.");
            return 0;
        }

        private static int VerifyMarkdownFile(string mdFile, int? expectedTotal)
        {
            if (!mdFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Verify file must be a .md file.");
                return 1;
            }

            var manifestPreferred = Path.Combine(
                Path.GetDirectoryName(mdFile)!,
                $"{Path.GetFileNameWithoutExtension(mdFile)}.manifest.txt"
            );
            var manifestLegacy = Path.Combine(
                Path.GetDirectoryName(mdFile)!,
                $"{Path.GetFileNameWithoutExtension(mdFile)}.manifest.json"
            );
            var (manifestPath, _) = FindManifestPath(manifestPreferred, manifestLegacy);

            var text = File.ReadAllText(mdFile, Encoding.UTF8);
            var parsed = ParseNotesFromText(text);
            var (exportedInManifest, unsupportedInManifest) = TryReadManifestCounts(manifestPath);
            var expectedFromManifest = TryReadManifestExpectedTotal(manifestPath);
            if (expectedTotal is null && expectedFromManifest >= 0)
            {
                expectedTotal = expectedFromManifest;
            }

            Console.WriteLine($"{"[VERIFY]".Pastel(Color.Cyan)} File: {mdFile}");
            Console.WriteLine($"Parsed notes: {parsed.Total} (exported: {parsed.Exported}, unsupported: {parsed.Unsupported})");
            Console.WriteLine($"Unique notes (by content hash): {parsed.Unique} (duplicates: {parsed.Duplicate})");
            if (expectedTotal is not null)
            {
                var diff = parsed.Total - expectedTotal.Value;
                Console.WriteLine($"Expected total: {expectedTotal.Value} (diff: {diff})");
            }

            if (exportedInManifest >= 0)
            {
                Console.WriteLine($"Manifest: exported {exportedInManifest}, unsupported {unsupportedInManifest}");
                if (exportedInManifest + unsupportedInManifest != parsed.Total)
                {
                    Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Manifest count != parsed count. Consider re-run export once to regenerate manifest.");
                    return 2;
                }

                if (expectedTotal is not null && !MatchesExpected(parsed, expectedTotal.Value))
                {
                    Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Export total != expected total.");
                    return 2;
                }

                return 0;
            }

            if (expectedTotal is not null && !MatchesExpected(parsed, expectedTotal.Value))
            {
                Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Export total != expected total.");
                return 2;
            }

            Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} Manifest file not found: {manifestPath}");
            return 0;
        }

        private static (string ManifestPath, bool LegacyName) FindManifestPath(string preferred, string legacy)
        {
            if (File.Exists(preferred))
            {
                return (preferred, false);
            }

            if (File.Exists(legacy))
            {
                return (legacy, true);
            }

            return (preferred, false);
        }

        private static (int Exported, int Unsupported) TryReadManifestCounts(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return (-1, -1);
                }

                var lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
                int exported = 0;
                int unsupported = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('|');
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    var kind = parts[0].Trim();
                    if (kind.Equals("exported", StringComparison.OrdinalIgnoreCase))
                    {
                        exported++;
                    }
                    else if (kind.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
                    {
                        unsupported++;
                    }
                }

                return (exported, unsupported);
            }
            catch
            {
                return (-1, -1);
            }
        }

        private static int TryReadManifestExpectedTotal(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return -1;
                }

                var lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('|');
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    if (!parts[0].Trim().Equals("total", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return int.TryParse(parts[1].Trim(), out var total) ? total : -1;
                }

                return -1;
            }
            catch
            {
                return -1;
            }
        }

        private static bool MatchesExpected((int Total, int Exported, int Unsupported, int Unique, int Duplicate) parsed, int expected)
        {
            if (parsed.Total == expected)
            {
                return true;
            }

            if (parsed.Duplicate > 0 && parsed.Unique == expected)
            {
                return true;
            }

            return false;
        }

        private static (int Total, int Exported, int Unsupported, int Unique, int Duplicate) ParseNotesFromFiles(IEnumerable<string> files)
        {
            int total = 0;
            int exported = 0;
            int unsupported = 0;
            int unique = 0;
            int duplicate = 0;
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var parsed = ParseNotesFromText(text, keys);
                total += parsed.Total;
                exported += parsed.Exported;
                unsupported += parsed.Unsupported;
                unique += parsed.Unique;
                duplicate += parsed.Duplicate;
            }

            return (total, exported, unsupported, unique, duplicate);
        }

        private static (int Total, int Exported, int Unsupported, int Unique, int Duplicate) ParseNotesFromText(string text)
        {
            return ParseNotesFromText(text, new HashSet<string>(StringComparer.Ordinal));
        }

        private static (int Total, int Exported, int Unsupported, int Unique, int Duplicate) ParseNotesFromText(string text, HashSet<string> knownKeys)
        {
            int total = 0;
            int exported = 0;
            int unsupported = 0;
            int unique = 0;
            int duplicate = 0;

            var current = new List<string>();
            bool anyBlock = false;

            void ConsumeCurrent()
            {
                if (current.Count == 0)
                {
                    return;
                }

                var raw = string.Join("\n", current);

                if (!TryParseBlock(current, out var kind, out var createdDate, out var title, out var content))
                {
                    current.Clear();
                    return;
                }

                anyBlock = true;
                total++;
                if (kind.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    unsupported++;
                }
                else
                {
                    exported++;
                }

                var key = Program.BuildStructuredHashKey(createdDate, title ?? string.Empty, content ?? string.Empty);
                if (!knownKeys.Add(key))
                {
                    duplicate++;
                }
                else
                {
                    unique++;
                }

                current.Clear();
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                if (line.Trim() == "****")
                {
                    ConsumeCurrent();
                    current.Add("****");
                    continue;
                }

                if (current.Count > 0)
                {
                    current.Add(line);
                }
            }

            ConsumeCurrent();

            if (!anyBlock && !string.IsNullOrWhiteSpace(text))
            {
                total++;
                exported++;
                var key = "legacy:" + Program.ComputeSha256Hex(Program.NormalizeNewlines(text));
                if (!knownKeys.Add(key))
                {
                    duplicate++;
                }
                else
                {
                    unique++;
                }
            }

            return (total, exported, unsupported, unique, duplicate);
        }

        private static bool TryParseBlock(IReadOnlyList<string> lines, out string kind, out DateTime createdDate, out string? title, out string? content)
        {
            kind = "exported";
            createdDate = default;
            title = null;
            content = null;

            if (lines.Count == 0 || lines[0].Trim() != "****")
            {
                return false;
            }

            int createdAtIndex = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].StartsWith("*Created at:", StringComparison.OrdinalIgnoreCase))
                {
                    createdAtIndex = i;
                    break;
                }
            }

            if (createdAtIndex < 0)
            {
                return false;
            }

            var createdLine = lines[createdAtIndex];
            var createdText = createdLine.Trim().Trim('*');
            var createdValue = createdText.Substring("Created at:".Length).Trim();
            if (!DateTime.TryParseExact(createdValue, "dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out createdDate))
            {
                return false;
            }

            int idx = 1;
            if (idx < createdAtIndex && lines[idx].StartsWith("## ", StringComparison.Ordinal))
            {
                title = lines[idx].Substring(3);
                idx++;
            }

            var contentLines = new List<string>();
            for (int i = idx; i < createdAtIndex; i++)
            {
                contentLines.Add(lines[i]);
            }

            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
            {
                contentLines.RemoveAt(contentLines.Count - 1);
            }

            var restoredContentLines = contentLines.Select(l => l.EndsWith("  ") ? l.Substring(0, l.Length - 2) : l);
            content = string.Join("\n", restoredContentLines);

            kind = content.TrimStart().StartsWith("**Unsupported note type", StringComparison.OrdinalIgnoreCase)
                ? "unsupported"
                : "exported";

            return true;
        }

        private static string BuildHashKey(DateTime createdDate, string title, string content) => Program.BuildStructuredHashKey(createdDate, title, content);

        private static string NormalizeNewlines(string s) => (s ?? string.Empty).Replace("\r\n", "\n");
    }

    private static class ExportDirectoryNormalizer
    {
        private readonly record struct NoteBlock(string Kind, DateTime CreatedDate, string? Title, string Content, string Raw);

        public static int Normalize(string dir, string timeStampFormat, int? expectedTotal)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Normalize path is empty.");
                return 1;
            }

            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path not found: {dir}");
                return 1;
            }

            var duplicatesDir = Path.Combine(dir, "duplicates");
            var legacyDir = Path.Combine(dir, "legacy_originals");
            Directory.CreateDirectory(duplicatesDir);
            Directory.CreateDirectory(legacyDir);

            var noteFiles = Directory.EnumerateFiles(dir, "note_*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".manifest.txt", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<(string Key, string Kind, DateTime CreatedDate, string? Title)>();

            int movedToDuplicates = 0;
            int splitFiles = 0;
            int renamedToMd = 0;
            int createdFiles = 0;

            foreach (var file in noteFiles)
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var blocks = ExtractBlocks(text);

                if (blocks.Count <= 1)
                {
                    if (blocks.Count == 0)
                    {
                        var key = "legacy:" + Program.ComputeSha256Hex(Program.NormalizeNewlines(text));
                        if (!keys.Add(key))
                        {
                            MoveToFolder(file, duplicatesDir);
                            movedToDuplicates++;
                            continue;
                        }

                        var target = EnsureMdExtension(file);
                        if (!string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
                        {
                            SafeMove(file, target);
                            renamedToMd++;
                        }

                        var createdDate = File.GetLastWriteTime(target);
                        entries.Add((key, "exported", createdDate, null));
                        continue;
                    }

                    var b = blocks[0];
                    var key2 = Program.BuildStructuredHashKey(b.CreatedDate, b.Title ?? string.Empty, b.Content ?? string.Empty);
                    if (!keys.Add(key2))
                    {
                        MoveToFolder(file, duplicatesDir);
                        movedToDuplicates++;
                        continue;
                    }

                    var desiredName = GetAvailableNotePath(dir, timeStampFormat, b.CreatedDate, key2);
                    var desiredFull = Path.Combine(dir, desiredName);
                    if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(desiredFull), StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = desiredFull;
                        if (!File.Exists(target))
                        {
                            WriteSingleBlock(target, b.Raw);
                            createdFiles++;
                            SafeMove(file, Path.Combine(legacyDir, Path.GetFileName(file)));
                        }
                        else
                        {
                            SafeMove(file, Path.Combine(legacyDir, Path.GetFileName(file)));
                        }
                    }
                    else
                    {
                        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        {
                            var target = EnsureMdExtension(file);
                            SafeMove(file, target);
                            renamedToMd++;
                        }
                    }

                    entries.Add((key2, b.Kind, b.CreatedDate, b.Title));
                    continue;
                }

                splitFiles++;
                int blockIndex = 0;
                foreach (var b in blocks)
                {
                    blockIndex++;
                    var key3 = Program.BuildStructuredHashKey(b.CreatedDate, b.Title ?? string.Empty, b.Content ?? string.Empty);
                    if (!keys.Add(key3))
                    {
                        var dupTarget = Path.Combine(duplicatesDir, $"{Path.GetFileName(file)}_{blockIndex}.md");
                        WriteSingleBlock(dupTarget, b.Raw);
                        movedToDuplicates++;
                        createdFiles++;
                        continue;
                    }

                    var desiredName = GetAvailableNotePath(dir, timeStampFormat, b.CreatedDate, key3);
                    var desiredFull = Path.Combine(dir, desiredName);
                    WriteSingleBlock(desiredFull, b.Raw);
                    createdFiles++;
                    entries.Add((key3, b.Kind, b.CreatedDate, b.Title));
                }

                SafeMove(file, Path.Combine(legacyDir, Path.GetFileName(file)));
            }

            var manifestPath = Path.Combine(dir, "manifest.txt");
            WriteManifest(manifestPath, expectedTotal, entries);

            Console.WriteLine($"{"[NORMALIZE]".Pastel(Color.Cyan)} Directory: {dir}");
            Console.WriteLine($"Input note_* files: {noteFiles.Count}");
            Console.WriteLine($"Unique blocks: {keys.Count}");
            Console.WriteLine($"Created files: {createdFiles}, Split source files: {splitFiles}, Renamed to .md: {renamedToMd}, Moved to duplicates: {movedToDuplicates}");
            return 0;
        }

        public static int ReclaimDuplicates(string dir, string timeStampFormat, int? expectedTotal)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Reclaim path is empty.");
                return 1;
            }

            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"{"[ERROR]".Pastel(Color.Red)} Path not found: {dir}");
                return 1;
            }

            var duplicatesDir = Path.Combine(dir, "duplicates");
            if (!Directory.Exists(duplicatesDir))
            {
                Console.WriteLine($"{"[WARN]".Pastel(Color.Yellow)} duplicates folder not found: {duplicatesDir}");
                return 0;
            }

            var existingNoteFiles = Directory.EnumerateFiles(dir, "note_*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".manifest.txt", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in existingNoteFiles)
            {
                try
                {
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    var blocks = ExtractBlocks(text);
                    if (blocks.Count == 1)
                    {
                        var b = blocks[0];
                        keys.Add(Program.BuildStructuredHashKey(b.CreatedDate, b.Title ?? string.Empty, b.Content ?? string.Empty));
                    }
                    else if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(text))
                    {
                        keys.Add("legacy:" + Program.ComputeSha256Hex(Program.NormalizeNewlines(text)));
                    }
                }
                catch
                {
                }
            }

            int restored = 0;
            var dupFiles = Directory.EnumerateFiles(duplicatesDir, "*", SearchOption.TopDirectoryOnly).ToList();
            foreach (var file in dupFiles)
            {
                try
                {
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    var blocks = ExtractBlocks(text);
                    if (blocks.Count == 0)
                    {
                        continue;
                    }

                    foreach (var b in blocks)
                    {
                        var key = Program.BuildStructuredHashKey(b.CreatedDate, b.Title ?? string.Empty, b.Content ?? string.Empty);
                        if (!keys.Add(key))
                        {
                            continue;
                        }

                        var desiredName = GetAvailableNotePath(dir, timeStampFormat, b.CreatedDate, key);
                        var desiredFull = Path.Combine(dir, desiredName);
                        WriteSingleBlock(desiredFull, b.Raw);
                        restored++;
                        if (expectedTotal is not null && existingNoteFiles.Count + restored >= expectedTotal.Value)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                }

                if (expectedTotal is not null && existingNoteFiles.Count + restored >= expectedTotal.Value)
                {
                    break;
                }
            }

            Console.WriteLine($"{"[RECLAIM]".Pastel(Color.Cyan)} Directory: {dir}");
            Console.WriteLine($"Existing note_* files: {existingNoteFiles.Count}");
            Console.WriteLine($"Restored from duplicates: {restored}");
            Console.WriteLine($"Total note_* files now: {existingNoteFiles.Count + restored}");

            return 0;
        }

        private static string EnsureMdExtension(string path)
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return path + ".md";
        }

        private static void SafeMove(string from, string to)
        {
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                var baseName = Path.GetFileNameWithoutExtension(to);
                var ext = Path.GetExtension(to);
                var dir = Path.GetDirectoryName(to)!;
                for (int i = 2; i < 1000; i++)
                {
                    var alt = Path.Combine(dir, $"{baseName}_{i}{ext}");
                    if (!File.Exists(alt))
                    {
                        to = alt;
                        break;
                    }
                }
            }

            File.Move(from, to);
        }

        private static void MoveToFolder(string file, string folder)
        {
            var name = Path.GetFileName(file);
            SafeMove(file, Path.Combine(folder, name));
        }

        private static void WriteSingleBlock(string path, string raw)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NormalizeNewlines(raw).TrimEnd() + "\n", Encoding.UTF8);
        }

        private static string GetAvailableNotePath(string dir, string timeStampFormat, DateTime createdDate, string noteKey)
        {
            var baseName = $"note_{createdDate.ToString(timeStampFormat)}";
            var candidate = baseName + ".md";
            var full = Path.Combine(dir, candidate);
            if (!File.Exists(full))
            {
                return candidate;
            }

            var suffix = KeySuffix(noteKey);
            var candidate2 = $"{baseName}_{suffix}.md";
            var full2 = Path.Combine(dir, candidate2);
            if (!File.Exists(full2))
            {
                return candidate2;
            }

            for (int i = 2; i < 1000; i++)
            {
                var candidate3 = $"{baseName}_{suffix}_{i}.md";
                var full3 = Path.Combine(dir, candidate3);
                if (!File.Exists(full3))
                {
                    return candidate3;
                }
            }

            return candidate2;
        }

        private static List<NoteBlock> ExtractBlocks(string text)
        {
            var lines = NormalizeNewlines(text).Split('\n');
            var current = new List<string>();
            var blocks = new List<NoteBlock>();

            void Consume()
            {
                if (current.Count == 0)
                {
                    return;
                }

                if (TryParseBlock(current, out var kind, out var createdDate, out var title, out var content))
                {
                    var raw = string.Join("\n", current);
                    blocks.Add(new NoteBlock(kind, createdDate, title, content ?? string.Empty, raw));
                }

                current.Clear();
            }

            foreach (var line in lines)
            {
                if (line.Trim() == "****")
                {
                    Consume();
                    current.Add("****");
                    continue;
                }

                if (current.Count > 0)
                {
                    current.Add(line);
                }
            }

            Consume();
            return blocks;
        }

        private static bool TryParseBlock(IReadOnlyList<string> lines, out string kind, out DateTime createdDate, out string? title, out string? content)
        {
            kind = "exported";
            createdDate = default;
            title = null;
            content = null;

            if (lines.Count == 0 || lines[0].Trim() != "****")
            {
                return false;
            }

            int createdAtIndex = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].StartsWith("*Created at:", StringComparison.OrdinalIgnoreCase))
                {
                    createdAtIndex = i;
                    break;
                }
            }

            if (createdAtIndex < 0)
            {
                return false;
            }

            var createdLine = lines[createdAtIndex];
            var createdText = createdLine.Trim().Trim('*');
            var createdValue = createdText.Substring("Created at:".Length).Trim();
            if (!DateTime.TryParseExact(createdValue, "dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out createdDate))
            {
                return false;
            }

            int idx = 1;
            if (idx < createdAtIndex && lines[idx].StartsWith("## ", StringComparison.Ordinal))
            {
                title = lines[idx].Substring(3);
                idx++;
            }

            var contentLines = new List<string>();
            for (int i = idx; i < createdAtIndex; i++)
            {
                contentLines.Add(lines[i]);
            }

            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
            {
                contentLines.RemoveAt(contentLines.Count - 1);
            }

            var restoredContentLines = contentLines.Select(l => l.EndsWith("  ") ? l.Substring(0, l.Length - 2) : l);
            content = string.Join("\n", restoredContentLines);

            kind = content.TrimStart().StartsWith("**Unsupported note type", StringComparison.OrdinalIgnoreCase)
                ? "unsupported"
                : "exported";

            return true;
        }

        private static void WriteManifest(string manifestPath, int? expectedTotal, List<(string Key, string Kind, DateTime CreatedDate, string? Title)> entries)
        {
            var lines = new List<string>(entries.Count + 2)
            {
                "v1"
            };

            if (expectedTotal is not null && expectedTotal.Value > 0)
            {
                lines.Add($"total|{expectedTotal.Value}");
            }

            foreach (var e in entries.OrderBy(e => e.CreatedDate))
            {
                var keyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Key));
                var titleBase64 = string.IsNullOrEmpty(e.Title) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Title));
                lines.Add($"{e.Kind}|{e.CreatedDate:O}|{keyBase64}|{titleBase64}");
            }

            File.WriteAllLines(manifestPath, lines, Encoding.UTF8);
        }

        private static string BuildHashKey(DateTime createdDate, string title, string content)
        {
            var payload = $"{createdDate:O}\n{NormalizeNewlines(title)}\n{NormalizeNewlines(content)}";
            return "hash:" + Program.ComputeSha256Hex(payload);
        }

        private static string KeySuffix(string key)
        {
            if (key.StartsWith("hash:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = key.Substring(5);
                return raw.Length <= 10 ? raw : raw.Substring(0, 10);
            }

            var cleanedFallback = new string(key.Where(char.IsLetterOrDigit).ToArray());
            return cleanedFallback.Length <= 10 ? cleanedFallback : cleanedFallback.Substring(0, 10);
        }

        private static string NormalizeNewlines(string s) => (s ?? string.Empty).Replace("\r\n", "\n");
    }
}
