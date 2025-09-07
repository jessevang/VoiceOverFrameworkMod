using StardewValley;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        /// <summary>Shared page segment representation.</summary>
        private sealed class PageSeg
        {
            public string Actor;
            public string Display; 
            public int PageIndex;
            public string Gender;
        }

        private static readonly HashSet<string> VanillaMarriables =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Abigail","Alex","Elliott","Emily","Haley","Harvey",
                "Leah","Maru","Penny","Sam","Sebastian","Shane", "Krobus"
            };

        private bool IsMarriableCharacter(string name) =>
            !string.IsNullOrWhiteSpace(name) && VanillaMarriables.Contains(name);

        /// <summary>Shared helpers for splitting & sanitizing Stardew Valley dialogue.</summary>
        private static class DialogueUtil
        {
            // ── Command/Token Regexes ────────────────────────────────────────────────
            private static readonly Regex RxLeadingGate = new(@"^\s*(?:[A-Za-z_]+|\d+)#", RegexOptions.Compiled);
            private static readonly Regex RxInlineFlag1 = new(@"#\$1\s+[A-Za-z0-9_]+#", RegexOptions.Compiled);
            private static readonly Regex RxStrayNumber = new(@"^\s*0?\.?\d+\s*", RegexOptions.Compiled);
            private static readonly Regex RxTrailingDollar = new(@"\$(?=[\s,\.!\?\)]|$)", RegexOptions.Compiled);
            private static readonly Regex RxCollapseWS = new(@"[ \t]+", RegexOptions.Compiled);

            private static readonly Regex RxLeadingNarr = new(@"^\s*%+", RegexOptions.Compiled);
            private static readonly Regex RxCmdQuestion = new(@"#?\$q\s+[^#]*#(?<q>[^#]*)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
            private static readonly Regex RxCmdResponse = new(@"#?\$r\s+[^#]*#[^#]*#?", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
            private static readonly Regex RxCmdQuick = new(@"\$y\s+(['""])(?<p>.*?)\1", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
            private static readonly Regex RxCmdOther = new(@"#\$(?:action|k|t|v)\b[^#]*", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
            private static readonly Regex RxOnceOnlyLine = new(@"^\s*\$1\s+\S+#(?<first>.*?)(?:#\$e#.*)?$", RegexOptions.Compiled | RegexOptions.Singleline);
            private static readonly Regex PortraitRegex = new(@"\$(?:h|s|u|l|a|\d+)", RegexOptions.Compiled);
            private static readonly Regex RxCmdChancePrefix = new(@"#?\$c\s*[0-9.]+\s*#", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // NOTE: Replaced the old boundary-based remover with a robust, locale-agnostic one. Used to fix language token not getting removed
            // - Optional percent `%` or full-width `％` (we also normalize to `%` beforehand)
            // - No \b boundaries (CJK adjacency can break those)
            private static readonly Regex RxStripTokens = new(
                @"(?i)(?:%?(?:adj|noun|place|name|spouse|firstnameletter|farm|favorite|kid1|kid2|pet|band|book|season|time))",
                RegexOptions.Compiled
            );

            private static readonly Regex RxAtToken = new(@"@", RegexOptions.Compiled);
            private static readonly Regex RxSquareBracketGrant = new(@"\[[^\]]+\]", RegexOptions.Compiled);

            // ---------- NEW: normalization helper (handles width variants like ％ and general form) ----------
            private static string PreNormalize(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                // Normalize to NFKC so width-variant symbols collapse to ASCII where possible
                s = s.Normalize(NormalizationForm.FormKC);
                // Extra safety: if any full-width percent remains for some reason, convert
                s = s.Replace('％', '%');
                return s;
            }

            public static List<PageSeg> SplitAndSanitize(string raw, bool splitBAsPage = false)
            {
                if (raw == null) raw = string.Empty;

                // --- 0) Expand weekly rotation FIRST (A||B||C -> [A, B, C]) ---
                if (raw.Contains("||"))
                {
                    var variants = raw.Split(new[] { "||" }, StringSplitOptions.None);
                    var merged = new List<PageSeg>();
                    int idx = 0;
                    foreach (var v in variants)
                    {
                        var segs = SplitAndSanitize(v, splitBAsPage);
                        foreach (var p in segs)
                        {
                            p.PageIndex = idx++;
                            merged.Add(p);
                        }
                    }
                    return merged;
                }

                // --- 0b) If there's a naked '|' (not part of $d/$p/$query), split those too ---
                if (raw.Contains("|") && !Regex.IsMatch(raw, @"\$(?:d|p|query)\b", RegexOptions.IgnoreCase))
                {
                    var barParts = raw.Split(new[] { '|' }, StringSplitOptions.None);
                    var merged = new List<PageSeg>();
                    int idx = 0;
                    foreach (var part in barParts)
                    {
                        var segs = SplitAndSanitize(part, splitBAsPage);
                        foreach (var p in segs)
                        {
                            p.PageIndex = idx++;
                            merged.Add(p);
                        }
                    }
                    return merged;
                }

                // $c random A#B
                if (TrySplitRandomChoice(raw, out var rcA, out var rcB))
                {
                    var left = SplitAndSanitize(rcA, splitBAsPage);
                    var right = SplitAndSanitize(rcB, splitBAsPage);
                    var merged = new List<PageSeg>(left.Count + right.Count);
                    int idx = 0;
                    foreach (var p in left) { p.PageIndex = idx++; merged.Add(p); }
                    foreach (var p in right) { p.PageIndex = idx++; merged.Add(p); }
                    return merged;
                }

                // $p prereq A|B
                if (TrySplitPrereq(raw, out var rpA, out var rpB))
                {
                    var left = SplitAndSanitize(rpA, splitBAsPage);
                    var right = SplitAndSanitize(rpB, splitBAsPage);
                    var merged = new List<PageSeg>(left.Count + right.Count);
                    int idx = 0;
                    foreach (var p in left) { p.PageIndex = idx++; merged.Add(p); }
                    foreach (var p in right) { p.PageIndex = idx++; merged.Add(p); }
                    return merged;
                }

                // $d / $query conditional A|B
                if (TrySplitConditionalChoice(raw, out var rdA, out var rdB))
                {
                    var left = SplitAndSanitize(rdA, splitBAsPage);
                    var right = SplitAndSanitize(rdB, splitBAsPage);
                    var merged = new List<PageSeg>(left.Count + right.Count);
                    int idx = 0;
                    foreach (var p in left) { p.PageIndex = idx++; merged.Add(p); }
                    foreach (var p in right) { p.PageIndex = idx++; merged.Add(p); }
                    return merged;
                }

                // --- 2) Now split into pages and lines ---
                var firstLevel = raw.Split(new[] { "#$e#" }, StringSplitOptions.None);

                var normalizedPages = new List<string>();
                foreach (var chunk in firstLevel)
                {
                    string c = chunk ?? "";

                    IEnumerable<string> stage2 = splitBAsPage
                        ? c.Split(new[] { "#$b#" }, StringSplitOptions.None)      // treat $b as page
                        : new[] { c.Replace("#$b#", "\n") };                      // or as newline

                    foreach (var part in stage2)
                    {
                        string p = part ?? "";

                        // If this chunk contains interactive tokens, DON'T split on bare '#'
                        if (p.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.IndexOf("$r", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.IndexOf("$y", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!string.IsNullOrWhiteSpace(p))
                                normalizedPages.Add(p.Trim());
                            continue;
                        }

                        // Otherwise, split on a plain '#' that is NOT a command (i.e., not followed by '$')
                        var pieces = Regex.Split(p, @"#(?!\$)");
                        foreach (var piece in pieces)
                        {
                            var byNewline = Regex.Split(piece ?? "", @"\r?\n");
                            foreach (var leaf in byNewline)
                                if (!string.IsNullOrWhiteSpace(leaf))
                                    normalizedPages.Add(leaf.Trim());
                        }
                    }
                }

                // --- 3) Build PageSegs, expanding $y / $q/$r and gender variants per leaf ---
                var pages = new List<PageSeg>();
                int nextIndex = 0;

                for (int i = 0; i < normalizedPages.Count; i++)
                {
                    string text = normalizedPages[i]?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // First expand interactive ($y / $q/$r)
                    List<string> bodies;
                    if (TryExpandInteractive(text, out var expanded))
                        bodies = expanded;
                    else
                        bodies = new List<string> { text };

                    // For each interactive body, expand gender in the requested order
                    foreach (var body in bodies)
                    {
                        var variants = ExpandGenderVariantsOrdered(body);
                        if (variants == null || variants.Count == 0)
                        {
                            AddPageIfNotEmpty(pages, body, nextIndex++, null);
                            continue;
                        }

                        foreach (var (gender, genderedText) in variants)
                            AddPageIfNotEmpty(pages, genderedText, nextIndex++, gender);
                    }
                }

                return pages;
            }

            // Expand $p prerequisite anywhere in the string.
            private static bool TrySplitPrereq(string text, out string a, out string b)
            {
                a = b = null;
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                var m = Regex.Match(
                    text,
                    @"^\s*(?<pre>.*?)(?:#?\$p\b[^#]*#)(?<a>.+?)\|(?<b>.+)$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

                if (!m.Success)
                    return false;

                string pre = m.Groups["pre"].Value;
                string optA = m.Groups["a"].Value;
                string optB = m.Groups["b"].Value;

                a = pre + optA;
                b = pre + optB;
                return true;
            }

            // Expand $y 'prompt_opt1_resp1_opt2_resp2' into: [prompt, resp1, resp2, ...]
            private static bool TryExpandInteractive(string text, out List<string> outputs)
            {
                outputs = null;
                if (string.IsNullOrWhiteSpace(text)) return false;

                // ---- Robust $y parsing (allow inner apostrophes like you'll) ----
                int yIdx = text.IndexOf("$y", StringComparison.OrdinalIgnoreCase);
                if (yIdx >= 0)
                {
                    int qStart = -1;
                    for (int i = yIdx + 2; i < text.Length; i++)
                    {
                        char ch = text[i];
                        if (ch == '\'' || ch == '"') { qStart = i; break; }
                        if (!char.IsWhiteSpace(ch)) break;
                    }

                    if (qStart >= 0)
                    {
                        char quote = text[qStart];
                        int qEnd = text.LastIndexOf(quote);
                        if (qEnd > qStart)
                        {
                            string payload = text.Substring(qStart + 1, qEnd - (qStart + 1));

                            // normalize like the game: "*" -> linebreak/page break
                            payload = payload.Replace("**", "<<<<asterisk>>>>")
                                             .Replace("*", "#$b#")
                                             .Replace("<<<<asterisk>>>>", "*");

                            var bits = payload.Split('_');
                            var tmp = new List<string>();
                            if (bits.Length > 0)
                            {
                                // 0 = prompt
                                var prompt = bits[0];
                                if (!string.IsNullOrWhiteSpace(prompt))
                                    tmp.Add(prompt.Trim());

                                // pairs: [choice, reply]
                                for (int k = 1; k + 1 < bits.Length; k += 2)
                                {
                                    var reply = bits[k + 1] ?? "";
                                    foreach (var seg in reply.Split(new[] { "#$b#" }, StringSplitOptions.None))
                                        if (!string.IsNullOrWhiteSpace(seg))
                                            tmp.Add(seg.Trim());
                                }
                            }

                            if (tmp.Count > 0)
                            {
                                outputs = tmp;
                                return true;
                            }
                        }
                    }
                }

                // ---- $q / $r expansion ----
                if (text.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("$r", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var segs = text.Split('#');
                    var tmp = new List<string>();
                    for (int i = 0; i < segs.Length; i++)
                    {
                        var seg = segs[i]?.Trim() ?? "";
                        if (seg.Length == 0) continue;

                        if (seg.StartsWith("$q", StringComparison.OrdinalIgnoreCase))
                        {
                            if (i + 1 < segs.Length && !string.IsNullOrWhiteSpace(segs[i + 1]))
                                tmp.Add(segs[i + 1].Trim());
                        }
                        else if (seg.StartsWith("$r", StringComparison.OrdinalIgnoreCase))
                        {
                            if (i + 1 < segs.Length && !string.IsNullOrWhiteSpace(segs[i + 1]))
                            {
                                var reply = segs[i + 1];
                                foreach (var rseg in reply.Split(new[] { "#$b#" }, StringSplitOptions.None))
                                    if (!string.IsNullOrWhiteSpace(rseg))
                                        tmp.Add(rseg.Trim());
                            }
                        }
                    }

                    if (tmp.Count > 0)
                    {
                        outputs = tmp;
                        return true;
                    }
                }

                return false;
            }

            private static void AddPageIfNotEmpty(List<PageSeg> pages, string body, int pageIndex, string gender)
            {
                if (string.IsNullOrWhiteSpace(body)) return;

                string actor = SanitizeForActor(body);
                string display = SanitizeForDisplayPattern(body);

                pages.Add(new PageSeg
                {
                    Actor = actor,
                    Display = display,
                    PageIndex = pageIndex,
                    Gender = gender
                });
            }

            private static string SanitizeForDisplayPattern(string s)
            {
                if (string.IsNullOrWhiteSpace(s))
                    return string.Empty;

                // --- NEW: normalize width & form first (handles ％ vs % etc) ---
                s = PreNormalize(s);

                // If it's a once-only line with an $e fallback, keep only the first-time text.
                if (RxOnceOnlyLine.IsMatch(s))
                    s = RxOnceOnlyLine.Replace(s, m => m.Groups["first"].Value);

                s = RxLeadingGate.Replace(s, "");
                s = RxInlineFlag1.Replace(s, "");
                s = RxStrayNumber.Replace(s, "");
                s = RxLeadingNarr.Replace(s, "");

                // keep only $q prompt, drop $r tokens (the replies themselves were expanded above)
                s = RxCmdQuestion.Replace(s, m => " " + (m.Groups["q"].Value ?? ""));
                s = RxCmdResponse.Replace(s, "");

                // remove '$c p#' prefix if present (we already expanded $c earlier)
                s = RxCmdChancePrefix.Replace(s, "");

                // turn plain '#' separators (not commands) into spaces
                s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

                // don't render $y here; TryExpandInteractive already handled it
                s = RxCmdOther.Replace(s, "");
                s = RxTrailingDollar.Replace(s, "");

                // strip bracketed grant tags like [Happy2]
                s = RxSquareBracketGrant.Replace(s, "");

                // gameplay-only tokens that should never appear on-screen
                s = Regex.Replace(s, @"%fork\d*\b", "", RegexOptions.IgnoreCase); // strip %fork*
                s = Regex.Replace(s, @"%noturn\b", "", RegexOptions.IgnoreCase);  // strip %noturn
                s = Regex.Replace(s, @"\s*\$k\b", "", RegexOptions.IgnoreCase);   // strip bare $k

                // --- CRITICAL CHANGE: remove variable %tokens robustly (ASCII or full-width or bare) ---
                // we pre-normalized, but this also handles bare kid1/kid2 cases
                s = RxStripTokens.Replace(s, "");

                // remove '@' (player name placeholder)
                s = RxAtToken.Replace(s, "");

                // drop portrait codes and tidy whitespace
                s = PortraitRegex.Replace(s, "");
                s = RxCollapseWS.Replace(s, " ");
                s = Regex.Replace(s, @"\s{2,}", " ").Trim();
                return s;
            }

            private static string StripControlTokensKeepPortraits(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;

                if (RxOnceOnlyLine.IsMatch(s))
                    s = RxOnceOnlyLine.Replace(s, m => m.Groups["first"].Value);

                s = RxLeadingGate.Replace(s, "");
                s = RxInlineFlag1.Replace(s, "");
                s = RxStrayNumber.Replace(s, "");
                s = RxLeadingNarr.Replace(s, "");

                s = RxCmdQuestion.Replace(s, m => " " + m.Groups["q"].Value);
                s = RxCmdResponse.Replace(s, "");

                // remove '$c p#' prefix if present (we already expanded $c earlier)
                s = RxCmdChancePrefix.Replace(s, "");

                // convert plain '#' to spaces
                s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

                // Drop any residual $y payload from this line entirely.
                s = Regex.Replace(s, @"\s*\$y\b.*$", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                // Explicitly remove %fork (not rendered to the player).
                s = Regex.Replace(s, @"%fork\b", "", RegexOptions.IgnoreCase);

                s = RxCmdOther.Replace(s, "");
                s = RxTrailingDollar.Replace(s, "");

                return RxCollapseWS.Replace(s, " ").Trim();
            }

            private static string SanitizeForActor(string s)
            {
                s = StripControlTokensKeepPortraits(s);
                s = RxAtToken.Replace(s, "{Farmer_Name}");
                s = PortraitRegex.Replace(s, m =>
                {
                    string code = m.Value.Substring(1);
                    string tag = code switch
                    {
                        "0" => "Neutral",
                        "1" or "h" => "Happy",
                        "2" or "s" => "Sad",
                        "3" or "u" => "Unique",
                        "4" or "l" => "Love",
                        "5" or "a" => "Angry",
                        _ => int.TryParse(code, out _) ? $"Custom:{code}" : "Unknown"
                    };
                    return $" {{Portrait:{tag}}}";
                });
                return Regex.Replace(s, @"\s{2,}", " ").Trim();
            }

            // unchanged helpers below …
            private static bool TrySplitRandomChoice(string text, out string a, out string b)
            {
                a = b = null;
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                var m = Regex.Match(
                    text,
                    @"^\s*(?<pre>.*?)(?:#?\$c\s*[0-9.]+\s*#)(?<a>.+?)#(?<b>.+)$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

                if (!m.Success)
                    return false;

                string pre = m.Groups["pre"].Value;
                string optA = m.Groups["a"].Value;
                string optB = m.Groups["b"].Value;

                a = pre + optA;
                b = pre + optB;
                return true;
            }

            private static bool TrySplitConditionalChoice(string text, out string a, out string b) =>
                Regex.Match(text ?? "", @"^\s*#?\$(?:d|query|p)\b[^#]*#(.+?)\|(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase) is Match m && m.Success
                    ? (a = m.Groups[1].Value, b = m.Groups[2].Value, true).Item3
                    : (a = b = null, false).Item2;

            private static bool TryTopLevelCaretGender(string text, out string male, out string female)
            {
                male = female = null;
                if (string.IsNullOrEmpty(text) || text.Contains("${")) return false;
                int caret = text.IndexOf('^');
                if (caret <= 0 || caret >= text.Length - 1) return false;

                int firstBreakAfterCaret = text.IndexOf('\n', caret + 1);
                if (firstBreakAfterCaret >= 0)
                {
                    string tail = text.Substring(firstBreakAfterCaret);
                    male = text.Substring(0, caret) + tail;
                    female = text.Substring(caret + 1, firstBreakAfterCaret - (caret + 1)) + tail;
                }
                else
                {
                    male = text.Substring(0, caret);
                    female = text.Substring(caret + 1);
                }
                return true;
            }

            private static bool TryTopLevelGender(string text, out string male, out string female, out string nb)
            {
                male = female = nb = null;
                var m = Regex.Match(text ?? "", @"\$\{([^{}]+)\}");
                if (!m.Success) return false;

                string inner = m.Groups[1].Value;
                char sep = inner.Contains('¦') ? '¦' : '^';
                var parts = inner.Split(sep);
                if (parts.Length < 2) return false;

                string before = text.Substring(0, m.Index);
                string after = text[(m.Index + m.Length)..];

                male = before + parts[0] + after;
                female = before + parts[1] + after;
                if (parts.Length >= 3) nb = before + parts[2] + after;
                return true;
            }

            private static List<(string gender, string text)> ExpandGenderVariantsOrdered(string input)
            {
                var outList = new List<(string gender, string text)>();
                if (string.IsNullOrWhiteSpace(input))
                {
                    outList.Add((null, input ?? string.Empty));
                    return outList;
                }

                // Start with a single seed
                var current = new List<(string gender, string text)> { (null, input) };

                // ── Phase 1: expand all ${male^female} (two-part) tokens first ──
                while (true)
                {
                    bool changed = false;
                    var next = new List<(string gender, string text)>();

                    foreach (var (g, s) in current)
                    {
                        var m = Regex.Match(s ?? "", @"\$\{([^{}]+)\}");
                        if (m.Success)
                        {
                            var inner = m.Groups[1].Value;
                            var parts = Regex.Split(inner, @"\^|¦"); // ^ or ¦
                            if (parts.Length == 2)
                            {
                                string before = s.Substring(0, m.Index);
                                string after = s.Substring(m.Index + m.Length);

                                next.Add((g ?? "male", before + parts[0] + after));
                                next.Add((g ?? "female", before + parts[1] + after));
                                changed = true;
                                continue;
                            }
                        }
                        next.Add((g, s));
                    }

                    current = next;
                    if (!changed) break;
                }

                // ── Phase 2: then expand all ${male^female^non-binary} (three-part) tokens ──
                while (true)
                {
                    bool changed = false;
                    var next = new List<(string gender, string text)>();

                    foreach (var (g, s) in current)
                    {
                        var m = Regex.Match(s ?? "", @"\$\{([^{}]+)\}");
                        if (m.Success)
                        {
                            var inner = m.Groups[1].Value;
                            var parts = Regex.Split(inner, @"\^|¦");
                            if (parts.Length >= 3)
                            {
                                string before = s.Substring(0, m.Index);
                                string after = s.Substring(m.Index + m.Length);

                                next.Add((g ?? "male", before + parts[0] + after));
                                next.Add((g ?? "female", before + parts[1] + after));
                                next.Add((g ?? "nonbinary", before + parts[2] + after));
                                changed = true;
                                continue;
                            }
                        }
                        next.Add((g, s));
                    }

                    current = next;
                    if (!changed) break;
                }

                // ── Phase 3: finally apply whole-line caret split: maleText ^ femaleText ──
                var final = new List<(string gender, string text)>();
                foreach (var (g, s) in current)
                {
                    if (TryTopLevelCaretGender(s, out var male, out var female))
                    {
                        final.Add((g ?? "male", male?.Trim()));
                        final.Add((g ?? "female", female?.Trim()));
                    }
                    else
                    {
                        final.Add((g, s));
                    }
                }

                // Done
                outList.AddRange(final);
                return outList;
            }
        }
    }
}
