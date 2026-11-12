using System;

/// <summary>
/// Seed and share-URL rules of the IRONVEIL World Explorer (pure logic, no
/// Unity or browser dependencies, so it can be tested in isolation).
///
/// Canonical form: <c>?seed=847291035&amp;v=1</c>
///  - seed: decimal digits only, leading zeros ignored, 1..2147483647.
///  - v: link format version. Missing means 1. Unknown versions still load
///    the world but are reported, and the canonical URL always carries the
///    current version.
/// </summary>
public static class ExplorerSeedUrl
{
    public const int CurrentVersion = 1;
    public const int MinSeed = 1;
    public const int MaxSeed = int.MaxValue; // 2,147,483,647

    public enum SeedStatus { Valid, Missing, Invalid }

    /// <summary>Result of reading a page URL.</summary>
    public readonly struct Request
    {
        public readonly SeedStatus SeedStatus;
        public readonly int Seed;            // valid only when SeedStatus == Valid
        public readonly string RawSeed;      // decoded text of the seed parameter, or null
        public readonly int Version;         // parsed version, CurrentVersion when missing, -1 when not a number
        public readonly string RawVersion;   // decoded text of the v parameter, or null
        public readonly bool VersionKnown;   // true for a missing or current version

        public Request(SeedStatus seedStatus, int seed, string rawSeed, int version, string rawVersion, bool versionKnown)
        {
            SeedStatus = seedStatus; Seed = seed; RawSeed = rawSeed;
            Version = version; RawVersion = rawVersion; VersionKnown = versionKnown;
        }
    }

    /// <summary>
    /// Validates a seed string: decimal digits only (no sign, spaces, decimal
    /// point or letters), leading zeros ignored, value 1..2147483647.
    /// </summary>
    public static bool TryParseSeed(string text, out int seed)
    {
        seed = 0;
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < '0' || c > '9') return false;
        }
        int start = 0;
        while (start < text.Length - 1 && text[start] == '0') start++;
        int digits = text.Length - start;
        if (digits > 10) return false; // more than 10 significant digits always overflows
        long value = 0;
        for (int i = start; i < text.Length; i++) value = value * 10 + (text[i] - '0');
        if (value < MinSeed || value > MaxSeed) return false;
        seed = (int)value;
        return true;
    }

    /// <summary>
    /// Reads the seed and version parameters of a URL or query string. The
    /// first occurrence of a parameter wins; other parameters are ignored.
    /// </summary>
    public static Request ParseUrl(string url)
    {
        string rawSeed = null, rawVersion = null;
        string query = QueryOf(url);
        if (query.Length > 0)
        {
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                if (key == "seed" && rawSeed == null) rawSeed = Decode(value);
                else if (key == "v" && rawVersion == null) rawVersion = Decode(value);
            }
        }

        SeedStatus status;
        int seed = 0;
        if (rawSeed == null) status = SeedStatus.Missing;
        else status = TryParseSeed(rawSeed, out seed) ? SeedStatus.Valid : SeedStatus.Invalid;

        int version = CurrentVersion;
        bool known = true;
        if (rawVersion != null)
        {
            version = TryParseSeed(rawVersion, out int v) ? v : -1;
            known = version == CurrentVersion;
        }
        return new Request(status, seed, rawSeed, version, rawVersion, known);
    }

    /// <summary>The canonical query string: <c>?seed=N&amp;v=1</c>.</summary>
    public static string CanonicalQuery(int seed)
    {
        if (seed < MinSeed) throw new ArgumentOutOfRangeException(nameof(seed));
        return "?seed=" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&v=" + CurrentVersion;
    }

    /// <summary>
    /// The canonical share URL: the page address without query or fragment,
    /// followed by the canonical query.
    /// </summary>
    public static string CanonicalUrl(string pageUrl, int seed)
    {
        return PageAddress(pageUrl) + CanonicalQuery(seed);
    }

    /// <summary>The URL without its query string and fragment.</summary>
    public static string PageAddress(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        int cut = url.Length;
        int q = url.IndexOf('?');
        int h = url.IndexOf('#');
        if (q >= 0) cut = Math.Min(cut, q);
        if (h >= 0) cut = Math.Min(cut, h);
        return url.Substring(0, cut);
    }

    /// <summary>
    /// Maps 32 random bits to a seed in 1..2147483647. Returns false for the
    /// two inputs that map to 0 so the caller draws again (no modulo bias).
    /// </summary>
    public static bool TrySeedFromRandomBits(uint bits, out int seed)
    {
        seed = (int)(bits & 0x7FFFFFFFu);
        return seed >= MinSeed;
    }

    private static string QueryOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        int h = url.IndexOf('#');
        if (h >= 0) url = url.Substring(0, h);
        int q = url.IndexOf('?');
        return q >= 0 ? url.Substring(q + 1) : string.Empty;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (Exception) { return value; }
    }
}
