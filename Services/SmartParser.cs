using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SparkBar;

internal static class SmartParser
{
    private static readonly Dictionary<string, (double Factor, string TargetUnit)> UnitFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["km"]          = (0.621371, "mi"),  ["kilometer"]   = (0.621371, "mi"),  ["kilometers"]  = (0.621371, "mi"),
        ["mi"]          = (1.60934, "km"),   ["mile"]        = (1.60934, "km"),   ["miles"]       = (1.60934, "km"),
        ["m"]           = (3.28084, "ft"),   ["meter"]       = (3.28084, "ft"),   ["meters"]      = (3.28084, "ft"),
        ["ft"]          = (0.3048, "m"),     ["feet"]        = (0.3048, "m"),     ["foot"]        = (0.3048, "m"),
        ["cm"]          = (0.393701, "in"),  ["centimeter"]  = (0.393701, "in"),  ["centimeters"] = (0.393701, "in"),
        ["in"]          = (2.54, "cm"),      ["inch"]        = (2.54, "cm"),      ["inches"]      = (2.54, "cm"),
        ["mm"]          = (0.0393701, "in"), ["millimeter"]  = (0.0393701, "in"), ["millimeters"] = (0.0393701, "in"),
        ["kg"]          = (2.20462, "lbs"),  ["kilogram"]    = (2.20462, "lbs"),  ["kilograms"]   = (2.20462, "lbs"),
        ["lb"]          = (0.453592, "kg"),  ["lbs"]         = (0.453592, "kg"),  ["pound"]       = (0.453592, "kg"), ["pounds"] = (0.453592, "kg"),
        ["g"]           = (0.035274, "oz"),  ["gram"]        = (0.035274, "oz"),  ["grams"]       = (0.035274, "oz"),
        ["oz"]          = (28.3495, "g"),    ["ounce"]       = (28.3495, "g"),    ["ounces"]      = (28.3495, "g"),
        ["km/h"]        = (0.621371, "mph"), ["kph"]         = (0.621371, "mph"), ["mph"]         = (1.60934, "km/h"),
        ["m/s"]         = (3.6, "km/h"),
        ["l"]           = (0.264172, "gal"), ["liter"]       = (0.264172, "gal"), ["liters"]      = (0.264172, "gal"), ["litre"] = (0.264172, "gal"), ["litres"] = (0.264172, "gal"),
        ["gal"]         = (3.78541, "L"),    ["gallon"]      = (3.78541, "L"),    ["gallons"]     = (3.78541, "L"),
        ["ml"]          = (0.033814, "fl oz"),["milliliter"] = (0.033814, "fl oz"),["milliliters"]= (0.033814, "fl oz"),
        ["floz"]        = (29.5735, "mL"),
        ["sqft"]        = (0.092903, "mÂ²"),  ["sqm"]         = (10.7639, "sq ft"),
        ["tb"]          = (1024.0, "GB"),    ["gb"]          = (1024.0, "MB"),    ["mb"]          = (1024.0, "KB"),   ["kb"] = (1024.0, "B")
    };

    public static bool TryParseHexColor(string input, out string hex6, out Color color)
    {
        hex6 = "";
        color = default;
        string s = (input ?? "").Trim();

        var m = Regex.Match(s, @"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$");
        if (m.Success)
        {
            string h = m.Groups[1].Value.ToUpperInvariant();
            hex6 = h.Length == 3
                ? $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}"
                : h;
            byte r = byte.Parse(hex6[..2], NumberStyles.HexNumber);
            byte g = byte.Parse(hex6.Substring(2, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex6.Substring(4, 2), NumberStyles.HexNumber);
            color = Color.FromRgb(r, g, b);
            return true;
        }

        var mRgb = Regex.Match(s, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*[\d.]+)?\s*\)$", RegexOptions.IgnoreCase);
        if (mRgb.Success &&
            int.TryParse(mRgb.Groups[1].Value, out int rr) && rr <= 255 &&
            int.TryParse(mRgb.Groups[2].Value, out int gg) && gg <= 255 &&
            int.TryParse(mRgb.Groups[3].Value, out int bb) && bb <= 255)
        {
            color = Color.FromRgb((byte)rr, (byte)gg, (byte)bb);
            hex6 = $"{rr:X2}{gg:X2}{bb:X2}";
            return true;
        }

        return false;
    }

    public static (string Hex, string Rgb, string Hsl) FormatColorVariants(Color c)
    {
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string rgb = $"rgb({c.R}, {c.G}, {c.B})";

        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;
        if (Math.Abs(max - min) > 1e-6)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (Math.Abs(max - r) < 1e-6) h = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (Math.Abs(max - g) < 1e-6) h = (b - r) / d + 2.0;
            else h = (r - g) / d + 4.0;
            h /= 6.0;
        }
        string hsl = $"hsl({Math.Round(h * 360):0}, {Math.Round(s * 100):0}%, {Math.Round(l * 100):0}%)";
        return (hex, rgb, hsl);
    }

    public static bool TrySmartConvert(string input, out string shortOut, out string copyOut, out string tipOut)
    {
        shortOut = ""; copyOut = ""; tipOut = "";
        string s = (input ?? "").Trim();
        if (s.Length < 2 || s.Length > 40 || s.Contains('\n'))
            return false;

        var mHex = Regex.Match(s, @"^0x([0-9a-fA-F]{1,12})$");
        if (mHex.Success && long.TryParse(mHex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hexVal))
        {
            copyOut = hexVal.ToString(CultureInfo.InvariantCulture);
            shortOut = "= " + copyOut;
            tipOut = copyOut + " (decimal)";
            return true;
        }

        var mBin = Regex.Match(s, @"^0b([01]{2,32})$", RegexOptions.IgnoreCase);
        if (mBin.Success)
        {
            long binVal = Convert.ToInt64(mBin.Groups[1].Value, 2);
            copyOut = binVal.ToString(CultureInfo.InvariantCulture);
            shortOut = "= " + copyOut;
            tipOut = copyOut + " (decimal)";
            return true;
        }

        var mTs = Regex.Match(s, @"^(\d{10}|\d{13})$");
        if (mTs.Success && long.TryParse(mTs.Groups[1].Value, out long rawTs))
        {
            long ts = mTs.Groups[1].Value.Length == 13 ? rawTs / 1000 : rawTs;
            if (ts >= 946684800 && ts <= 2524608000)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                copyOut = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
                shortOut = "\U0001F552 " + dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                tipOut = copyOut;
                return true;
            }
        }

        var mUnit = Regex.Match(s,
            @"^([+-]?\d+(?:,\d{3})*(?:\.\d+)?)\s*(Â°?\s*[CF]|celsius|fahrenheit|km/h|kph|mph|m/s|km|kilometers?|mi|miles?|m|meters?|ft|feet|foot|cm|centimeters?|in|inch|inches|mm|millimeters?|kg|kilograms?|lbs?|pounds?|g|grams?|oz|ounces?|liters?|litres?|l|gallons?|gal|milliliters?|ml|fl\s*oz|sqft|sq\s*ft|sqm|tb|gb|mb|kb)\b$",
            RegexOptions.IgnoreCase);
        if (mUnit.Success && double.TryParse(mUnit.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        {
            string u = Regex.Replace(mUnit.Groups[2].Value, @"[\sÂ°]+", "").ToLowerInvariant();
            double val;
            string unit;
            if (u is "c" or "celsius")
            {
                val = (num * 9.0 / 5.0) + 32.0;
                unit = "Â°F";
            }
            else if (u is "f" or "fahrenheit")
            {
                val = (num - 32.0) * 5.0 / 9.0;
                unit = "Â°C";
            }
            else if (UnitFactors.TryGetValue(u, out var pair))
            {
                val = num * pair.Factor;
                unit = pair.TargetUnit;
            }
            else return false;

            string fNum = Math.Abs(val - Math.Round(val)) < 0.005
                ? Math.Round(val).ToString("0", CultureInfo.InvariantCulture)
                : val.ToString("0.##", CultureInfo.InvariantCulture);
            copyOut = $"{fNum} {unit}";
            shortOut = $"â‰ˆ {copyOut}";
            tipOut = copyOut;
            return true;
        }

        return false;
    }

    public static bool TryEvalMath(string input, out string resOut)
    {
        resOut = "";
        string s = (input ?? "").Trim();
        if (s.Length < 3 || s.Length > 80 || s.Contains('\n'))
            return false;
        if (!Regex.IsMatch(s, @"^[0-9a-zA-Z+\-*/().%\s^]+$") ||
            (!Regex.IsMatch(s, @"[+\-*/^%]") && !Regex.IsMatch(s, @"\b(sqrt|sin|cos|tan|log|ln|abs|round|floor|ceil)\s*\(", RegexOptions.IgnoreCase)) ||
            !Regex.IsMatch(s, @"\d|pi\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(s, @"^\d{2,4}-\d{1,2}-\d{1,2}$"))
            return false;

        var parser = new ArithParser(s);
        if (!parser.TryEval(out double val) || double.IsNaN(val) || double.IsInfinity(val))
            return false;

        resOut = Math.Abs(val - Math.Round(val)) < 1e-9
            ? Math.Round(val).ToString("0", CultureInfo.InvariantCulture)
            : val.ToString("0.######", CultureInfo.InvariantCulture);
        return !string.Equals(resOut, s, StringComparison.OrdinalIgnoreCase);
    }

    private ref struct ArithParser
    {
        private readonly string _s;
        private int _pos;

        public ArithParser(string s) { _s = s; _pos = 0; }

        public bool TryEval(out double result)
        {
            result = 0;
            try
            {
                double v = ParseAddSub();
                SkipWs();
                if (_pos < _s.Length) return false;
                result = v;
                return true;
            }
            catch { return false; }
        }

        private void SkipWs()
        {
            while (_pos < _s.Length && (_s[_pos] == ' ' || _s[_pos] == '\t'))
                _pos++;
        }

        private double ParseAddSub()
        {
            double left = ParseMulDiv();
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length) break;
                char op = _s[_pos];
                if (op != '+' && op != '-') break;
                _pos++;
                double right = ParseMulDiv();
                left = op == '+' ? left + right : left - right;
            }
            return left;
        }

        private double ParseMulDiv()
        {
            double left = ParsePower();
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length) break;
                char op = _s[_pos];
                if (op != '*' && op != '/' && op != '%') break;
                _pos++;
                double right = ParsePower();
                if ((op == '/' || op == '%') && right == 0)
                    throw new DivideByZeroException();
                left = op == '*' ? left * right : (op == '/' ? left / right : left % right);
            }
            return left;
        }

        private double ParsePower()
        {
            double b = ParseUnary();
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == '^')
            {
                _pos++;
                return Math.Pow(b, ParsePower());
            }
            return b;
        }

        private double ParseUnary()
        {
            SkipWs();
            if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-'))
            {
                char ch = _s[_pos++];
                return ch == '-' ? -ParseUnary() : ParseUnary();
            }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                double v = ParseAddSub();
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] != ')')
                    throw new FormatException();
                _pos++;
                return v;
            }
            if (_pos < _s.Length && char.IsLetter(_s[_pos]))
            {
                int idStart = _pos;
                while (_pos < _s.Length && char.IsLetter(_s[_pos]))
                    _pos++;
                string ident = _s.Substring(idStart, _pos - idStart).ToLowerInvariant();
                if (ident == "pi") return Math.PI;
                if (ident == "e") return Math.E;
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] != '(')
                    throw new FormatException();
                _pos++;
                double arg = ParseAddSub();
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] != ')')
                    throw new FormatException();
                _pos++;
                return ident switch
                {
                    "sqrt"  => arg >= 0 ? Math.Sqrt(arg) : throw new FormatException(),
                    "sin"   => Math.Sin(arg),
                    "cos"   => Math.Cos(arg),
                    "tan"   => Math.Tan(arg),
                    "log"   => arg > 0 ? Math.Log10(arg) : throw new FormatException(),
                    "ln"    => arg > 0 ? Math.Log(arg) : throw new FormatException(),
                    "abs"   => Math.Abs(arg),
                    "round" => Math.Round(arg),
                    "floor" => Math.Floor(arg),
                    "ceil"  => Math.Ceiling(arg),
                    _       => throw new FormatException()
                };
            }
            int start = _pos;
            bool hasDot = false;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || (!hasDot && _s[_pos] == '.')))
            {
                if (_s[_pos] == '.') hasDot = true;
                _pos++;
            }
            if (_pos == start) throw new FormatException();
            return double.Parse(_s.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    public static void GetTextStats(string s, out int words, out int chars, out int lines)
    {
        s ??= "";
        chars = s.Length;
        string trimmed = s.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            words = 0;
            lines = 0;
            return;
        }
        words = Regex.Matches(trimmed, @"\S+").Count;
        lines = 1;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n') lines++;
    }

    public static string CleanUrl(string url)
    {
        string u = (url ?? "").Trim();
        var m = Regex.Match(u, @"^(https?://[^?#]+)(\?[^#]*)?(#.*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return u;
        var kept = new List<string>();
        string q = m.Groups[2].Value;
        if (!string.IsNullOrEmpty(q) && q.Length > 1)
        {
            foreach (string part in q[1..].Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string key = part.Split('=')[0].Trim();
                if (!string.IsNullOrEmpty(key) &&
                    !Regex.IsMatch(key, @"^(utm_.*|fbclid|gclid|dclid|mc_cid|mc_eid|msclkid|ref|_ga|_gl|igshid|si)$", RegexOptions.IgnoreCase))
                {
                    kept.Add(part);
                }
            }
        }
        return m.Groups[1].Value + (kept.Count > 0 ? "?" + string.Join("&", kept) : "") + m.Groups[3].Value;
    }

    public static bool ParseUrlActionCandidates(string rawUrl, out string suggestedName, out List<(string Label, string Template)> candidates, out string defaultTemplate)
    {
        suggestedName = "My Search";
        candidates = new List<(string, string)>();
        defaultTemplate = "";

        string u = (rawUrl ?? "").Trim();
        if (!Regex.IsMatch(u, @"^https?://", RegexOptions.IgnoreCase))
            u = "https://" + u;
        var m = Regex.Match(u, @"^(https?://)([^/?#]+)([^?#]*)(\?[^#]*)?(#.*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        string scheme = m.Groups[1].Value;
        string host = m.Groups[2].Value;
        string path = m.Groups[3].Value;
        string query = m.Groups[4].Value;
        string frag = m.Groups[5].Value;

        string cleanHost = Regex.Replace(host, @"^(www|m|search|docs|developer|en|app)\.", "", RegexOptions.IgnoreCase);
        string[] dp = cleanHost.Split('.');
        if (dp.Length > 0 && !string.IsNullOrEmpty(dp[0]))
            suggestedName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dp[0].ToLowerInvariant());

        var kept = new List<(string Raw, string Key, string Val)>();
        if (!string.IsNullOrEmpty(query) && query.Length > 1)
        {
            foreach (string part in query[1..].Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string[] kv = part.Split('=', 2);
                string k = kv[0].Trim();
                if (!Regex.IsMatch(k, @"^(utm_.*|fbclid|gclid|dclid|mc_cid|mc_eid|msclkid|ref|_ga|_gl|igshid|si)$", RegexOptions.IgnoreCase))
                    kept.Add((part, k, kv.Length >= 2 ? kv[1] : ""));
            }
        }

        if (u.Contains("{selection}", StringComparison.Ordinal))
            candidates.Add(("Keep existing {selection} placeholder", u));
        else if (u.Contains("%s", StringComparison.Ordinal))
            candidates.Add(("Replace %s with {selection}", u.Replace("%s", "{selection}")));

        if (kept.Count > 0)
        {
            var order = new List<int>();
            const string searchKeyPattern = @"^(q|query|s|search|search_query|k|keyword|keywords|term|text|wd|p|ask|prompt)$";
            for (int i = 0; i < kept.Count; i++)
                if (Regex.IsMatch(kept[i].Key, searchKeyPattern, RegexOptions.IgnoreCase))
                    order.Add(i);
            for (int i = 0; i < kept.Count; i++)
                if (!Regex.IsMatch(kept[i].Key, searchKeyPattern, RegexOptions.IgnoreCase))
                    order.Add(i);

            foreach (int tIdx in order)
            {
                var parts = new List<string>();
                for (int i = 0; i < kept.Count; i++)
                    parts.Add(i == tIdx ? kept[i].Key + "={selection}" : kept[i].Raw);
                string val = kept[tIdx].Val;
                string pv = !string.IsNullOrEmpty(val) ? $" (replaces '{(val.Length > 18 ? val[..18] : val)}')" : "";
                candidates.Add(($"Query param ?{kept[tIdx].Key}={{selection}}{pv}", $"{scheme}{host}{path}?{string.Join("&", parts)}{frag}"));
            }
        }

        string tp = path.TrimEnd('/');
        if (!string.IsNullOrEmpty(tp) && tp.Length > 1)
        {
            string[] segs = tp[1..].Split('/');
            if (segs.Length > 0 && !string.IsNullOrEmpty(segs[^1]))
            {
                string lastSeg = segs[^1];
                segs[^1] = "{selection}";
                string trailing = path.EndsWith('/') ? "/" : "";
                candidates.Add(($"Replace path '/{(lastSeg.Length > 20 ? lastSeg[..20] : lastSeg)}' with /{{selection}}", $"{scheme}{host}/{string.Join("/", segs)}{trailing}"));
            }
        }

        string baseUrl = u.TrimEnd('/');
        candidates.Add(("Append /{selection} to URL path", baseUrl + "/{selection}"));
        if (string.IsNullOrEmpty(query))
            candidates.Add(("Append ?q={selection} query parameter", baseUrl + "?q={selection}"));

        defaultTemplate = candidates[0].Template;
        return true;
    }

    public static bool IsAllowedActionUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        var m = Regex.Match(uri.Trim(), @"^([a-zA-Z][a-zA-Z0-9+.-]{1,31}):");
        if (!m.Success) return false;
        string scheme = m.Groups[1].Value.ToLowerInvariant();
        if (scheme is "javascript" or "vbscript" or "data" or "file" or "about" or "cmd" or "powershell" or "wscript" or "cscript" or "mshta")
            return false;
        return true;
    }

    public static bool IsLocalOrAllowedPath(string s, bool allowUnc)
    {
        s = (s ?? "").Trim().Trim('"');
        if (string.IsNullOrEmpty(s) || s.Contains('\n')) return false;
        if (!Regex.IsMatch(s, @"^([A-Za-z]:\\|\\\\)")) return false;
        if (Regex.IsMatch(s, @"[*?<>|]")) return false;
        if (s.StartsWith(@"\\", StringComparison.Ordinal) && !allowUnc) return false;
        return System.IO.File.Exists(s) || System.IO.Directory.Exists(s);
    }

    public static string HintForIndex(int oneBasedIndex)
    {
        if (oneBasedIndex <= 9)
            return ((char)('0' + oneBasedIndex)).ToString();
        string outStr = "";
        int n = oneBasedIndex - 10;
        while (true)
        {
            outStr = ((char)('A' + (n % 26))) + outStr;
            n = (n / 26) - 1;
            if (n < 0) return outStr;
        }
    }

    // ---------------- Text Transforms ----------------
    public static string ToTitleCase(string s) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase((s ?? "").ToLower());

    public static string ToSentenceCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return Regex.Replace(s.ToLower(), @"(^\s*\p{Ll}|[.!?]\s+\p{Ll})", m => m.Value.ToUpper());
    }

    public static List<string> SplitIdentifierWords(string s)
    {
        string spaced = Regex.Replace(Regex.Replace(s ?? "", @"([a-z0-9])([A-Z])", "$1 $2"), @"[^A-Za-z0-9]+", " ");
        return spaced.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
    }

    public static string ToCamelCase(string s)
    {
        var words = SplitIdentifierWords(s);
        if (words.Count == 0) return s;
        var sb = new StringBuilder(words[0]);
        for (int i = 1; i < words.Count; i++)
            sb.Append(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words[i]));
        return sb.ToString();
    }

    public static string ToSnakeCase(string s) => string.Join("_", SplitIdentifierWords(s));
    public static string ToKebabCase(string s) => string.Join("-", SplitIdentifierWords(s));
    public static string ToConstantCase(string s) => string.Join("_", SplitIdentifierWords(s).Select(w => w.ToUpperInvariant()));
    public static string ToPascalCase(string s) => string.Concat(SplitIdentifierWords(s).Select(w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w)));

    public static string NumberLines(string s)
    {
        var lines = Regex.Replace(s ?? "", @"\r\n?", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = $"{i + 1}. {lines[i].TrimStart()}";
        return string.Join("\n", lines);
    }

    public static string BulletLines(string s)
    {
        var lines = Regex.Replace(s ?? "", @"\r\n?", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = Regex.Replace(lines[i].TrimStart(), @"^([â€¢\-*]|\d+\.)\s+", "");
            lines[i] = $"â€¢ {trimmed}";
        }
        return string.Join("\n", lines);
    }

    public static bool TryFormatJson(string s, bool indented, out string formatted)
    {
        formatted = "";
        string t = (s ?? "").Trim();
        if (t.Length < 2 || !((t.StartsWith('{') && t.EndsWith('}')) || (t.StartsWith('[') && t.EndsWith(']'))))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(t);
            formatted = System.Text.Json.JsonSerializer.Serialize(
                doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });
            return true;
        }
        catch { return false; }
    }

    public static bool TryDecodeBase64(string s, out string decoded)
    {
        decoded = "";
        string t = (s ?? "").Trim();
        if (t.Length < 8 || t.Length % 4 != 0 || !Regex.IsMatch(t, @"^[A-Za-z0-9+/]+={0,2}$"))
            return false;
        try
        {
            byte[] bytes = Convert.FromBase64String(t);
            if (bytes.Length == 0 || bytes.Length > 8192) return false;
            string utf8 = Encoding.UTF8.GetString(bytes);
            foreach (char c in utf8)
            {
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                    return false;
            }
            decoded = utf8;
            return true;
        }
        catch { return false; }
    }

    public static string SortLinesAZ(string s)
    {
        var lines = Regex.Replace(s ?? "", @"\r\n?", "\n").Split('\n');
        Array.Sort(lines, StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    public static string DedupeLines(string s)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outLines = new List<string>();
        foreach (string line in Regex.Replace(s ?? "", @"\r\n?", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t))
                outLines.Add(t);
        }
        return string.Join("\n", outLines);
    }

    // ---------------- Pure C# Offline QR Code Generator (ISO/IEC 18004) ----------------
    // Supports Versions 1..20 (up to 858 UTF-8 bytes) at ECC Level L with zero network calls!
    private static readonly int[] TotalCodewords =
    {
        0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
        404, 466, 532, 581, 655, 733, 815, 901, 991, 1085
    };
    private static readonly int[] EccCodewordsPerBlock =
    {
        0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18,
        20, 24, 26, 30, 22, 24, 28, 30, 28, 28
    };
    private static readonly int[] NumErrorCorrectionBlocks =
    {
        0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,
        4, 4, 4, 4, 6, 6, 6, 6, 7, 8
    };
    private static readonly int[][] AlignmentLocations =
    {
        Array.Empty<int>(),
        Array.Empty<int>(),
        new[]{6, 18},
        new[]{6, 22},
        new[]{6, 26},
        new[]{6, 30},
        new[]{6, 34},
        new[]{6, 22, 38},
        new[]{6, 24, 42},
        new[]{6, 26, 46},
        new[]{6, 28, 50},
        new[]{6, 30, 54},
        new[]{6, 32, 58},
        new[]{6, 34, 62},
        new[]{6, 26, 46, 66},
        new[]{6, 26, 48, 70},
        new[]{6, 26, 50, 74},
        new[]{6, 30, 54, 78},
        new[]{6, 30, 56, 82},
        new[]{6, 30, 58, 86},
        new[]{6, 34, 62, 90}
    };

    public static BitmapSource GenerateQrBitmap(string text, int targetPixelSize = 320)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text ?? "");
        int version = 0;
        for (int v = 1; v <= 20; v++)
        {
            int dataCap = TotalCodewords[v] - EccCodewordsPerBlock[v] * NumErrorCorrectionBlocks[v];
            int headerBits = 4 + (v <= 9 ? 8 : 16);
            if (utf8.Length * 8 + headerBits <= dataCap * 8)
            {
                version = v;
                break;
            }
        }
        if (version == 0) return null;

        int totalCw = TotalCodewords[version];
        int eccPerBlock = EccCodewordsPerBlock[version];
        int numBlocks = NumErrorCorrectionBlocks[version];
        int dataCapBytes = totalCw - eccPerBlock * numBlocks;

        // Build bit stream: Byte mode (0100) + length + payload + terminator + pad bytes
        var bits = new List<bool>(dataCapBytes * 8);
        void AppendBits(int val, int len)
        {
            for (int i = len - 1; i >= 0; i--)
                bits.Add(((val >> i) & 1) != 0);
        }
        AppendBits(0b0100, 4);
        AppendBits(utf8.Length, version <= 9 ? 8 : 16);
        foreach (byte b in utf8) AppendBits(b, 8);
        int rem = dataCapBytes * 8 - bits.Count;
        AppendBits(0, Math.Min(4, rem));
        while (bits.Count % 8 != 0) bits.Add(false);

        byte[] dataBytes = new byte[dataCapBytes];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) dataBytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        int fillIdx = bits.Count >> 3;
        for (int i = fillIdx; i < dataCapBytes; i++)
            dataBytes[i] = (i - fillIdx) % 2 == 0 ? (byte)0xEC : (byte)0x11;

        // Split into blocks and compute Reed-Solomon ECC
        int numShortBlocks = numBlocks - (totalCw % numBlocks);
        int shortBlockLen = totalCw / numBlocks;
        int shortDataLen = shortBlockLen - eccPerBlock;

        byte[] rsDiv = BuildRsDivisor(eccPerBlock);
        var dataBlocks = new byte[numBlocks][];
        var eccBlocks = new byte[numBlocks][];
        int offset = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int dLen = shortDataLen + (i < numShortBlocks ? 0 : 1);
            dataBlocks[i] = new byte[dLen];
            Array.Copy(dataBytes, offset, dataBlocks[i], 0, dLen);
            offset += dLen;
            eccBlocks[i] = ComputeRsRemainder(dataBlocks[i], rsDiv, eccPerBlock);
        }

        // Interleave data and ECC codewords
        var interleaved = new List<byte>(totalCw);
        int maxDataLen = shortDataLen + 1;
        for (int i = 0; i < maxDataLen; i++)
            for (int j = 0; j < numBlocks; j++)
                if (i < dataBlocks[j].Length)
                    interleaved.Add(dataBlocks[j][i]);
        for (int i = 0; i < eccPerBlock; i++)
            for (int j = 0; j < numBlocks; j++)
                interleaved.Add(eccBlocks[j][i]);

        // Construct matrix
        int size = version * 4 + 17;
        bool[,] modules = new bool[size, size];
        bool[,] isFunc = new bool[size, size];

        void SetFunc(int r, int c, bool dark)
        {
            if (r >= 0 && r < size && c >= 0 && c < size)
            {
                modules[r, c] = dark;
                isFunc[r, c] = true;
            }
        }

        void DrawFinder(int cr, int cc)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunc(cr + dy, cc + dx, dist != 2 && dist != 4);
                }
        }

        DrawFinder(3, 3);
        DrawFinder(3, size - 4);
        DrawFinder(size - 4, 3);

        for (int i = 0; i < size; i++)
        {
            if (!isFunc[6, i]) SetFunc(6, i, i % 2 == 0);
            if (!isFunc[i, 6]) SetFunc(i, 6, i % 2 == 0);
        }

        int[] aligns = AlignmentLocations[version];
        foreach (int ar in aligns)
            foreach (int ac in aligns)
            {
                if ((ar == 6 && ac == 6) || (ar == 6 && ac == size - 7) || (ar == size - 7 && ac == 6))
                    continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        SetFunc(ar + dy, ac + dx, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        // Reserve format info & dark module
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6) { SetFunc(8, i, false); SetFunc(i, 8, false); }
            if (i < 8) { SetFunc(8, size - 1 - i, false); SetFunc(size - 1 - i, 8, false); }
        }
        SetFunc(size - 8, 8, true);

        // Version info (v >= 7)
        if (version >= 7)
        {
            int remV = version;
            for (int i = 0; i < 12; i++)
                remV = (remV << 1) ^ ((remV >> 11) * 0x1F25);
            int vBits = (version << 12) | remV;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vBits >> i) & 1) != 0;
                int a = size - 11 + (i % 3);
                int b = i / 3;
                SetFunc(a, b, bit);
                SetFunc(b, a, bit);
            }
        }

        // Place codeword bits in zigzag
        int bitIdx = 0;
        int totalBits = interleaved.Count * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int c = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int r = upward ? size - 1 - vert : vert;
                    if (!isFunc[r, c] && bitIdx < totalBits)
                    {
                        bool dark = ((interleaved[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        modules[r, c] = dark;
                        bitIdx++;
                    }
                }
            }
        }

        // Apply Mask 0: (r + c) % 2 == 0
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (!isFunc[r, c] && (r + c) % 2 == 0)
                    modules[r, c] = !modules[r, c];

        // Write Format Info for ECC Level L (01) and Mask 0 (000) -> 0x77C4
        const int formatBits = 0x77C4;
        for (int i = 0; i <= 5; i++) SetFunc(8, i, ((formatBits >> i) & 1) != 0);
        SetFunc(8, 7, ((formatBits >> 6) & 1) != 0);
        SetFunc(8, 8, ((formatBits >> 7) & 1) != 0);
        SetFunc(7, 8, ((formatBits >> 8) & 1) != 0);
        for (int i = 9; i < 15; i++) SetFunc(14 - i, 8, ((formatBits >> i) & 1) != 0);
        for (int i = 0; i < 8; i++) SetFunc(size - 1 - i, 8, ((formatBits >> i) & 1) != 0);
        for (int i = 8; i < 15; i++) SetFunc(8, size - 15 + i, ((formatBits >> i) & 1) != 0);
        SetFunc(size - 8, 8, true);

        // Render crisp bitmap with 4-module quiet zone
        int border = 4;
        int totalMods = size + border * 2;
        int scale = Math.Max(4, targetPixelSize / totalMods);
        int imgPx = totalMods * scale;
        int stride = imgPx * 4;
        byte[] pixels = new byte[imgPx * stride];

        for (int y = 0; y < imgPx; y++)
        {
            int mr = (y / scale) - border;
            for (int x = 0; x < imgPx; x++)
            {
                int mc = (x / scale) - border;
                bool dark = mr >= 0 && mr < size && mc >= 0 && mc < size && modules[mr, mc];
                byte v = dark ? (byte)0x0F : (byte)0xFF;
                int p = y * stride + x * 4;
                pixels[p]     = dark ? (byte)0x2A : (byte)0xFF; // B
                pixels[p + 1] = dark ? (byte)0x17 : (byte)0xFF; // G
                pixels[p + 2] = v;                              // R
                pixels[p + 3] = 0xFF;                           // A
            }
        }

        var bmp = BitmapSource.Create(imgPx, imgPx, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] BuildRsDivisor(int degree)
    {
        byte[] res = new byte[degree];
        res[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                res[j] = GfMul(res[j], root);
                if (j + 1 < degree) res[j] ^= res[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return res;
    }

    private static byte[] ComputeRsRemainder(byte[] data, byte[] divisor, int degree)
    {
        byte[] rem = new byte[degree];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, degree - 1);
            rem[degree - 1] = 0;
            for (int i = 0; i < degree; i++)
                rem[i] ^= GfMul(divisor[i], factor);
        }
        return rem;
    }

    private static byte GfMul(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }
}
