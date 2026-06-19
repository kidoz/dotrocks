using System.Data;
using System.Data.Common;
using System.Text;

namespace DotRocks.Data.Protocol.Commands;

internal static class CommandTextParameterBinder
{
    public static string Bind(string commandText, DbParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Count == 0)
        {
            return commandText;
        }

        Dictionary<string, DbParameter> parameterMap = BuildParameterMap(parameters);
        var usedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder(commandText.Length);

        for (int index = 0; index < commandText.Length; index++)
        {
            char current = commandText[index];
            if (current == '\'')
            {
                AppendSingleQuotedLiteral(commandText, builder, ref index);
                continue;
            }

            if (current == '"')
            {
                AppendQuotedRegion(commandText, builder, ref index, '"');
                continue;
            }

            if (current == '`')
            {
                AppendQuotedRegion(commandText, builder, ref index, '`');
                continue;
            }

            if (current == '-' && IsNext(commandText, index, '-'))
            {
                AppendLineComment(commandText, builder, ref index);
                continue;
            }

            if (current == '#')
            {
                AppendLineComment(commandText, builder, ref index);
                continue;
            }

            if (current == '/' && IsNext(commandText, index, '*'))
            {
                AppendBlockComment(commandText, builder, ref index);
                continue;
            }

            if (current == '?')
            {
                throw new NotSupportedException(
                    "Positional parameter placeholders are not supported. Use named @parameter placeholders."
                );
            }

            if (current == '@')
            {
                if (IsNext(commandText, index, '@'))
                {
                    builder.Append("@@");
                    index++;
                    continue;
                }

                if (
                    index + 1 >= commandText.Length
                    || !IsParameterNameStart(commandText[index + 1])
                )
                {
                    throw new FormatException(
                        $"Invalid parameter placeholder at character {index}."
                    );
                }

                int nameStart = index + 1;
                int nameEnd = nameStart;
                while (nameEnd < commandText.Length && IsParameterNamePart(commandText[nameEnd]))
                {
                    nameEnd++;
                }

                string parameterName = commandText[nameStart..nameEnd];
                if (!parameterMap.TryGetValue(parameterName, out DbParameter? parameter))
                {
                    throw new InvalidOperationException(
                        $"Command text references parameter '@{parameterName}', but it was not provided."
                    );
                }

                builder.Append(SqlLiteralFormatter.Format(parameter.Value));
                usedParameters.Add(parameterName);
                index = nameEnd - 1;
                continue;
            }

            builder.Append(current);
        }

        foreach (string parameterName in parameterMap.Keys)
        {
            if (!usedParameters.Contains(parameterName))
            {
                throw new InvalidOperationException(
                    $"Parameter '@{parameterName}' was provided but is not referenced by the command text."
                );
            }
        }

        return builder.ToString();
    }

    public static PreparedCommandText Prepare(string commandText, DbParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        ArgumentNullException.ThrowIfNull(parameters);

        Dictionary<string, DbParameter> parameterMap = BuildParameterMap(parameters);
        var referencedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasPlaceholders = false;

        for (int index = 0; index < commandText.Length; index++)
        {
            char current = commandText[index];
            if (current == '\'')
            {
                SkipSingleQuotedLiteral(commandText, ref index);
                continue;
            }

            if (current == '"')
            {
                SkipQuotedRegion(commandText, ref index, '"');
                continue;
            }

            if (current == '`')
            {
                SkipQuotedRegion(commandText, ref index, '`');
                continue;
            }

            if (current == '-' && IsNext(commandText, index, '-'))
            {
                SkipLineComment(commandText, ref index);
                continue;
            }

            if (current == '#')
            {
                SkipLineComment(commandText, ref index);
                continue;
            }

            if (current == '/' && IsNext(commandText, index, '*'))
            {
                SkipBlockComment(commandText, ref index);
                continue;
            }

            if (current == '?')
            {
                throw new NotSupportedException(
                    "Positional parameter placeholders are not supported. Use named @parameter placeholders."
                );
            }

            if (current == '@')
            {
                if (IsNext(commandText, index, '@'))
                {
                    index++;
                    continue;
                }

                if (
                    index + 1 >= commandText.Length
                    || !IsParameterNameStart(commandText[index + 1])
                )
                {
                    throw new FormatException(
                        $"Invalid parameter placeholder at character {index}."
                    );
                }

                int nameStart = index + 1;
                int nameEnd = nameStart;
                while (nameEnd < commandText.Length && IsParameterNamePart(commandText[nameEnd]))
                {
                    nameEnd++;
                }

                string parameterName = commandText[nameStart..nameEnd];
                if (!parameterMap.ContainsKey(parameterName))
                {
                    throw new InvalidOperationException(
                        $"Command text references parameter '@{parameterName}', but it was not provided."
                    );
                }

                referencedParameters.Add(parameterName);
                hasPlaceholders = true;
                index = nameEnd - 1;
            }
        }

        foreach (string parameterName in parameterMap.Keys)
        {
            if (!referencedParameters.Contains(parameterName))
            {
                throw new InvalidOperationException(
                    $"Parameter '@{parameterName}' was provided but is not referenced by the command text."
                );
            }
        }

        return new PreparedCommandText(commandText, [.. referencedParameters], hasPlaceholders);
    }

    private static Dictionary<string, DbParameter> BuildParameterMap(
        DbParameterCollection parameters
    )
    {
        var parameterMap = new Dictionary<string, DbParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (DbParameter parameter in parameters)
        {
            if (parameter.Direction != ParameterDirection.Input)
            {
                throw new NotSupportedException(
                    "Only input parameters are supported for text commands."
                );
            }

            string parameterName = NormalizeParameterName(parameter.ParameterName);
            if (!parameterMap.TryAdd(parameterName, parameter))
            {
                throw new InvalidOperationException(
                    $"Parameter '@{parameterName}' is defined more than once."
                );
            }
        }

        return parameterMap;
    }

    private static string NormalizeParameterName(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            throw new ArgumentException(
                "Parameter names must not be empty.",
                nameof(parameterName)
            );
        }

        string normalized = parameterName[0] == '@' ? parameterName[1..] : parameterName;
        if (normalized.Length == 0 || !IsParameterNameStart(normalized[0]))
        {
            throw new ArgumentException(
                $"Parameter name '{parameterName}' is not a supported @name parameter.",
                nameof(parameterName)
            );
        }

        for (int i = 1; i < normalized.Length; i++)
        {
            if (!IsParameterNamePart(normalized[i]))
            {
                throw new ArgumentException(
                    $"Parameter name '{parameterName}' is not a supported @name parameter.",
                    nameof(parameterName)
                );
            }
        }

        return normalized;
    }

    private static void AppendSingleQuotedLiteral(
        string commandText,
        StringBuilder builder,
        ref int index
    )
    {
        builder.Append(commandText[index]);
        while (++index < commandText.Length)
        {
            builder.Append(commandText[index]);
            if (commandText[index] == '\\' && index + 1 < commandText.Length)
            {
                builder.Append(commandText[++index]);
                continue;
            }

            if (commandText[index] != '\'')
            {
                continue;
            }

            if (IsNext(commandText, index, '\''))
            {
                builder.Append(commandText[++index]);
                continue;
            }

            break;
        }
    }

    private static void AppendQuotedRegion(
        string commandText,
        StringBuilder builder,
        ref int index,
        char quote
    )
    {
        builder.Append(commandText[index]);
        while (++index < commandText.Length)
        {
            builder.Append(commandText[index]);
            if (commandText[index] == quote)
            {
                break;
            }
        }
    }

    private static void AppendLineComment(string commandText, StringBuilder builder, ref int index)
    {
        builder.Append(commandText[index]);
        while (++index < commandText.Length)
        {
            builder.Append(commandText[index]);
            if (commandText[index] == '\n')
            {
                break;
            }
        }
    }

    private static void AppendBlockComment(string commandText, StringBuilder builder, ref int index)
    {
        builder.Append("/*");
        index++;
        while (++index < commandText.Length)
        {
            builder.Append(commandText[index]);
            if (commandText[index] == '*' && IsNext(commandText, index, '/'))
            {
                builder.Append('/');
                index++;
                break;
            }
        }
    }

    private static void SkipSingleQuotedLiteral(string commandText, ref int index)
    {
        while (++index < commandText.Length)
        {
            if (commandText[index] == '\\' && index + 1 < commandText.Length)
            {
                index++;
                continue;
            }

            if (commandText[index] != '\'')
            {
                continue;
            }

            if (IsNext(commandText, index, '\''))
            {
                index++;
                continue;
            }

            break;
        }
    }

    private static void SkipQuotedRegion(string commandText, ref int index, char quote)
    {
        while (++index < commandText.Length)
        {
            if (commandText[index] == quote)
            {
                break;
            }
        }
    }

    private static void SkipLineComment(string commandText, ref int index)
    {
        while (++index < commandText.Length)
        {
            if (commandText[index] == '\n')
            {
                break;
            }
        }
    }

    private static void SkipBlockComment(string commandText, ref int index)
    {
        index++;
        while (++index < commandText.Length)
        {
            if (commandText[index] == '*' && IsNext(commandText, index, '/'))
            {
                index++;
                break;
            }
        }
    }

    private static bool IsNext(string value, int index, char expected) =>
        index + 1 < value.Length && value[index + 1] == expected;

    private static bool IsParameterNameStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsParameterNamePart(char value) =>
        IsParameterNameStart(value) || value is >= '0' and <= '9';
}

internal sealed record PreparedCommandText(
    string CommandText,
    IReadOnlyList<string> ParameterNames,
    bool HasPlaceholders
);
