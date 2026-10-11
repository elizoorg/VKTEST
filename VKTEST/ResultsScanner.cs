using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BmwBenchmarkAutomator;

/// Снимок «путь → (mtime, размер)» для поиска файлов, созданных/обновлённых бенчмарком.
public sealed class FileSnapshot
{
    private readonly Dictionary<string, (DateTime Utc, long Size)> _files;
    private FileSnapshot(Dictionary<string, (DateTime, long)> files) => _files = files;

    public static FileSnapshot Capture(IEnumerable<(string Dir, int Depth)> sources)
    {
        var files = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, depth) in sources) Walk(dir, depth, files);
        return new FileSnapshot(files);
    }

    private static void Walk(string dir, int depth, Dictionary<string, (DateTime, long)> files)
    {
        if (depth < 0 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { var fi = new FileInfo(f); files[fi.FullName] = (fi.LastWriteTimeUtc, fi.Length); }
                catch { }
            }
        }
        catch { }
        if (depth == 0) return;
        try { foreach (var s in Directory.EnumerateDirectories(dir)) Walk(s, depth - 1, files); }
        catch { }
    }

    public List<string> ChangedFiles(FileSnapshot baseline)
    {
        var changed = new List<string>();
        foreach (var (path, meta) in _files)
            if (!baseline._files.TryGetValue(path, out var old) || old.Utc != meta.Utc || old.Size != meta.Size)
                changed.Add(path);
        return changed;
    }
}

public sealed class Metric { public string Key { get; set; } = ""; public double Value { get; set; } }

public sealed class ParsedResultFile
{
    public string Path { get; set; } = "";
    public List<Metric> Metrics { get; set; } = new();
    public List<string> RelevantLines { get; set; } = new();
}

public static class ResultsScanner
{
    private static readonly string[] ParseExtensions = { ".csv", ".json", ".txt", ".xml", ".html", ".htm", ".log" };

    public static List<ParsedResultFile> ParseResults(List<string> changedFiles)
    {
        var candidates = changedFiles
            .Where(f => ParseExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => { try { return new FileInfo(f).Length > 0; } catch { return false; } })
            .OrderBy(Priority)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        var parsed = new List<ParsedResultFile>();
        foreach (var f in candidates)
        {
            try
            {
                var p = BenchmarkResultParser.Parse(f);
                if (p.Metrics.Count > 0 || p.RelevantLines.Count > 0) parsed.Add(p);
            }
            catch (Exception ex) { Log.Warn($"Не удалось разобрать {f}: {ex.Message}"); }
        }
        return parsed;
    }

    private static int Priority(string f)
    {
        var name = Path.GetFileName(f).ToLowerInvariant();
        if (name.Contains("result") || name.Contains("bench") || name.Contains("stat") ||
            name.Contains("fps") || name.Contains("report")) return 0;
        if (name.EndsWith(".log")) return 2;
        return 1;
    }
}

/// Обобщённый парсер: известные метрики по regex + JSON-обход + «интересные» строки + OCR-строки.
public static class BenchmarkResultParser
{
    private static readonly (string Key, Regex Re)[] Patterns =
    {
        ("Low 0.1% FPS",    new Regex(@"0[.,]1\s*%\s*low\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Low 1% FPS",      new Regex(@"(?<!0[.,])1\s*%\s*low\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Average FPS",     new Regex(@"(?:avg|average|mean)\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Min FPS",         new Regex(@"\bmin(?:imum)?\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Max FPS",         new Regex(@"\bmax(?:imum)?\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Score",           new Regex(@"\bscore\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("FPS (generic)",   new Regex(@"(\d+(?:[.,]\d+)?)\s*fps", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Frame time (ms)", new Regex(@"frame\s*time\D{0,25}?(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    public static ParsedResultFile Parse(string path)
    {
        var result = new ParsedResultFile { Path = path };
        var text = File.ReadAllText(path);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            ParseJson(text, result);
        else
            ParseText(text, result);

        result.Metrics = result.Metrics.GroupBy(m => m.Value).Select(g => g.First()).ToList();
        return result;
    }

    private static void ParseText(string text, ParsedResultFile result)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            foreach (var (key, re) in Patterns)
            {
                var m = re.Match(line);
                if (m.Success &&
                    double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v))
                {
                    result.Metrics.Add(new Metric { Key = key, Value = v });
                    break;
                }
            }

            bool hasNumber = line.Any(char.IsDigit);
            bool interesting = line.Contains("fps", StringComparison.OrdinalIgnoreCase)
                               || line.Contains('%')
                               || line.Contains('\t') || line.Contains(';') || line.Contains(',');
            if (hasNumber && interesting && result.RelevantLines.Count < 200)
                result.RelevantLines.Add(line);
        }
    }

    private static void ParseJson(string text, ParsedResultFile result)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            WalkJson(doc.RootElement, "", result, 0);
        }
        catch { ParseText(text, result); }
    }

    private static void WalkJson(JsonElement el, string prefix, ParsedResultFile result, int depth)
    {
        if (result.Metrics.Count > 500 || depth > 12) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    WalkJson(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", result, depth + 1);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    WalkJson(item, $"{prefix}[{i++}]", result, depth + 1);
                break;
            case JsonValueKind.Number:
                if (el.TryGetDouble(out var d))
                    result.Metrics.Add(new Metric { Key = prefix, Value = d });
                break;
            case JsonValueKind.String:
                var s = el.GetString();
                if (s != null && double.TryParse(s.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v))
                    result.Metrics.Add(new Metric { Key = prefix, Value = v });
                break;
        }
    }

    /// Разбор строк, распознанных OCR-ом с финального экрана результатов.
    public static List<Metric> ParseOcrLines(IEnumerable<string> lines)
    {
        var result = new List<Metric>();
        foreach (var line in lines)
        {
            var nums = Regex.Matches(line, @"\d+(?:[.,]\d+)?")
                .Select(m => double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture))
                .ToList();
            if (nums.Count == 0) continue;

            var low = line.ToLowerInvariant();
            if (Regex.IsMatch(low, @"average|avg|平均|средн"))
                result.Add(new Metric { Key = "Average FPS (screen)", Value = nums[0] });
            else if (Regex.IsMatch(low, @"\bmax|最大"))
                result.Add(new Metric { Key = "Max FPS (screen)", Value = nums[0] });
            else if (Regex.IsMatch(low, @"\bmin|мин|最小"))
                result.Add(new Metric { Key = "Min FPS (screen)", Value = nums[0] });
            else if (low.Contains("fps") || low.Contains("帧率") || low.Contains("帧"))
                result.Add(new Metric { Key = $"FPS (screen) «{line.Trim()}»", Value = nums[0] });
        }
        return result;
    }
}