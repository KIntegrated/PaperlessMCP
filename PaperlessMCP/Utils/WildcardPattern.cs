using System.Text;
using System.Text.RegularExpressions;

namespace PaperlessMCP.Utils;

/// <summary>
/// Pattern matching for attribute-name search. Wildcards are the default because
/// they are what a model reaches for ("find names like acme*") and their
/// translation to regex is fully escaped — no surprise quantifiers ('Acme?' in
/// regex means optional 'e', in a glob it means one character) and no ReDoS
/// surface by construction. Raw regex is an explicit opt-in and is compiled with
/// a hard match timeout so a pathological pattern cannot hang the call.
/// </summary>
public static class WildcardPattern
{
    /// <summary>Hard cap on regex scanning per IsMatch, guarding against pathological patterns.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compiles a wildcard or regex pattern into a full-string matcher.
    /// </summary>
    /// <returns>false with an actionable error when the pattern is empty or invalid.</returns>
    public static bool TryCompile(
        string pattern,
        bool regexMode,
        bool ignoreCase,
        out Regex? regex,
        out string? error)
    {
        regex = null;
        error = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Pattern must not be empty";
            return false;
        }

        string source;
        if (regexMode)
        {
            source = pattern;
            try
            {
                // Validate eagerly so an invalid pattern becomes a VALIDATION error
                // with the regex message instead of an unhandled exception.
                _ = new Regex(source, RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"Invalid regex pattern: {ex.Message}";
                return false;
            }
        }
        else
        {
            // Glob semantics, anchored at both ends: '*' matches any run (including
            // empty), '?' exactly one character, everything else is escaped literal.
            var sb = new StringBuilder("^");
            foreach (var ch in pattern)
            {
                switch (ch)
                {
                    case '*':
                        sb.Append(".*");
                        break;
                    case '?':
                        sb.Append('.');
                        break;
                    default:
                        sb.Append(Regex.Escape(ch.ToString()));
                        break;
                }
            }

            sb.Append('$');
            source = sb.ToString();
        }

        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        regex = new Regex(source, options, MatchTimeout);
        return true;
    }

    /// <summary>
    /// Longest literal run in a wildcard pattern, usable as a server-side
    /// name__icontains prefilter. A chunk that sits between wildcards must appear
    /// in every match, so narrowing the API query with it can never hide a result.
    /// Returns null when no chunk is worth a filtered request (needs at least 3
    /// characters to beat an unfiltered fetch).
    /// </summary>
    public static string? GetLiteralPrefilter(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var chunks = pattern.Split(['*', '?'], StringSplitOptions.RemoveEmptyEntries);
        var longest = chunks.Length > 0 ? chunks.MaxBy(c => c.Length) : null;
        return longest is { Length: >= 3 } ? longest : null;
    }
}
