using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SparkBar;

public sealed record DefinitionSense(string PartOfSpeech, string Definition, string Example);

public sealed record DefinitionLookupResult(
    bool Success,
    string Word,
    string Phonetic,
    List<DefinitionSense> Senses,
    List<string> Synonyms,
    string SourceName,
    string ErrorMessage);

public sealed record TranslationLookupResult(
    bool Success,
    string SourceLang,
    string TargetLang,
    string TranslatedText,
    string Transliteration,
    List<(string Pos, List<string> Terms)> Alternatives,
    string ErrorMessage);

internal static class LookupService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SparkBar/2.0 (Windows 11; +https://github.com/sujansujan/SparkBar)");
        return c;
    }

    public static async Task<DefinitionLookupResult> LookupDefinitionAsync(string rawText, CancellationToken ct)
    {
        string term = (rawText ?? "").Trim().Trim('"', '\'', '“', '”', '(', ')', '[', ']', ',', ';');
        if (term.Length > 120) term = term[..120].Trim();
        if (string.IsNullOrWhiteSpace(term))
            return new DefinitionLookupResult(false, "", "", new(), new(), "", "Nothing selected to define.");

        string firstWord = term.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? term;
        bool singleWord = !term.Contains(' ');

        // 1. Primary: Free Dictionary API (rich phonetics, parts of speech, definitions, examples, synonyms)
        if (singleWord)
        {
            var dictRes = await TryFreeDictionaryAsync(firstWord, ct).ConfigureAwait(false);
            if (dictRes != null && dictRes.Success && dictRes.Senses.Count > 0)
                return dictRes;
        }

        // 2. Fallback 1: Google Dictionary / Definitions JSON endpoint
        var googleRes = await TryGoogleDictionaryAsync(term, ct).ConfigureAwait(false);
        if (googleRes != null && googleRes.Success && googleRes.Senses.Count > 0)
            return googleRes;

        // 3. Fallback 2: Wikipedia REST Summary API (great for proper nouns, acronyms, tech terms, phrases)
        var wikiRes = await TryWikipediaSummaryAsync(term, ct).ConfigureAwait(false);
        if (wikiRes != null && wikiRes.Success && wikiRes.Senses.Count > 0)
            return wikiRes;

        return new DefinitionLookupResult(
            false,
            term,
            "",
            new(),
            new(),
            "",
            $"No offline/online dictionary entry found for \"{term}\". Use Web ↗ to search in your browser.");
    }

    private static async Task<DefinitionLookupResult> TryFreeDictionaryAsync(string word, CancellationToken ct)
    {
        try
        {
            string url = "https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word);
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            string resolvedWord = word;
            string phonetic = "";
            var senses = new List<DefinitionSense>();
            var synonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("word", out var wProp) && wProp.ValueKind == JsonValueKind.String)
                {
                    string w = wProp.GetString();
                    if (!string.IsNullOrWhiteSpace(w)) resolvedWord = w;
                }

                if (string.IsNullOrEmpty(phonetic) && entry.TryGetProperty("phonetic", out var phProp) && phProp.ValueKind == JsonValueKind.String)
                    phonetic = phProp.GetString() ?? "";

                if (string.IsNullOrEmpty(phonetic) && entry.TryGetProperty("phonetics", out var phArr) && phArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in phArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                        {
                            string pt = tProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(pt)) { phonetic = pt; break; }
                        }
                    }
                }

                if (entry.TryGetProperty("meanings", out var meanings) && meanings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in meanings.EnumerateArray())
                    {
                        string pos = m.TryGetProperty("partOfSpeech", out var posP) && posP.ValueKind == JsonValueKind.String
                            ? posP.GetString() ?? "" : "";

                        if (m.TryGetProperty("synonyms", out var mSyn) && mSyn.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in mSyn.EnumerateArray())
                                if (s.ValueKind == JsonValueKind.String && synonyms.Count < 8)
                                    synonyms.Add(s.GetString());
                        }

                        if (m.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
                        {
                            int perPos = 0;
                            foreach (var d in defs.EnumerateArray())
                            {
                                string defText = d.TryGetProperty("definition", out var defP) && defP.ValueKind == JsonValueKind.String
                                    ? defP.GetString() ?? "" : "";
                                string exText = d.TryGetProperty("example", out var exP) && exP.ValueKind == JsonValueKind.String
                                    ? exP.GetString() ?? "" : "";

                                if (d.TryGetProperty("synonyms", out var dSyn) && dSyn.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var s in dSyn.EnumerateArray())
                                        if (s.ValueKind == JsonValueKind.String && synonyms.Count < 8)
                                            synonyms.Add(s.GetString());
                                }

                                if (!string.IsNullOrWhiteSpace(defText))
                                {
                                    senses.Add(new DefinitionSense(pos, defText.Trim(), exText.Trim()));
                                    perPos++;
                                    if (perPos >= 2 || senses.Count >= 6) break;
                                }
                            }
                        }
                        if (senses.Count >= 6) break;
                    }
                }
                if (senses.Count >= 6) break;
            }

            if (senses.Count == 0) return null;
            return new DefinitionLookupResult(true, resolvedWord, phonetic, senses, synonyms.Take(6).ToList(), "Dictionary", "");
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<DefinitionLookupResult> TryGoogleDictionaryAsync(string term, CancellationToken ct)
    {
        try
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=md&dt=bd&dt=t&dj=1&q={Uri.EscapeDataString(term)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var senses = new List<DefinitionSense>();
            var synonyms = new List<string>();

            if (root.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
            {
                foreach (var grp in defs.EnumerateArray())
                {
                    string pos = grp.TryGetProperty("pos", out var posP) && posP.ValueKind == JsonValueKind.String
                        ? posP.GetString() ?? "" : "";
                    if (grp.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in entries.EnumerateArray())
                        {
                            string gloss = e.TryGetProperty("gloss", out var gP) && gP.ValueKind == JsonValueKind.String
                                ? gP.GetString() ?? "" : "";
                            string ex = e.TryGetProperty("example", out var exP) && exP.ValueKind == JsonValueKind.String
                                ? exP.GetString() ?? "" : "";
                            if (!string.IsNullOrWhiteSpace(gloss))
                            {
                                senses.Add(new DefinitionSense(pos, gloss.Trim(), ex.Trim()));
                                if (senses.Count >= 6) break;
                            }
                        }
                    }
                    if (senses.Count >= 6) break;
                }
            }

            if (root.TryGetProperty("dict", out var dictArr) && dictArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dictArr.EnumerateArray())
                {
                    string pos = d.TryGetProperty("pos", out var posP) && posP.ValueKind == JsonValueKind.String
                        ? posP.GetString() ?? "" : "";
                    if (d.TryGetProperty("terms", out var termsP) && termsP.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var t in termsP.EnumerateArray())
                        {
                            if (t.ValueKind == JsonValueKind.String)
                            {
                                string s = t.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                            }
                        }
                        if (list.Count > 0)
                        {
                            if (senses.Count == 0)
                                senses.Add(new DefinitionSense(pos, string.Join(", ", list.Take(6)), ""));
                            foreach (var s in list)
                                if (synonyms.Count < 6 && !string.Equals(s, term, StringComparison.OrdinalIgnoreCase))
                                    synonyms.Add(s);
                        }
                    }
                }
            }

            if (senses.Count == 0) return null;
            return new DefinitionLookupResult(true, term, "", senses, synonyms, "Google Dict", "");
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<DefinitionLookupResult> TryWikipediaSummaryAsync(string term, CancellationToken ct)
    {
        try
        {
            string titleSlug = term.Replace(' ', '_');
            string url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(titleSlug);
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string type = root.TryGetProperty("type", out var tP) && tP.ValueKind == JsonValueKind.String ? tP.GetString() ?? "" : "";
            if (string.Equals(type, "https://mediawiki.org/wiki/HyperSwitch/errors/not_found", StringComparison.OrdinalIgnoreCase))
                return null;

            string title = root.TryGetProperty("title", out var titleP) && titleP.ValueKind == JsonValueKind.String ? titleP.GetString() ?? term : term;
            string desc = root.TryGetProperty("description", out var descP) && descP.ValueKind == JsonValueKind.String ? descP.GetString() ?? "" : "";
            string extract = root.TryGetProperty("extract", out var extP) && extP.ValueKind == JsonValueKind.String ? extP.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(extract)) return null;
            if (extract.Length > 340) extract = extract[..339].TrimEnd() + "…";

            var senses = new List<DefinitionSense>
            {
                new(string.IsNullOrWhiteSpace(desc) ? "summary" : desc, extract, "")
            };
            return new DefinitionLookupResult(true, title, "", senses, new(), "Wikipedia", "");
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<TranslationLookupResult> TranslateAsync(string rawText, string targetLang, CancellationToken ct)
    {
        string text = (rawText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationLookupResult(false, "", targetLang, "", "", new(), "Nothing selected to translate.");

        string tl = string.IsNullOrWhiteSpace(targetLang) ? "en" : targetLang.Trim();
        try
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(tl)}&dt=t&dt=bd&dt=rm&dj=1&q={Uri.EscapeDataString(text)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new TranslationLookupResult(false, "", tl, "", "", new(), $"Translation service returned HTTP {(int)resp.StatusCode}.");

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string srcLang = root.TryGetProperty("src", out var srcP) && srcP.ValueKind == JsonValueKind.String
                ? (srcP.GetString() ?? "auto").ToUpperInvariant() : "AUTO";

            var transSb = new StringBuilder();
            var translitSb = new StringBuilder();

            if (root.TryGetProperty("sentences", out var sents) && sents.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sents.EnumerateArray())
                {
                    if (s.TryGetProperty("trans", out var tp) && tp.ValueKind == JsonValueKind.String)
                        transSb.Append(tp.GetString());
                    if (s.TryGetProperty("translit", out var trp) && trp.ValueKind == JsonValueKind.String)
                    {
                        string tr = trp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(tr))
                        {
                            if (translitSb.Length > 0) translitSb.Append(' ');
                            translitSb.Append(tr);
                        }
                    }
                }
            }

            var alts = new List<(string Pos, List<string> Terms)>();
            if (root.TryGetProperty("dict", out var dictArr) && dictArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dictArr.EnumerateArray())
                {
                    string pos = d.TryGetProperty("pos", out var posP) && posP.ValueKind == JsonValueKind.String
                        ? posP.GetString() ?? "" : "";
                    var terms = new List<string>();
                    if (d.TryGetProperty("terms", out var termsP) && termsP.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in termsP.EnumerateArray())
                        {
                            if (t.ValueKind == JsonValueKind.String)
                            {
                                string v = t.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(v) && terms.Count < 5)
                                    terms.Add(v);
                            }
                        }
                    }
                    if (terms.Count > 0 && alts.Count < 3)
                        alts.Add((pos, terms));
                }
            }

            string translated = transSb.ToString().Trim();
            if (string.IsNullOrEmpty(translated))
                return new TranslationLookupResult(false, srcLang, tl, "", "", alts, "Empty translation returned.");

            return new TranslationLookupResult(true, srcLang, tl, translated, translitSb.ToString().Trim(), alts, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TranslationLookupResult(false, "", tl, "", "", new(), $"Network error: {ex.Message}");
        }
    }
}
