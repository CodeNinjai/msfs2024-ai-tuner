using System.Text;

namespace AiTuner.Core.Msfs;

public abstract class UserCfgNode
{
    public string Name { get; }
    protected UserCfgNode(string name) => Name = name;
}

/// <summary>A single "Key Value" line. Value is kept raw (including quotes) for round-trip write-back.</summary>
public sealed class UserCfgValue : UserCfgNode
{
    public string Value { get; set; }
    public UserCfgValue(string name, string value) : base(name) => Value = value;
}

public sealed class UserCfgSection : UserCfgNode
{
    public List<UserCfgNode> Children { get; } = new();
    public UserCfgSection(string name) : base(name) { }
}

public sealed record UserCfgFlatEntry(string Section, string Key, string Value);

/// <summary>
/// Parses MSFS UserCfg.opt: nested "{Section" ... "}" blocks with "Key Value" lines.
/// Preserves order and raw values; ToText() regenerates the original tab-indented format
/// so stage-2 write-back keeps the file byte-compatible.
/// </summary>
public sealed class UserCfgDocument
{
    public UserCfgSection Root { get; }

    private readonly string _newline;

    private UserCfgDocument(UserCfgSection root, string newline)
    {
        Root = root;
        _newline = newline;
    }

    public static UserCfgDocument Parse(string text)
    {
        // MSFS 2024 schreibt die Datei mit LF; das erkannte Zeilenende wird beim Schreiben übernommen.
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var root = new UserCfgSection("");
        var stack = new Stack<UserCfgSection>();
        stack.Push(root);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('{'))
            {
                var section = new UserCfgSection(line[1..].Trim());
                stack.Peek().Children.Add(section);
                stack.Push(section);
            }
            else if (line == "}")
            {
                if (stack.Count > 1)
                    stack.Pop();
            }
            else
            {
                var i = line.IndexOfAny([' ', '\t']);
                var key = i < 0 ? line : line[..i];
                var value = i < 0 ? "" : line[(i + 1)..].Trim();
                stack.Peek().Children.Add(new UserCfgValue(key, value));
            }
        }

        return new UserCfgDocument(root, newline);
    }

    /// <summary>Regenerates the file in the original format (tabs per depth, detected line ending).</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        WriteChildren(builder, Root, 0, _newline);
        return builder.ToString();
    }

    private static void WriteChildren(StringBuilder builder, UserCfgSection section, int depth, string newline)
    {
        var indent = new string('\t', depth);
        foreach (var child in section.Children)
        {
            if (child is UserCfgValue value)
            {
                builder.Append(indent).Append(value.Name);
                if (value.Value.Length > 0)
                    builder.Append(' ').Append(value.Value);
                builder.Append(newline);
            }
            else if (child is UserCfgSection sub)
            {
                builder.Append(indent).Append('{').Append(sub.Name).Append(newline);
                WriteChildren(builder, sub, depth + 1, newline);
                builder.Append(indent).Append('}').Append(newline);
            }
        }
    }

    /// <summary>Sets an existing value. sectionPath uses '/' (e.g. "Graphics/Texture"); "" = root level.</summary>
    public bool SetValue(string sectionPath, string key, string newValue)
    {
        var section = ResolveSection(sectionPath);
        if (section is null)
            return false;

        foreach (var child in section.Children)
        {
            if (child is UserCfgValue value && value.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value.Value = newValue;
                return true;
            }
        }
        return false;
    }

    public string? GetValue(string sectionPath, string key)
    {
        var section = ResolveSection(sectionPath);
        return section?.Children.OfType<UserCfgValue>()
            .FirstOrDefault(v => v.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private UserCfgSection? ResolveSection(string path)
    {
        var current = Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Children.OfType<UserCfgSection>()
                .FirstOrDefault(s => s.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }
        return current;
    }

    public UserCfgSection? FindSection(string name)
        => FindSection(Root, name);

    private static UserCfgSection? FindSection(UserCfgSection parent, string name)
    {
        foreach (var child in parent.Children)
        {
            if (child is UserCfgSection section)
            {
                if (string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase))
                    return section;
                if (FindSection(section, name) is { } nested)
                    return nested;
            }
        }
        return null;
    }

    public IEnumerable<UserCfgFlatEntry> Flatten()
        => Flatten(Root, "");

    private static IEnumerable<UserCfgFlatEntry> Flatten(UserCfgSection section, string path)
    {
        foreach (var child in section.Children)
        {
            if (child is UserCfgValue value)
            {
                yield return new UserCfgFlatEntry(path, value.Name, value.Value);
            }
            else if (child is UserCfgSection sub)
            {
                var subPath = path.Length == 0 ? sub.Name : $"{path} › {sub.Name}";
                foreach (var entry in Flatten(sub, subPath))
                    yield return entry;
            }
        }
    }
}
