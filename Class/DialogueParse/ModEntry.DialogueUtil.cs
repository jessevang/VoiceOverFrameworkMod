using StardewValley;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        /// <summary>Shared page segment representation.</summary>
        private sealed class PageSeg
        {
            public string Actor;
            public string Display;     // <- This is now pre-sanitized for DisplayPattern matching
            public int PageIndex;
            public string Gender;
        }

        private static readonly HashSet<string> VanillaMarriables =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Abigail","Alex","Elliott","Emily","Haley","Harvey",
                "Leah","Maru","Penny","Sam","Sebastian","Shane"
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
            private static readonly Regex PortraitRegex = new(@"\$(?:h|s|u|l|a|\d+)\b", RegexOptions.Compiled);
            private static readonly Regex RxCmdChancePrefix = new(@"#?\$c\s*[0-9.]+\s*#", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // tokens to REMOVE for DisplayPattern (random + player-specific + time/season/etc)
            private static readonly Regex RxRemovePercentTokens = new(
                @"%(?:adj|noun|place|name|spouse|firstnameletter|farm|favorite|kid1|kid2|pet|firstnameletter|band|book|season|time)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            private static readonly Regex RxAtToken = new(@"@", RegexOptions.Compiled);
            private static readonly Regex RxSquareBracketGrant = new(@"\[[^\]]+\]", RegexOptions.Compiled);

            public static List<PageSeg> SplitAndSanitize(string raw, bool splitBAsPage = false)
            {
                if (raw == null) raw = string.Empty;

                // 0) weekly rotation
                int weeklyIdx = raw.IndexOf("||", StringComparison.Ordinal);
                if (weeklyIdx >= 0)
                    raw = raw[..weeklyIdx];

                // 1) split on explicit page end
                var firstLevel = raw.Split(new[] { "#$e#" }, StringSplitOptions.None);

                var normalizedPages = new List<string>();
                foreach (var chunk in firstLevel)
                {
                    string c = chunk ?? "";

                    IEnumerable<string> stage2;
                    if (splitBAsPage)
                        stage2 = c.Split(new[] { "#$b#" }, StringSplitOptions.None);
                    else
                        stage2 = new[] { c.Replace("#$b#", "\n") };

                    foreach (var part in stage2)
                    {
                        var byNewline = Regex.Split(part ?? "", @"\r?\n");
                        foreach (var leaf in byNewline)
                            if (!string.IsNullOrWhiteSpace(leaf))
                                normalizedPages.Add(leaf);
                    }
                }

                var pages = new List<PageSeg>();
                int nextIndex = 0;

                for (int i = 0; i < normalizedPages.Count; i++)
                {
                    string text = normalizedPages[i]?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // NEW: expand interactive sequences into prompt + NPC follow-ups (same order as the game)
                    if (TryExpandInteractive(text, out var expanded))
                    {
                        foreach (var body in expanded)
                            AddPageIfNotEmpty(pages, body, nextIndex++, null);
                        continue;
                    }

                    // Top-level random choice: "$c 0.5#A#B"
                    if (TrySplitRandomChoice(text, out var cA, out var cB))
                    {
                        AddPageIfNotEmpty(pages, cA, nextIndex++, null);
                        AddPageIfNotEmpty(pages, cB, nextIndex++, null);
                        continue;
                    }

                    // Conditional choice: "$d FLAG#A|B" or $query/$p
                    if (TrySplitConditionalChoice(text, out var dA, out var dB))
                    {
                        AddPageIfNotEmpty(pages, dA, nextIndex++, null);
                        AddPageIfNotEmpty(pages, dB, nextIndex++, null);
                        continue;
                    }

                    // Caret gender split: "Ugh...^Why?"
                    if (TryTopLevelCaretGender(text, out var cgMale, out var cgFemale))
                    {
                        AddPageIfNotEmpty(pages, cgMale, nextIndex++, "male");
                        AddPageIfNotEmpty(pages, cgFemale, nextIndex++, "female");
                        continue;
                    }

                    // ${m^f(^n)} gender variant
                    if (TryTopLevelGender(text, out var male, out var female, out var nb))
                    {
                        if (!string.IsNullOrEmpty(male)) AddPageIfNotEmpty(pages, male, nextIndex++, "male");
                        if (!string.IsNullOrEmpty(female)) AddPageIfNotEmpty(pages, female, nextIndex++, "female");
                        if (!string.IsNullOrEmpty(nb)) AddPageIfNotEmpty(pages, nb, nextIndex++, "nonbinary");
                    }
                    else
                    {
                        AddPageIfNotEmpty(pages, text, nextIndex++, null);
                    }
                }

                return pages;
            }

            // Expand $y 'prompt_opt1_resp1_opt2_resp2' into: [prompt, resp1, resp2, ...]
            // Expand $q .. # <prompt> # $r .. # <resp1> # $r .. # <resp2> ... into same shape.

            // Expand $y 'prompt_opt1_resp1_opt2_resp2' into: [prompt, resp1, resp2, ...]
            // Expand $q .. # <prompt> # $r .. # <resp1> # $r .. # <resp2> ... into same shape.
            private static bool TryExpandInteractive(string text, out List<string> outputs)
            {
                outputs = null;
                if (string.IsNullOrWhiteSpace(text)) return false;

                // ---- Robust $y parsing (allow inner apostrophes like you'll) ----
                // Find $y, then take the first quote after it as the wrapper,
                // and capture up to the LAST occurrence of that same quote.
                int yIdx = text.IndexOf("$y", StringComparison.OrdinalIgnoreCase);
                if (yIdx >= 0)
                {
                    int qStart = -1;
                    for (int i = yIdx + 2; i < text.Length; i++)
                    {
                        char ch = text[i];
                        if (ch == '\'' || ch == '"') { qStart = i; break; }
                        if (!char.IsWhiteSpace(ch)) break; // something else after $y → not a quoted payload
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

                s = RxCmdChancePrefix.Replace(s, "");
                s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

                // IMPORTANT: don't render $y here; TryExpandInteractive removed it already
                s = RxCmdOther.Replace(s, "");
                s = RxTrailingDollar.Replace(s, "");

                s = RxSquareBracketGrant.Replace(s, "");

                // --- Gameplay-only tokens that should never appear on-screen ---
                s = Regex.Replace(s, @"%fork\d*\b", "", RegexOptions.IgnoreCase); // strip %fork*
                s = Regex.Replace(s, @"%noturn\b", "", RegexOptions.IgnoreCase);  // strip %noturn
                s = Regex.Replace(s, @"\s*\$k\b", "", RegexOptions.IgnoreCase);   // strip bare $k

                // NEW: strip a dangling "$d <flag>" prefix that some packs accidentally kept
                //      (covers "$d bus", optional leading '#', optional trailing '#', and trims any trailing space)
                s = Regex.Replace(s, @"^\s*#?\$d(?:\s+[^\s#]+)?#?\s*", "", RegexOptions.IgnoreCase);

                // Remove variable %tokens (name/farm/etc) and '@'
                s = RxRemovePercentTokens.Replace(s, "");
                s = RxAtToken.Replace(s, "");

                // Drop portrait codes and tidy whitespace
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

                s = RxCmdChancePrefix.Replace(s, "");
                s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

                // Drop any residual $y payload from this line entirely.
                s = Regex.Replace(s, @"\s*\$y\b.*$", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                // Explicitly remove %fork control code (it's not rendered to the player).
                s = Regex.Replace(s, @"%fork\b", "", RegexOptions.IgnoreCase);

                // ✅ NEW: also strip a dangling "$d <flag>" prefix here (covers "$d bus", with/without '#' and trims space)
                s = Regex.Replace(s, @"^\s*#?\$d(?:\s+[^\s#]+)?#?\s*", "", RegexOptions.IgnoreCase);

                s = RxCmdOther.Replace(s, "");
                s = RxTrailingDollar.Replace(s, "");

                // NOTE: We intentionally KEEP portrait tokens here (handled elsewhere),
                // so don't strip PortraitRegex in this method.

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
                var m = Regex.Match(text ?? "", @"^\s*#?\$c\s*[0-9.]+\s*#(.+?)#(.+)$",
                                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (m.Success) { a = m.Groups[1].Value; b = m.Groups[2].Value; return true; }
                a = b = null; return false;
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
        }

    }
}
