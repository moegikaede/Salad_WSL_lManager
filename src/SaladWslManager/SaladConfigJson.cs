using System;
using System.Globalization;
using System.IO;
using System.Text;

internal static partial class Program
{
    // Keep conservative config parsing isolated; normal monitoring does not mutate Salad configuration.
    private static void TrySetSaladConfigFlag(string key, string value)
    {
        Log("salad_config_set_skipped observation_only=true key=" + key);
    }

    private static void RepairKnownSaladConfigValues()
    {
        Log("salad_config_repair_skipped observation_only=true");
    }

    private static bool TryGetSaladConfigString(string key, out string value)
    {
        value = null;
        try
        {
            var configPath = GetSaladConfigPath();
            if (!File.Exists(configPath))
            {
                return false;
            }

            string text;
            using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                text = reader.ReadToEnd();
            }

            return TryGetTopLevelJsonStringProperty(text, key, out value);
        }
        catch (Exception ex)
        {
            Log("salad_config_get_error key=" + key + " error=" + ex.Message);
            return false;
        }
    }

    private static string GetSaladConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Salad",
            "config.json");
    }

    private static bool IsSaladConfigFlagTrue(string key)
    {
        try
        {
            var configPath = GetSaladConfigPath();
            if (!File.Exists(configPath))
            {
                return false;
            }

            string text;
            using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                text = reader.ReadToEnd();
            }

            string value;
            return TryGetTopLevelJsonStringProperty(text, key, out value) &&
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log("salad_config_read_error key=" + key + " error=" + ex.Message);
            return false;
        }
    }

    private static bool TryGetTopLevelJsonStringProperty(string text, string key, out string value)
    {
        value = null;
        int valueLiteralStart;
        int valueLiteralEnd;
        string currentValue;
        if (!TryFindTopLevelJsonStringProperty(text, key, out currentValue, out valueLiteralStart, out valueLiteralEnd))
        {
            return false;
        }

        value = currentValue;
        return true;
    }

    private static bool TryReplaceTopLevelJsonStringProperty(string text, string key, string newValue, out string updated)
    {
        updated = text;
        int valueLiteralStart;
        int valueLiteralEnd;
        string currentValue;
        if (!TryFindTopLevelJsonStringProperty(text, key, out currentValue, out valueLiteralStart, out valueLiteralEnd))
        {
            return false;
        }

        updated = text.Substring(0, valueLiteralStart) +
            ToJsonStringLiteral(newValue) +
            text.Substring(valueLiteralEnd);
        return true;
    }

    private static bool TryFindTopLevelJsonStringProperty(
        string text,
        string key,
        out string value,
        out int valueLiteralStart,
        out int valueLiteralEnd)
    {
        value = null;
        valueLiteralStart = -1;
        valueLiteralEnd = -1;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var index = 0;
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '{')
        {
            return false;
        }

        index++;
        while (index < text.Length)
        {
            SkipJsonWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                return false;
            }

            string name;
            int nameLiteralStart;
            int nameLiteralEnd;
            if (!TryReadJsonStringLiteral(text, ref index, out name, out nameLiteralStart, out nameLiteralEnd))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref index);
            if (index >= text.Length || text[index] != ':')
            {
                return false;
            }

            index++;
            SkipJsonWhitespace(text, ref index);

            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return TryReadJsonStringLiteral(text, ref index, out value, out valueLiteralStart, out valueLiteralEnd);
            }

            if (!SkipJsonValue(text, ref index))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref index);
            if (index < text.Length && text[index] == ',')
            {
                index++;
                continue;
            }

            if (index < text.Length && text[index] == '}')
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadJsonStringLiteral(
        string text,
        ref int index,
        out string value,
        out int literalStart,
        out int literalEnd)
    {
        value = null;
        literalStart = index;
        literalEnd = -1;

        if (index >= text.Length || text[index] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        index++;
        while (index < text.Length)
        {
            var ch = text[index++];
            if (ch == '"')
            {
                literalEnd = index;
                value = builder.ToString();
                return true;
            }

            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            if (index >= text.Length)
            {
                return false;
            }

            var escaped = text[index++];
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                    builder.Append(escaped);
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (index + 4 > text.Length)
                    {
                        return false;
                    }

                    int codePoint;
                    if (!int.TryParse(
                        text.Substring(index, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out codePoint))
                    {
                        return false;
                    }

                    builder.Append((char)codePoint);
                    index += 4;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool SkipJsonValue(string text, ref int index)
    {
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length)
        {
            return false;
        }

        if (text[index] == '"')
        {
            string value;
            int literalStart;
            int literalEnd;
            return TryReadJsonStringLiteral(text, ref index, out value, out literalStart, out literalEnd);
        }

        if (text[index] == '{' || text[index] == '[')
        {
            var depth = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (ch == '"')
                {
                    string ignored;
                    int literalStart;
                    int literalEnd;
                    if (!TryReadJsonStringLiteral(text, ref index, out ignored, out literalStart, out literalEnd))
                    {
                        return false;
                    }

                    continue;
                }

                if (ch == '{' || ch == '[')
                {
                    depth++;
                }
                else if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        index++;
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        while (index < text.Length &&
            text[index] != ',' &&
            text[index] != '}' &&
            text[index] != ']')
        {
            index++;
        }

        return true;
    }

    private static void SkipJsonWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static string ToJsonStringLiteral(string value)
    {
        if (value == null)
        {
            value = "";
        }

        var builder = new StringBuilder();
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
