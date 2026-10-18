using System.Globalization;
using System.Text;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Services;

public enum SearchOp { Eq, Lt, Lte, Gt, Gte }

/// <summary>Fields that compare as numbers. Ranked is unix seconds.</summary>
public enum NumField { Stars, Length, Bpm, Ar, Cs, Od, Hp, Combo, Plays, Ranked }

public enum TextField { Artist, Title, Creator, Difficulty }

public sealed record NumFilter(NumField Field, SearchOp Op, double Value);

public sealed record TextFilter(TextField Field, string Value);

/// <summary>
/// A search box parsed into the shape the query can act on: bare words that match any of
/// the four text columns, field filters that match one, and comparisons against numbers.
/// </summary>
public sealed record BrowseSearch(
    IReadOnlyList<string> Words,
    IReadOnlyList<TextFilter> Fields,
    IReadOnlyList<NumFilter> Numbers,
    BeatmapStatus? Status)
{
    public static readonly BrowseSearch Empty = new([], [], [], null);

    /// <summary>
    /// Terms past this are ignored. Every term becomes one more nested WHERE, and EF walks
    /// that nesting recursively: a few thousand one-letter words — a single anonymous GET —
    /// overflowed the stack and took the whole process down, and a thousand already broke
    /// SQLite's expression depth. Sixteen is far past any real search.
    /// </summary>
    public const int MaxTerms = 16;

    /// <summary>Characters past this are ignored, so no single term becomes an outsized LIKE pattern.</summary>
    public const int MaxLength = 256;

    public bool IsEmpty => Words.Count == 0 && Fields.Count == 0 && Numbers.Count == 0 && Status is null;

    /// <summary>
    /// Parsing is total, as everywhere else here: a term that means nothing as a filter is
    /// kept as a word rather than rejected. Someone searching for a map called "10:00" gets
    /// a search, not an error message. The same goes for the bounds above — an oversized
    /// search is cut short, not refused.
    /// </summary>
    public static BrowseSearch Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        if (raw.Length > MaxLength)
        {
            // Never cut between the halves of a surrogate pair.
            var cut = char.IsHighSurrogate(raw[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            raw = raw[..cut];
        }

        var words = new List<string>();
        var fields = new List<TextFilter>();
        var numbers = new List<NumFilter>();
        BeatmapStatus? status = null;

        foreach (var token in Tokenize(raw).Take(MaxTerms))
        {
            var (key, op, value) = Split(token);
            if (key is null)
            {
                words.Add(token);
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "artist": fields.Add(new TextFilter(TextField.Artist, value)); break;
                case "title" or "song": fields.Add(new TextFilter(TextField.Title, value)); break;
                case "creator" or "mapper": fields.Add(new TextFilter(TextField.Creator, value)); break;
                case "difficulty" or "diff" or "version": fields.Add(new TextFilter(TextField.Difficulty, value)); break;

                // Every counted map is ranked or approved, so this separates those two
                // rather than reaching for the graveyard — the same word osu!web uses,
                // narrowed to the only values this catalogue holds.
                case "status" when ParseStatus(value) is { } s: status = s; break;

                // Each of these is guarded on its value parsing, so "stars>soon" falls to
                // the default and is searched for as text rather than silently becoming a
                // filter that matches everything.
                case "stars" or "star" or "sr" when ParseNumber(value) is { } n: AddStars(numbers, op, n); break;
                case "length" or "len" when ParseLength(value) is { } n: numbers.Add(new(NumField.Length, op, n)); break;
                case "bpm" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Bpm, op, n)); break;
                case "ar" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Ar, op, n)); break;
                case "cs" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Cs, op, n)); break;
                case "od" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Od, op, n)); break;
                case "hp" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Hp, op, n)); break;
                case "combo" or "maxcombo" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Combo, op, n)); break;
                case "plays" or "playcount" when ParseNumber(value) is { } n: numbers.Add(new(NumField.Plays, op, n)); break;
                case "ranked" or "year" when ParseDate(value) is { } r: AddRanked(numbers, op, r); break;

                default: words.Add(token); break;
            }
        }

        return new BrowseSearch(words, fields, numbers, status);
    }

    /// <summary>
    /// stars=6 means the sixth star band, not the vanishingly unlikely 6.000. Star rating
    /// is a computed float nobody can type exactly, and bands are how this app already
    /// talks about difficulty. Every other numeric field is a value the mapper authored,
    /// so there = means exactly that.
    /// </summary>
    private static void AddStars(List<NumFilter> into, SearchOp op, double v)
    {
        if (op == SearchOp.Eq)
        {
            into.Add(new NumFilter(NumField.Stars, SearchOp.Gte, v));
            into.Add(new NumFilter(NumField.Stars, SearchOp.Lt, v + 1));
        }
        else into.Add(new NumFilter(NumField.Stars, op, v));
    }

    /// <summary>
    /// ranked=2019 is a year, not an instant, so equality becomes the range it stands for.
    /// Comparisons resolve to the same unix seconds the shadow column stores, because
    /// SQLite cannot compare the DateTimeOffset the display value is kept in.
    /// </summary>
    private static void AddRanked(List<NumFilter> into, SearchOp op, (double From, double To) range)
    {
        var (from, to) = range;

        switch (op)
        {
            case SearchOp.Eq:
                into.Add(new NumFilter(NumField.Ranked, SearchOp.Gte, from));
                into.Add(new NumFilter(NumField.Ranked, SearchOp.Lt, to));
                break;
            // "after 2019" means after all of it; "before 2019" means before any of it.
            case SearchOp.Gt: into.Add(new NumFilter(NumField.Ranked, SearchOp.Gte, to)); break;
            case SearchOp.Gte: into.Add(new NumFilter(NumField.Ranked, SearchOp.Gte, from)); break;
            case SearchOp.Lt: into.Add(new NumFilter(NumField.Ranked, SearchOp.Lt, from)); break;
            case SearchOp.Lte: into.Add(new NumFilter(NumField.Ranked, SearchOp.Lt, to)); break;
        }
    }

    private static BeatmapStatus? ParseStatus(string v) => v.ToLowerInvariant() switch
    {
        "ranked" => BeatmapStatus.Ranked,
        "approved" => BeatmapStatus.Approved,
        _ => null
    };

    /// <summary>
    /// Finite numbers only. "nan", "infinity" and "1e999" all parse as doubles, but the value
    /// is inlined into the SQL as a literal, and SQLite reads NaN or Infinity as a column
    /// name — so the page failed with "no such column" for anyone opening that link.
    /// </summary>
    private static double? ParseNumber(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    /// <summary>Seconds, or the m:ss the table shows — both are what someone would type.</summary>
    private static double? ParseLength(string v)
    {
        var parts = v.Split(':');
        if (parts.Length == 1) return ParseNumber(v);

        double total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return double.IsFinite(total) ? total : null;
    }

    /// <summary>
    /// A year, a month or a day, as the half-open unix range it covers. Every part that is
    /// present has to be one: "2019-13" is a typo, not a request for all of 2019, and only by
    /// staying text does it show up as one in the parsed summary.
    /// </summary>
    private static (double From, double To)? ParseDate(string v)
    {
        var parts = v.Split('-');
        if (parts.Length > 3) return null;

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return null;

        var year = values[0];
        int? month = parts.Length > 1 ? values[1] : null;
        int? day = parts.Length > 2 ? values[2] : null;
        if (year is < 2007 or > 2200 || month is < 1 or > 12 || day is < 1 or > 31) return null;

        try
        {
            var from = new DateTimeOffset(year, month ?? 1, day ?? 1, 0, 0, 0, TimeSpan.Zero);
            var to = day is not null ? from.AddDays(1) : month is not null ? from.AddMonths(1) : from.AddYears(1);
            return (from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds());
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Splits key:op:value, or returns a null key when the token is just a word.</summary>
    private static (string? Key, SearchOp Op, string Value) Split(string token)
    {
        for (var i = 1; i < token.Length; i++)
        {
            var c = token[i];
            if (c is not ('=' or '<' or '>')) continue;

            var twoChar = i + 1 < token.Length && token[i + 1] == '=';
            var op = c switch
            {
                '<' => twoChar ? SearchOp.Lte : SearchOp.Lt,
                '>' => twoChar ? SearchOp.Gte : SearchOp.Gt,
                _ => SearchOp.Eq
            };

            var valueStart = i + (twoChar && c != '=' ? 2 : 1);
            var value = token[valueStart..].Trim('"');
            return value.Length == 0 ? (null, op, "") : (token[..i], op, value);
        }
        return (null, SearchOp.Eq, "");
    }

    /// <summary>
    /// Whitespace-separated, except inside quotes — so artist="Toby Fox" is one term, and
    /// so is creator="nao" when the name has a space in it.
    /// </summary>
    private static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in raw)
        {
            if (c == '"') { quoted = !quoted; continue; }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
