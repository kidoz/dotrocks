namespace DotRocks.Data.Protocol.Commands;

/// <summary>
/// Classifies SQL statement text for connection-management decisions. Currently detects
/// session-mutating statements (USE / SET) so the pool can retire connections whose session state
/// may have changed.
/// </summary>
internal static class SqlStatementClassifier
{
    // Detection is intentionally conservative (it errs toward discarding): it skips leading
    // whitespace and SQL comments, then flags a statement whose leading keyword is USE or SET.
    public static bool IsSessionMutating(string commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return false;
        }

        ReadOnlySpan<char> sql = commandText.AsSpan();
        int index = SkipLeadingTrivia(sql);
        return MatchesKeyword(sql, index, "USE") || MatchesKeyword(sql, index, "SET");
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
