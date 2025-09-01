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
                @"%(?:adj|noun|place|name|spouse|farm|favorite|kid1|kid2|pet|firstnameletter|band|book|season|time)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // remove farmer name token '@'
            private static readonly Regex RxAtToken = new(@"@", RegexOptions.Compiled);

            // item grants like [O] or registry IDs in brackets
            private static readonly Regex RxSquareBracketGrant = new(@"\[[^\]]+\]", RegexOptions.Compiled);

            // ── Public API ─────────────────────────────────────────────
            public static List<PageSeg> SplitAndSanitize(string raw, bool splitBAsPage = false)
            {
                if (raw == null) raw = string.Empty;

                // 0) Weekly rotation: take the first variant before "||"
                int weeklyIdx = raw.IndexOf("||", StringComparison.Ordinal);
                if (weeklyIdx >= 0)
                    raw = raw[..weeklyIdx];

                // Base split by "#$e#"
                var firstLevel = raw.Split(new[] { "#$e#" }, StringSplitOptions.None);

                // If splitBAsPage=true (events), also split "#$b#" into separate pages
                var normalizedPages = new List<string>();
                foreach (var chunk in firstLevel)
                {
                    string c = chunk ?? "";
                    if (splitBAsPage)
                    {
                        var bs = c.Split(new[] { "#$b#" }, StringSplitOptions.None);
                        foreach (var b in bs)
                            normalizedPages.Add(b ?? "");
                    }
                    else
                    {
                        normalizedPages.Add(c.Replace("#$b#", "\n"));
                    }
                }

                var pages = new List<PageSeg>();
                int nextIndex = 0;

                for (int i = 0; i < normalizedPages.Count; i++)
                {
                    string text = normalizedPages[i]?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // 1) Top-level random choice: "$c 0.5#A#B"
                    if (TrySplitRandomChoice(text, out var cA, out var cB))
                    {
                        AddPageIfNotEmpty(pages, cA, nextIndex++, null);
                        AddPageIfNotEmpty(pages, cB, nextIndex++, null);
                        continue;
                    }

                    // 2) Conditional choice: "$d FLAG#A|B" or $query/$p
                    if (TrySplitConditionalChoice(text, out var dA, out var dB))
                    {
                        AddPageIfNotEmpty(pages, dA, nextIndex++, null);
                        AddPageIfNotEmpty(pages, dB, nextIndex++, null);
                        continue;
                    }

                    // 3) Caret gender split: "Ugh...^Why?"
                    if (TryTopLevelCaretGender(text, out var cgMale, out var cgFemale))
                    {
                        AddPageIfNotEmpty(pages, cgMale, nextIndex++, "male");
                        AddPageIfNotEmpty(pages, cgFemale, nextIndex++, "female");
                        continue;
                    }

                    // 4) ${m^f(^n)} gender variant
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

            // ── Helpers ─────────────────────────────────────────────
            private static void AddPageIfNotEmpty(List<PageSeg> pages, string body, int pageIndex, string gender)
            {
                if (string.IsNullOrWhiteSpace(body)) return;

                // Actor text: readable with portrait tags expanded
                string actor = SanitizeForActor(body);

                // DisplayPattern: STRIPPED of random/player values & control codes
                string display = SanitizeForDisplayPattern(body);

                pages.Add(new PageSeg
                {
                    Actor = actor,
                    Display = display,
                    PageIndex = pageIndex,
                    Gender = gender
                });
            }

            /// <summary>
            /// Build a DisplayPattern suitable for matching against the in-game sanitized text.
            /// Removes variable/random tokens and names so it’s stable across saves/replays.
            /// </summary>
            private static string SanitizeForDisplayPattern(string s)
            {
                if (string.IsNullOrWhiteSpace(s))
                    return string.Empty;

                // $1 once-only line → keep the first branch
                if (RxOnceOnlyLine.IsMatch(s))
                    s = RxOnceOnlyLine.Replace(s, m => m.Groups["first"].Value);

                // strip some leading gates/strays/narration
                s = RxLeadingGate.Replace(s, "");
                s = RxInlineFlag1.Replace(s, "");
                s = RxStrayNumber.Replace(s, "");
                s = RxLeadingNarr.Replace(s, "");

                // Keep only the $q prompt, drop $r answers
                s = RxCmdQuestion.Replace(s, m => " " + (m.Groups["q"].Value ?? ""));
                s = RxCmdResponse.Replace(s, "");

                // remove chance prefix BEFORE generic '#' cleanup
                s = RxCmdChancePrefix.Replace(s, "");

                // generic hash cleanup (not followed by $)
                s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

                // Expand $y into readable choices (optional, harmless if absent)
                s = RxCmdQuick.Replace(s, m =>
                {
                    string payload = m.Groups["p"].Value ?? "";
                    var bits = payload.Split('_');
                    var pairs = new List<string>();
                    for (int k = 0; k + 1 < bits.Length; k += 2)
                        pairs.Add($"{bits[k]}: {bits[k + 1]}");
                    return pairs.Count > 0 ? string.Join(" | ", pairs) : "";
                });

                // drop other $action/$t/$v etc
                s = RxCmdOther.Replace(s, "");
                s = RxTrailingDollar.Replace(s, "");

                // remove item grants in [ ... ]
                s = RxSquareBracketGrant.Replace(s, "");

                // *** CRITICAL FOR DISPLAYPATTERN ***
                // Remove player/random tokens entirely (we’ll also remove them from the live text)
                s = RxRemovePercentTokens.Replace(s, "");
                s = RxAtToken.Replace(s, ""); // remove '@' farmer token

                // drop portrait/emote codes
                s = PortraitRegex.Replace(s, "");

                // collapse whitespace
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

                s = RxCmdQuick.Replace(s, m =>
                {
                    string payload = m.Groups["p"].Value ?? "";
                    var bits = payload.Split('_');
                    var pairs = new List<string>();
                    for (int k = 0; k + 1 < bits.Length; k += 2)
                        pairs.Add($"{bits[k]}: {bits[k + 1]}");
                    return pairs.Count > 0 ? string.Join(" | ", pairs) : "";
                });

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

            // split helpers
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
