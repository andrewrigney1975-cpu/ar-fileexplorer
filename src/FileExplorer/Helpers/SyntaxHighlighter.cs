namespace FileExplorer.Helpers;

public enum SyntaxTokenKind { Plain, Keyword, String, Comment, Number, Tag, Attribute }

public readonly record struct SyntaxToken(string Text, SyntaxTokenKind Kind);

/// Lightweight, line-oriented tokenizer used to color-code source file previews. Not a real
/// parser - good enough for at-a-glance readability, not perfect fidelity on every edge case.
public static class SyntaxHighlighter
{
    private sealed record LanguageProfile(HashSet<string> Keywords, string? LineComment, bool HasBlockComment, bool IsMarkup);

    public static List<List<SyntaxToken>> Tokenize(string text, string extension)
    {
        var profile = GetProfile(extension) ?? new LanguageProfile(new HashSet<string>(), null, false, false);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<List<SyntaxToken>>(lines.Length);
        var inComment = false;

        foreach (var line in lines)
        {
            result.Add(profile.IsMarkup
                ? TokenizeMarkupLine(line, ref inComment)
                : TokenizeCodeLine(line, profile, ref inComment));
        }

        return result;
    }

    private static List<SyntaxToken> TokenizeCodeLine(string line, LanguageProfile profile, ref bool inBlockComment)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;
        var n = line.Length;

        while (i < n)
        {
            if (inBlockComment)
            {
                var close = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new SyntaxToken(line[i..], SyntaxTokenKind.Comment));
                    break;
                }

                tokens.Add(new SyntaxToken(line[i..(close + 2)], SyntaxTokenKind.Comment));
                i = close + 2;
                inBlockComment = false;
                continue;
            }

            var c = line[i];

            if (profile.LineComment is { } lc && i + lc.Length <= n && string.CompareOrdinal(line, i, lc, 0, lc.Length) == 0)
            {
                tokens.Add(new SyntaxToken(line[i..], SyntaxTokenKind.Comment));
                break;
            }

            if (profile.HasBlockComment && c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new SyntaxToken(line[i..], SyntaxTokenKind.Comment));
                    inBlockComment = true;
                    break;
                }

                tokens.Add(new SyntaxToken(line[i..(close + 2)], SyntaxTokenKind.Comment));
                i = close + 2;
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                var start = i;
                var quote = c;
                i++;
                while (i < n && line[i] != quote)
                {
                    if (line[i] == '\\' && i + 1 < n)
                    {
                        i++;
                    }
                    i++;
                }
                if (i < n)
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.String));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.'))
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }
                var word = line[start..i];
                tokens.Add(new SyntaxToken(word, profile.Keywords.Contains(word) ? SyntaxTokenKind.Keyword : SyntaxTokenKind.Plain));
                continue;
            }

            {
                var start = i;
                while (i < n)
                {
                    var ch = line[i];
                    if (char.IsLetterOrDigit(ch) || ch == '_' || ch is '"' or '\'' or '`')
                    {
                        break;
                    }
                    if (profile.HasBlockComment && ch == '/' && i + 1 < n && line[i + 1] == '*')
                    {
                        break;
                    }
                    if (profile.LineComment is { } lc2 && i + lc2.Length <= n && string.CompareOrdinal(line, i, lc2, 0, lc2.Length) == 0)
                    {
                        break;
                    }
                    i++;
                }
                if (i == start)
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.Plain));
            }
        }

        return tokens;
    }

    private static List<SyntaxToken> TokenizeMarkupLine(string line, ref bool inComment)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;
        var n = line.Length;

        while (i < n)
        {
            if (inComment)
            {
                var close = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new SyntaxToken(line[i..], SyntaxTokenKind.Comment));
                    break;
                }

                tokens.Add(new SyntaxToken(line[i..(close + 3)], SyntaxTokenKind.Comment));
                i = close + 3;
                inComment = false;
                continue;
            }

            if (i + 3 < n && line[i] == '<' && line[i + 1] == '!' && line[i + 2] == '-' && line[i + 3] == '-')
            {
                var close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new SyntaxToken(line[i..], SyntaxTokenKind.Comment));
                    inComment = true;
                    break;
                }

                tokens.Add(new SyntaxToken(line[i..(close + 3)], SyntaxTokenKind.Comment));
                i = close + 3;
                continue;
            }

            var c = line[i];

            if (c == '<')
            {
                var start = i;
                i++;
                if (i < n && line[i] == '/')
                {
                    i++;
                }
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] is ':' or '-' or '_' or '.'))
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.Tag));
                continue;
            }

            if (c is '"' or '\'')
            {
                var start = i;
                var quote = c;
                i++;
                while (i < n && line[i] != quote)
                {
                    i++;
                }
                if (i < n)
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.String));
                continue;
            }

            // Crude attribute-name detection: a bare word immediately followed by '=' (typically
            // preceded by whitespace inside a tag). Good enough for a preview, not a real parser.
            if (char.IsLetter(c) && (i == 0 || line[i - 1] is ' ' or '\t'))
            {
                var start = i;
                var j = i;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] is ':' or '-' or '_'))
                {
                    j++;
                }
                if (j < n && line[j] == '=')
                {
                    tokens.Add(new SyntaxToken(line[start..j], SyntaxTokenKind.Attribute));
                    i = j;
                    continue;
                }
            }

            {
                var start = i;
                while (i < n && line[i] != '<' && line[i] != '"' && line[i] != '\'')
                {
                    i++;
                }
                if (i == start)
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(line[start..i], SyntaxTokenKind.Plain));
            }
        }

        return tokens;
    }

    private static LanguageProfile? GetProfile(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => new LanguageProfile(CsKeywords, "//", true, false),
        ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" => new LanguageProfile(JsKeywords, "//", true, false),
        ".java" => new LanguageProfile(JavaKeywords, "//", true, false),
        ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" => new LanguageProfile(CKeywords, "//", true, false),
        ".go" => new LanguageProfile(GoKeywords, "//", true, false),
        ".rs" => new LanguageProfile(RustKeywords, "//", true, false),
        ".json" => new LanguageProfile(JsonKeywords, null, false, false),
        ".css" => new LanguageProfile(new HashSet<string>(), null, true, false),
        ".py" => new LanguageProfile(PyKeywords, "#", false, false),
        ".xml" or ".xaml" or ".html" or ".htm" => new LanguageProfile(new HashSet<string>(), null, false, true),
        _ => null,
    };

    private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern","false",
        "finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock",
        "long","namespace","new","null","object","operator","out","override","params","private","protected","public",
        "readonly","record","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct",
        "switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","var",
        "virtual","void","volatile","while",
    };

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "async","await","break","case","catch","class","const","continue","debugger","default","delete","do","else",
        "export","extends","false","finally","for","function","if","import","in","instanceof","interface","let","new",
        "null","of","return","static","super","switch","this","throw","true","try","type","typeof","undefined","var",
        "void","while","with","yield",
    };

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract","assert","boolean","break","byte","case","catch","char","class","const","continue","default","do",
        "double","else","enum","extends","final","finally","float","for","goto","if","implements","import","instanceof",
        "int","interface","long","native","new","package","private","protected","public","return","short","static",
        "strictfp","super","switch","synchronized","this","throw","throws","transient","true","false","null","try",
        "void","volatile","while",
    };

    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "auto","break","case","char","const","continue","default","do","double","else","enum","extern","float","for",
        "goto","if","inline","int","long","register","restrict","return","short","signed","sizeof","static","struct",
        "switch","typedef","union","unsigned","void","volatile","while","class","namespace","template","public",
        "private","protected","virtual","new","delete","this","true","false","nullptr","using","bool",
    };

    private static readonly HashSet<string> GoKeywords = new(StringComparer.Ordinal)
    {
        "break","case","chan","const","continue","default","defer","else","fallthrough","for","func","go","goto","if",
        "import","interface","map","package","range","return","select","struct","switch","type","var","true","false",
        "nil","int","string","bool","float64","error",
    };

    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
    {
        "as","break","const","continue","crate","else","enum","extern","false","fn","for","if","impl","in","let","loop",
        "match","mod","move","mut","pub","ref","return","self","Self","static","struct","super","trait","true","type",
        "unsafe","use","where","while","async","await","dyn",
    };

    private static readonly HashSet<string> PyKeywords = new(StringComparer.Ordinal)
    {
        "and","as","assert","async","await","break","class","continue","def","del","elif","else","except","False",
        "finally","for","from","global","if","import","in","is","lambda","None","nonlocal","not","or","pass","raise",
        "return","True","try","while","with","yield",
    };

    private static readonly HashSet<string> JsonKeywords = new(StringComparer.Ordinal) { "true", "false", "null" };
}
