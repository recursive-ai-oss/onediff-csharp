using System.Globalization;

namespace OneDiff.Cli;

/// <summary>The options a command accepts, and the parser for them. Short flags may be clustered (<c>-up</c>), a value may follow a short option directly (<c>-U3</c>) or as the next argument, and long options take <c>--name value</c> or <c>--name=value</c>. <c>--</c> ends options and a lone <c>-</c> is an operand (standard input).</summary>
internal sealed class OptionSet
{
    private readonly Dictionary<string, (string Key, bool TakesValue)> names = new(StringComparer.Ordinal);

    public OptionSet Flag(string key, params string[] optionNames) => Add(key, false, optionNames);

    public OptionSet Value(string key, params string[] optionNames) => Add(key, true, optionNames);

    private OptionSet Add(string key, bool takesValue, string[] optionNames)
    {
        foreach (string n in optionNames)
        {
            names[n] = (key, takesValue);
        }

        return this;
    }

    public ParsedArgs Parse(ReadOnlySpan<string> args)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--")
            {
                for (i++; i < args.Length; i++)
                {
                    parsed.Operands.Add(args[i]);
                }

                break;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = a.IndexOf('=', StringComparison.Ordinal);
                string name = eq < 0 ? a : a.Substring(0, eq);
                if (!names.TryGetValue(name, out var option))
                {
                    throw new CliException($"unknown option '{name}'");
                }

                if (!option.TakesValue)
                {
                    if (eq >= 0)
                    {
                        throw new CliException($"option '{name}' takes no value");
                    }

                    parsed.Add(option.Key, string.Empty);
                }
                else if (eq >= 0)
                {
                    parsed.Add(option.Key, a.Substring(eq + 1));
                }
                else if (i + 1 < args.Length)
                {
                    parsed.Add(option.Key, args[++i]);
                }
                else
                {
                    throw new CliException($"option '{name}' needs a value");
                }

                continue;
            }

            if (a.Length > 1 && a[0] == '-')
            {
                for (int c = 1; c < a.Length; c++)
                {
                    string name = "-" + a[c];
                    if (!names.TryGetValue(name, out var option))
                    {
                        throw new CliException($"unknown option '{name}'");
                    }

                    if (!option.TakesValue)
                    {
                        parsed.Add(option.Key, string.Empty);
                        continue;
                    }

                    if (c + 1 < a.Length)
                    {
                        parsed.Add(option.Key, a.Substring(c + 1));
                    }
                    else if (i + 1 < args.Length)
                    {
                        parsed.Add(option.Key, args[++i]);
                    }
                    else
                    {
                        throw new CliException($"option '{name}' needs a value");
                    }

                    break;
                }

                continue;
            }

            parsed.Operands.Add(a);
        }

        return parsed;
    }
}

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);

    public List<string> Operands { get; } = [];

    public void Add(string key, string value)
    {
        if (!values.TryGetValue(key, out List<string>? list))
        {
            values[key] = list = [];
        }

        list.Add(value);
    }

    public bool Has(string key) => values.ContainsKey(key);

    /// <summary>The last value given for the option, or null.</summary>
    public string? Get(string key) => values.TryGetValue(key, out List<string>? l) ? l[^1] : null;

    public IReadOnlyList<string> All(string key) => values.TryGetValue(key, out List<string>? l) ? l : [];

    /// <summary>An option's value as a non-negative integer, or <paramref name="fallback"/> when absent.</summary>
    public int GetInt(string key, string optionName, int fallback)
    {
        string? v = Get(key);
        if (v is null)
        {
            return fallback;
        }

        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            throw new CliException($"option '{optionName}' needs a non-negative number, got '{v}'");
        }

        return n;
    }

    /// <summary>An option's value matched case-insensitively against <paramref name="allowed"/>, or <paramref name="fallback"/> when absent.</summary>
    public T GetChoice<T>(string key, string optionName, T fallback, params (string Name, T Value)[] allowed)
    {
        string? v = Get(key);
        if (v is null)
        {
            return fallback;
        }

        foreach ((string name, T value) in allowed)
        {
            if (string.Equals(name, v, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new CliException($"option '{optionName}': '{v}' is not one of {string.Join(", ", allowed.Select(a => a.Name))}");
    }
}
