using System.Text;

namespace EnergyTracker.Architecture.Tests;

// Shared by the source-text guards (NoDirectClockReadsTests, BackendAnalyzerGateIsConfiguredTests):
// one string-aware comment stripper instead of a copy per guard.
internal static class CSharpSource
{
    // Removes // and /* */ comments but keeps the contents of string and char literals (a `//` inside
    // a string is not a comment). Knows regular "...", verbatim @"..." (no escapes, "" is a quote),
    // raw """...""" (closed by the same quote run) and char literals ('"', '\''), so none of them
    // desyncs the quote state and hides a later clock read.
    public static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                var quoteRun = 0;
                while (i + quoteRun < source.Length && source[i + quoteRun] == '"')
                {
                    quoteRun++;
                }

                if (quoteRun >= 3)
                {
                    // Raw string: copy through the next run of at least quoteRun quotes.
                    var close = source.IndexOf(new string('"', quoteRun), i + quoteRun, StringComparison.Ordinal);
                    var end = close < 0 ? source.Length : close + quoteRun;
                    result.Append(source, i, end - i);
                    i = end;
                    continue;
                }

                var verbatim = (i > 0 && source[i - 1] == '@') || (i > 1 && source[i - 1] == '$' && source[i - 2] == '@');
                result.Append(c);
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            result.Append("\"\"");
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i++]);
                    }

                    result.Append(source[i++]);
                }

                if (i < source.Length)
                {
                    result.Append(source[i++]);
                }
            }
            else if (c == '\'')
            {
                // Char literal: one char or one escape, then the closing quote.
                result.Append(c);
                i++;
                if (i < source.Length && source[i] == '\\' && i + 1 < source.Length)
                {
                    result.Append(source[i++]);
                }

                if (i < source.Length)
                {
                    result.Append(source[i++]);
                }

                if (i < source.Length && source[i] == '\'')
                {
                    result.Append(source[i++]);
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }
}
