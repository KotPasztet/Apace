using DiffPatch.Data;
using System.Collections;
using System.Text.RegularExpressions;

namespace MCEPatcher.Core;

internal class DiffParser
{
    private delegate void ParserAction(string line, Match m);

    private class HandlerRow
    {
        public Regex Expression { get; }

        public Action<string, Match> Action { get; }

        public HandlerRow(Regex expression, Action<string, Match> action)
        {
            Expression = expression;
            Action = action;
        }
    }

    private class HandlerCollection : IEnumerable<HandlerRow>, IEnumerable
    {
        private List<HandlerRow> handlers = new List<HandlerRow>();

        public void Add(string expression, Action action)
        {
            handlers.Add(new HandlerRow(new Regex(expression), delegate
            {
                action();
            }));
        }

        public void Add(string expression, Action<string> action)
        {
            handlers.Add(new HandlerRow(new Regex(expression), delegate (string line, Match m)
            {
                action(line);
            }));
        }

        public void Add(string expression, Action<string, Match> action)
        {
            handlers.Add(new HandlerRow(new Regex(expression), action));
        }

        public IEnumerator<HandlerRow> GetEnumerator()
        {
            return handlers.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return handlers.GetEnumerator();
        }
    }

    private const string noeol = "\\ No newline at end of file";

    private const string devnull = "/dev/null";

    private List<FileDiff> files = new List<FileDiff>();

    private int in_del;

    private int in_add;

    private Chunk current;

    private FileDiff file;

    private int oldStart;

    private int newStart;

    private int oldLines;

    private int newLines;

    private readonly HandlerCollection schema;

    public DiffParser()
    {
        schema = new HandlerCollection
        {
            { "^diff\\s", Start },
            { "^new file mode \\d+$", NewFile },
            { "^deleted file mode \\d+$", DeletedFile },
            { "^index\\s[\\da-zA-Z]+\\.\\.[\\da-zA-Z]+(\\s(\\d+))?$", Index },
            { "^---\\s", FromFile },
            { "^\\+\\+\\+\\s", ToFile },
            { "^@@\\s+\\-(\\d+),?(\\d+)?\\s+\\+(\\d+),?(\\d+)?\\s@@", Chunk },
            { "^-", DeleteLine },
            { "^\\+", AddLine },
            { "^Binary files (.+) and (.+) differ", BinaryDiff }
        };
    }

    public IEnumerable<FileDiff> Run(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            if (!ParseLine(line))
            {
                ParseNormalLine(line);
            }
        }

        return files;
    }

    private void Start(string? line)
    {
        file = new FileDiff();
        files.Add(file);
        if (file.To is null && file.From is null)
        {
            var array = ParseFileNames(line);
            if (array is not null)
            {
                file.From = array[0];
                file.To = array[1];
            }
        }
    }

    private void Restart()
    {
        if (file is null || file.Chunks.Count is not 0)
        {
            Start(null);
        }
    }

    private void NewFile()
    {
        Restart();
        file.Type = FileChangeType.Add;
        file.From = "/dev/null";
    }

    private void DeletedFile()
    {
        Restart();
        file.Type = FileChangeType.Delete;
        file.To = "/dev/null";
    }

    private void Index(string line)
    {
        Restart();
        file.Index = line.Split(' ').Skip(1);
    }

    private void FromFile(string line)
    {
        Restart();
        file.From = ParseFileName(line);
    }

    private void ToFile(string line)
    {
        Restart();
        file.To = ParseFileName(line);
    }

    private void BinaryDiff()
    {
        Restart();
        file.Type = FileChangeType.Modified;
    }

    private void Chunk(string line, Match match)
    {
        in_del = oldStart = int.Parse(match.Groups[1].Value);
        oldLines = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        in_add = newStart = int.Parse(match.Groups[3].Value);
        newLines = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        var rangeInfo = new ChunkRangeInfo(new ChunkRange(oldStart, oldLines), new ChunkRange(newStart, newLines));
        current = new Chunk(line, rangeInfo);
        file.Chunks.Add(current);
    }

    private void DeleteLine(string line)
    {
        var content = DiffLineHelper.GetContent(line);
        current.Changes.Add(new LineDiff(LineChangeType.Delete, in_del++, content));
        file.Deletions++;
    }

    private void AddLine(string line)
    {
        var content = DiffLineHelper.GetContent(line);
        current.Changes.Add(new LineDiff(LineChangeType.Add, in_add++, content));
        file.Additions++;
    }

    private void ParseNormalLine(string line)
    {
        if (file is not null && !string.IsNullOrEmpty(line))
        {
            var content = DiffLineHelper.GetContent(line);
            current.Changes.Add(new LineDiff((line is not "\\ No newline at end of file") ? in_del++ : 0, (line is not "\\ No newline at end of file") ? in_add++ : 0, content));
        }
    }

    private bool ParseLine(string line)
    {
        foreach (var item in schema)
        {
            var match = item.Expression.Match(line);
            if (match.Success)
            {
                item.Action(line, match);
                return true;
            }
        }

        return false;
    }

    private static string[]? ParseFileNames(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        return (from fileName in s.Split(' ').Reverse().Take(2)
            .Reverse()
            select Regex.Replace(fileName, "^(a|b)\\/", "")).ToArray();
    }

    private static string ParseFileName(string s)
    {
        s = s.TrimStart('-', '+');
        s = s.Trim();
        var match = new Regex("\\t.*|\\d{4}-\\d\\d-\\d\\d\\s\\d\\d:\\d\\d:\\d\\d(.\\d+)?\\s(\\+|-)\\d\\d\\d\\d").Match(s);
        if (match.Success)
        {
            s = s.Substring(0, match.Index).Trim();
        }

        if (!Regex.IsMatch(s, "^(a|b)\\/"))
        {
            return s;
        }

        return s.Substring(2);
    }

    internal static class DiffLineHelper
    {
        public static string GetContent(string line)
        {
            return line.Substring(1);
        }
    }
}
