// Compiled by MSBuild's RoslynCodeTaskFactory (see MonoGame.AgentTools.targets), so it targets
// netstandard2.0 for Visual Studio's .NET Framework MSBuild as well as the .NET SDK's: no JSON library is
// available to both, and none is needed for the minimal edits below. Spans aren't available either.
#nullable enable
#pragma warning disable CA1845 // span-based string.Concat isn't in netstandard2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace MonoGame.AgentTools.Build
{
    /// <summary>
    /// Adds the MonoGame.AgentTools MCP server to the repository's agent configuration on build:
    /// <c>.mcp.json</c> always, and <c>.vscode/mcp.json</c> and <c>.cursor/mcp.json</c> where those folders
    /// exist, as Aspire's <c>aspire agent init</c> configures the agents it detects.
    /// </summary>
    public sealed class AddAgentToolsMcpServer : Microsoft.Build.Utilities.Task
    {
        /// <summary>The repository root; empty to find it by walking up to a .git folder or file.</summary>
        public string Root { get; set; } = "";

        [Required]
        public string StartDirectory { get; set; } = "";

        [Required]
        public string PackageVersion { get; set; } = "";

        public override bool Execute()
        {
            var root = Root.Length > 0 ? Root : FindRepositoryRoot(StartDirectory);
            if (root == null)
            {
                Log.LogMessage(MessageImportance.Normal,
                    "MonoGame.AgentTools: no .git found above the project, so the MCP server wasn't added to .mcp.json. Set AgentToolsRoot to choose the folder.");
                return true;
            }

            Configure(root, ".mcp.json", "mcpServers");
            if (Directory.Exists(Path.Combine(root, ".vscode")))
                Configure(root, ".vscode/mcp.json", "servers");
            if (Directory.Exists(Path.Combine(root, ".cursor")))
                Configure(root, ".cursor/mcp.json", "mcpServers");
            return !Log.HasLoggedErrors;
        }

        private void Configure(string root, string relativePath, string container)
        {
            var path = Path.Combine(root, relativePath);
            // Projects building in parallel may reach the same file: hold it exclusively while editing.
            for (int attempt = 0; ; ++attempt)
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        string text;
                        using (var reader = new StreamReader(file, new UTF8Encoding(false), true, 4096, leaveOpen: true))
                            text = reader.ReadToEnd();
                        var result = McpConfigEditor.Edit(text, container, McpConfigEditor.ServerName, PackageVersion);
                        if (result.Error != null)
                        {
                            Log.LogWarning(null, "AGT001", null, path, 0, 0, 0, 0,
                                "MonoGame.AgentTools: couldn't add the MCP server to {0}: {1}. Add it yourself: \"{2}\": {3}",
                                path, result.Error, McpConfigEditor.ServerName, McpConfigEditor.Entry(PackageVersion, "", ""));
                            return;
                        }
                        if (result.Text == null)
                        {
                            if (result.Note != null)
                                Log.LogMessage(MessageImportance.Normal, "MonoGame.AgentTools: {0}: {1}", relativePath, result.Note);
                            return;
                        }
                        var bytes = new UTF8Encoding(false).GetBytes(result.Text);
                        file.SetLength(0);
                        file.Write(bytes, 0, bytes.Length);
                        Log.LogMessage(MessageImportance.High, "MonoGame.AgentTools: {0}: {1}", relativePath, result.Note);
                        return;
                    }
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private static string? FindRepositoryRoot(string start)
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var git = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))   // a file in worktrees and submodules
                    return directory.FullName;
            }
            return null;
        }
    }

    /// <summary>
    /// Adds or updates one server entry in an MCP configuration file with minimal text edits, keeping the
    /// rest of the file as it was (comments, trailing commas, formatting), as VS Code's jsonc-parser edits
    /// settings.json. An entry the user changed is left alone; only one this package wrote (its own command
    /// with an older version) is updated, as Aspire updates only its own deprecated forms.
    /// </summary>
    public static class McpConfigEditor
    {
        public const string ServerName = "monogame-agenttools";
        public const string PackageId = "MonoGame.AgentTools.Mcp";

        private static readonly char[] LineBreaks = { '\r', '\n' };

        public sealed class Result
        {
            /// <summary>The new file text, or null when nothing changes.</summary>
            public string? Text { get; set; }
            /// <summary>What happened, for the build log.</summary>
            public string? Note { get; set; }
            /// <summary>Why the file couldn't be edited, or null.</summary>
            public string? Error { get; set; }
        }

        /// <summary>
        /// The server entry, with <paramref name="indent"/> for its members and <paramref name="closingIndent"/>
        /// for its closing brace. <c>dotnet dnx</c> rather than <c>dnx</c>: on Windows dnx is a .cmd script,
        /// which clients that start servers without a shell can't run.
        /// </summary>
        public static string Entry(string version, string indent, string closingIndent, string newline = " ")
        {
            var separator = newline == " " ? " " : newline + indent;
            return "{" + separator + "\"type\": \"stdio\"," + separator + "\"command\": \"dotnet\"," + separator
                + "\"args\": [\"dnx\", \"" + PackageId + "@" + version + "\", \"--yes\", \"mcp\"]"
                + (newline == " " ? " " : newline + closingIndent) + "}";
        }

        public static Result Edit(string text, string container, string name, string version)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var unit = DetectIndentUnit(text);
            if (text.Trim().Length == 0)
            {
                var created = "{" + newline + unit + Quote(container) + ": {" + newline + unit + unit + Quote(name) + ": "
                    + Entry(version, unit + unit + unit, unit + unit, newline) + newline + unit + "}" + newline + "}" + newline;
                return new Result { Text = created, Note = "created with the " + name + " MCP server" };
            }

            Node root;
            try
            {
                root = new Parser(text).ParseDocument();
            }
            catch (FormatException e)
            {
                return new Result { Error = "it isn't valid JSON (" + e.Message + ")" };
            }
            if (root.Kind != NodeKind.Object)
                return new Result { Error = "its top level isn't an object" };

            var servers = root.Get(container);
            if (servers == null)
            {
                var memberIndent = LineIndent(text, root.Start) + unit;
                var member = Quote(container) + ": {" + newline + memberIndent + unit + Quote(name) + ": "
                    + Entry(version, memberIndent + unit + unit, memberIndent + unit, newline) + newline + memberIndent + "}";
                return new Result { Text = Insert(text, root, member, memberIndent, newline), Note = "added the " + name + " MCP server" };
            }
            if (servers.Kind != NodeKind.Object)
                return new Result { Error = "\"" + container + "\" isn't an object" };

            var existing = servers.Get(name);
            var indent = LineIndent(text, servers.Start) + unit;
            if (existing == null)
            {
                var member = Quote(name) + ": " + Entry(version, indent + unit, indent, newline);
                return new Result { Text = Insert(text, servers, member, indent, newline), Note = "added the " + name + " MCP server" };
            }

            var current = OwnVersion(existing);
            if (current == null)
                return new Result { Note = "left the " + name + " entry as it is (it was changed by hand)" };
            if (current == version)
                return new Result();
            var replacement = Entry(version, indent + unit, indent, newline);
            return new Result
            {
                Text = text.Substring(0, existing.Start) + replacement + text.Substring(existing.End),
                Note = "updated the " + name + " MCP server from " + current + " to " + version,
            };
        }

        /// <summary>The version in an entry this package wrote, or null if the entry is anything else.</summary>
        private static string? OwnVersion(Node entry)
        {
            if (entry.Kind != NodeKind.Object)
                return null;
            var command = entry.Get("command");
            var args = entry.Get("args");
            if (command == null || command.Kind != NodeKind.String || args == null || args.Kind != NodeKind.Array)
                return null;
            var items = args.Items;
            int index;
            if (command.Value == "dotnet" && items.Count == 4 && items[0].Value == "dnx")
                index = 1;
            else if (command.Value == "dnx" && items.Count == 3)
                index = 0;   // the form before dotnet dnx
            else
                return null;
            var package = items[index].Value;
            var prefix = PackageId + "@";
            if (package == null || !package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || items[index + 1].Value != "--yes" || items[index + 2].Value != "mcp")
                return null;
            return package.Substring(prefix.Length);
        }

        /// <summary>Inserts a member as the last one of <paramref name="obj"/>.</summary>
        private static string Insert(string text, Node obj, string member, string indent, string newline)
        {
            if (obj.Members.Count == 0)
            {
                var closingIndent = LineIndent(text, obj.Start);
                return text.Substring(0, obj.Start + 1) + newline + indent + member + newline + closingIndent
                    + text.Substring(obj.Start + 1).TrimStart(' ', '\t', '\r', '\n');
            }
            var last = obj.Members[obj.Members.Count - 1].Value;
            var next = SkipTrivia(text, last.End);
            var comma = next < text.Length && text[next] == ',';   // JSONC allows a trailing comma
            // Insert after the rest of the line when it's only a comment, so the comment stays with its member.
            var anchor = comma ? next + 1 : last.End;
            var lineEnd = text.IndexOfAny(LineBreaks, anchor);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var rest = text.Substring(anchor, lineEnd - anchor).Trim();
            var at = rest.Length == 0 || rest.StartsWith("//", StringComparison.Ordinal) ? lineEnd : anchor;
            if (comma)
                return text.Substring(0, at) + newline + indent + member + "," + text.Substring(at);
            return text.Substring(0, last.End) + "," + text.Substring(last.End, at - last.End) + newline + indent + member + text.Substring(at);
        }

        private static string Quote(string s) => "\"" + s + "\"";

        private static string LineIndent(string text, int position)
        {
            var start = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
            var end = start;
            while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                ++end;
            return text.Substring(start, end - start);
        }

        /// <summary>The first indentation in the file, or two spaces.</summary>
        private static string DetectIndentUnit(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var end = 0;
                while (end < line.Length && (line[end] == ' ' || line[end] == '\t'))
                    ++end;
                if (end > 0 && end < line.Length && line[end] != '\r')
                    return line.Substring(0, end);
            }
            return "  ";
        }

        private static int SkipTrivia(string text, int position)
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    ++position;
                else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n')
                        ++position;
                }
                else if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
                {
                    var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw new FormatException("unterminated comment");
                    position = end + 2;
                }
                else
                    break;
            }
            return position;
        }

        private enum NodeKind { Object, Array, String, Other }

        private sealed class Member
        {
            public Member(string key, Node value) => (Key, Value) = (key, value);

            public string Key { get; }
            public Node Value { get; }
        }

        /// <summary>A JSON value and where it is in the text (<see cref="End"/> is exclusive).</summary>
        private sealed class Node
        {
            public NodeKind Kind { get; set; }
            public int Start { get; set; }
            public int End { get; set; }
            public string? Value { get; set; }
            public List<Member> Members { get; } = new List<Member>();
            public List<Node> Items { get; } = new List<Node>();

            public Node? Get(string key)
            {
                foreach (var member in Members)
                    if (member.Key == key)
                        return member.Value;
                return null;
            }
        }

        /// <summary>A JSONC parser (comments and trailing commas allowed) that records positions.</summary>
        private sealed class Parser
        {
            private readonly string _text;
            private int _position;

            public Parser(string text) => _text = text;

            public Node ParseDocument()
            {
                var node = ParseValue();
                if (SkipTrivia(_text, _position) != _text.Length)
                    throw new FormatException("unexpected text after the top-level value");
                return node;
            }

            private Node ParseValue()
            {
                _position = SkipTrivia(_text, _position);
                if (_position >= _text.Length)
                    throw new FormatException("unexpected end");
                var c = _text[_position];
                if (c == '{')
                    return ParseObject();
                if (c == '[')
                    return ParseArray();
                if (c == '"')
                {
                    var start = _position;
                    var value = ParseString();
                    return new Node { Kind = NodeKind.String, Start = start, End = _position, Value = value };
                }
                var begin = _position;
                while (_position < _text.Length && "{}[],:\"/ \t\r\n".IndexOf(_text[_position]) < 0)
                    ++_position;
                if (_position == begin)
                    throw new FormatException("unexpected '" + c + "' at " + begin);
                return new Node { Kind = NodeKind.Other, Start = begin, End = _position };
            }

            private Node ParseObject()
            {
                var node = new Node { Kind = NodeKind.Object, Start = _position };
                ++_position;
                while (true)
                {
                    _position = SkipTrivia(_text, _position);
                    if (_position < _text.Length && _text[_position] == '}')
                        break;
                    if (_position >= _text.Length || _text[_position] != '"')
                        throw new FormatException("expected a property name at " + _position);
                    var key = ParseString();
                    _position = SkipTrivia(_text, _position);
                    Expect(':');
                    node.Members.Add(new Member(key, ParseValue()));
                    _position = SkipTrivia(_text, _position);
                    if (_position < _text.Length && _text[_position] == ',')
                        ++_position;
                    else if (_position >= _text.Length || _text[_position] != '}')
                        throw new FormatException("expected ',' or '}' at " + _position);
                }
                ++_position;
                node.End = _position;
                return node;
            }

            private Node ParseArray()
            {
                var node = new Node { Kind = NodeKind.Array, Start = _position };
                ++_position;
                while (true)
                {
                    _position = SkipTrivia(_text, _position);
                    if (_position < _text.Length && _text[_position] == ']')
                        break;
                    node.Items.Add(ParseValue());
                    _position = SkipTrivia(_text, _position);
                    if (_position < _text.Length && _text[_position] == ',')
                        ++_position;
                    else if (_position >= _text.Length || _text[_position] != ']')
                        throw new FormatException("expected ',' or ']' at " + _position);
                }
                ++_position;
                node.End = _position;
                return node;
            }

            private string ParseString()
            {
                var value = new StringBuilder();
                ++_position;
                while (true)
                {
                    if (_position >= _text.Length)
                        throw new FormatException("unterminated string");
                    var c = _text[_position++];
                    if (c == '"')
                        return value.ToString();
                    if (c != '\\')
                    {
                        value.Append(c);
                        continue;
                    }
                    if (_position >= _text.Length)
                        throw new FormatException("unterminated string");
                    var e = _text[_position++];
                    switch (e)
                    {
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case 'r': value.Append('\r'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'u':
                            if (_position + 4 > _text.Length)
                                throw new FormatException("bad \\u escape");
                            value.Append((char)Convert.ToInt32(_text.Substring(_position, 4), 16));
                            _position += 4;
                            break;
                        default: value.Append(e); break;
                    }
                }
            }

            private void Expect(char c)
            {
                if (_position >= _text.Length || _text[_position] != c)
                    throw new FormatException("expected '" + c + "' at " + _position);
                ++_position;
            }
        }
    }
}
