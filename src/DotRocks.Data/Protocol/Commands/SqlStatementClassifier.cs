namespace DotRocks.Data.Protocol.Commands;

/// <summary>
/// Classifies SQL for session retirement and conservative read-only fallback decisions.
/// </summary>
internal static class SqlStatementClassifier
{
    // A conservative retry allowlist, not a SQL parser. Reject ambiguous forms (including CTEs)
    // and inspect the raw text for separators/assignments/INTO even inside literals or comments:
    // a false negative only disables fallback; a false positive could replay a write.
    public static bool IsReadOnlyQuery(string commandText)
    {
        ReadOnlySpan<char> sql = commandText.AsSpan().Trim();
        if (!sql.IsEmpty && sql[^1] == ';')
        {
            sql = sql[..^1];
        }

        if (
            sql.Contains(';')
            || sql.Contains(":=", StringComparison.Ordinal)
            || sql.Contains("/*!", StringComparison.Ordinal)
        )
        {
            return false;
        }

        int start = SkipLeadingTrivia(sql);
        if (
            !MatchesKeyword(sql, start, "SELECT")
            && !MatchesKeyword(sql, start, "SHOW")
            && !MatchesKeyword(sql, start, "DESCRIBE")
            && !MatchesKeyword(sql, start, "DESC")
        )
        {
            return false;
        }

        for (int index = start; index < sql.Length; index++)
        {
            if (
                (index == 0 || !IsIdentifierPart(sql[index - 1]))
                && MatchesKeyword(sql, index, "INTO")
            )
            {
                return false;
            }
        }

        return true;
    }

    // Detection is intentionally conservative (it errs toward discarding): it skips leading
    // whitespace and SQL comments, then flags a statement whose leading keyword is USE or SET.
    // It also flags any statement that assigns a user variable with ":=" (for example
    // "SELECT @tenant := ?"), since that mutates session state without a leading SET keyword and
    // would otherwise leak the variable into the next lease of a pooled connection. Matching ":="
    // anywhere (including inside a string literal) can only over-retire a connection, which is the
    // safe direction.
    public static bool IsSessionMutating(string commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return false;
        }

        ReadOnlySpan<char> sql = commandText.AsSpan();
        int index = SkipLeadingTrivia(sql);
        return MatchesKeyword(sql, index, "USE")
            || MatchesKeyword(sql, index, "SET")
            || commandText.Contains(":=", StringComparison.Ordinal);
    }

    private static int SkipLeadingTrivia(ReadOnlySpan<char> sql)
    {
        int index = 0;
        while (index < sql.Length)
        {
            char current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            // Line comments: "-- ..." and "# ..." run to the end of the line.
            if (
                current == '#'
                || (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            )
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            // Block comment: "/* ... */".
            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            break;
        }

        return index;
    }

    private static bool MatchesKeyword(ReadOnlySpan<char> sql, int index, string keyword)
    {
        if (index + keyword.Length > sql.Length)
        {
            return false;
        }

        if (!sql.Slice(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Require a word boundary so identifiers like USER or SETTINGS do not match.
        int next = index + keyword.Length;
        return next >= sql.Length || !IsIdentifierPart(sql[next]);
    }

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';
}
