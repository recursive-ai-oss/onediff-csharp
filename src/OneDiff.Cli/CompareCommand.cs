using System.Globalization;
using OneDiff.Documents;

namespace OneDiff.Cli;

/// <summary>
/// <c>onediff compare &lt;old&gt; &lt;new&gt; &lt;output&gt;</c>: compares two documents and writes the redline. A thin layer over
/// <see cref="Redline"/>: inputs are recognized by content, the output form follows the output extension unless
/// <c>--form</c> says otherwise, and the look and rules come from the named settings and style presets.
/// </summary>
internal static class CompareCommand
{
    public const string Summary = "compare <old> <new> <output> [options]   compare two documents and write the redline";

    private const string Help = """
        usage: onediff compare <old> <new> <output> [options]
               onediff compare <old> <new> -o <output> [options]

        Compares two documents (text or PDF, recognized by content) and writes the redline. The output form follows the
        extension of <output>: .html (inline), .json (the change list), .pdf (the new PDF with the changes marked on its
        own pages when the new document is a PDF, otherwise a rendered redline).

        options:
          -o, --output <file>                    the output file (instead of the third argument)
          --form <html|html-side-by-side|json>   choose the output form instead of using the extension
          --settings <legal|strict|text>         what to show and hide (default legal)
          --style <blackline|sidebyside|word|developer>
                                                 the look (default: the form's own)
          --title <text>                         page heading and title
          --language <tag>                       language tag written on the page (default en)
          --timestamp <ISO 8601>                 print this time in the footer (the clock is never read)
          --no-cover  --no-footer  --no-bars     leave out the cover page, the footer, the change bars
          --type-tags                            tag each change with its type
          -h, --help                             show this text
        """;

    public static int Run(string[] args, CliIo io)
    {
        var positional = new List<string>();
        string? output = null;
        string? form = null, settingsName = null, styleName = null, title = null, language = null, timestamp = null;
        bool noCover = false, noFooter = false, noBars = false, typeTags = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    io.Write(null, TextEncoding.Utf8Bytes(Help + "\n"));
                    return 0;
                case "--no-cover":
                    noCover = true;
                    break;
                case "--no-footer":
                    noFooter = true;
                    break;
                case "--no-bars":
                    noBars = true;
                    break;
                case "--type-tags":
                    typeTags = true;
                    break;
                case "-o" or "--output":
                    if (i + 1 >= args.Length)
                    {
                        return Fail(io, $"{a} needs a value");
                    }

                    output = args[++i];
                    break;
                case "--form" or "--settings" or "--style" or "--title" or "--language" or "--timestamp":
                    if (i + 1 >= args.Length)
                    {
                        return Fail(io, $"{a} needs a value");
                    }

                    string value = args[++i];
                    switch (a)
                    {
                        case "--form":
                            form = value;
                            break;
                        case "--settings":
                            settingsName = value;
                            break;
                        case "--style":
                            styleName = value;
                            break;
                        case "--title":
                            title = value;
                            break;
                        case "--language":
                            language = value;
                            break;
                        default:
                            timestamp = value;
                            break;
                    }

                    break;
                default:
                    if (a.StartsWith('-') && a.Length > 1)
                    {
                        return Fail(io, $"unknown option '{a}'");
                    }

                    positional.Add(a);
                    break;
            }
        }

        if (output is not null)
        {
            positional.Add(output);
        }

        if (positional.Count != 3 || (output is not null && args.Count(x => x is "-o" or "--output") > 1))
        {
            return Fail(io, "expected <old> <new> <output>");
        }

        CompareSettings? settings = settingsName?.ToLowerInvariant() switch
        {
            null or "legal" => CompareSettings.Legal,
            "strict" => CompareSettings.Strict with { ThrowOnFailure = false },
            "text" or "textonly" => CompareSettings.TextOnly,
            _ => null,
        };
        if (settings is null)
        {
            return Fail(io, $"unknown settings '{settingsName}' (legal, strict, text)");
        }

        RedlineForm? chosen = null;
        if (form is not null)
        {
            chosen = form.ToLowerInvariant() switch
            {
                "html" => RedlineForm.Html,
                "html-side-by-side" or "side-by-side" => RedlineForm.HtmlSideBySide,
                "json" => RedlineForm.Json,
                _ => null,
            };
            if (chosen is null)
            {
                return Fail(io, $"unknown form '{form}' (html, html-side-by-side, json)");
            }
        }

        RedlineStyle? style = styleName?.ToLowerInvariant() switch
        {
            null => null,
            "blackline" => RedlineStyle.Blackline,
            "sidebyside" or "side-by-side" => RedlineStyle.SideBySide,
            "word" => RedlineStyle.Word,
            "developer" => RedlineStyle.Developer,
            _ => RedlineStyle.Blackline with { Name = "" },
        };
        if (style is { Name: "" })
        {
            return Fail(io, $"unknown style '{styleName}' (blackline, sidebyside, word, developer)");
        }

        DateTimeOffset? when = null;
        if (timestamp is not null)
        {
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return Fail(io, $"cannot read the timestamp '{timestamp}'");
            }

            when = parsed;
        }

        if (style is not null || title is not null || language is not null || when is not null || noCover || noFooter || noBars || typeTags)
        {
            RedlineStyle s = style ?? (ReferenceEquals(chosen, RedlineForm.HtmlSideBySide) ? RedlineStyle.SideBySide : settings.Style);
            s = s with
            {
                Title = title ?? s.Title,
                Language = language ?? s.Language,
                Timestamp = when ?? s.Timestamp,
                CoverPage = s.CoverPage && !noCover,
                Footer = s.Footer && !noFooter,
                ChangeBars = s.ChangeBars && !noBars,
                TypeTags = s.TypeTags || typeTags,
            };
            settings = settings with { Style = s };
        }

        try
        {
            if (chosen is null)
            {
                Redline.Create(positional[0], positional[1], positional[2], settings);
            }
            else
            {
                Redline.Create(positional[0], positional[1], positional[2], chosen, settings);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(io, ex.Message);
        }

        return 0;
    }

    private static int Fail(CliIo io, string message)
    {
        io.Info("onediff compare: " + message);
        return 2;
    }
}
