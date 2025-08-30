using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using Microsoft.Xna.Framework.Content;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // ── Command/Token Regexes ────────────────────────────────────────────────
        private static readonly Regex RxLeadingGate = new(@"^\s*(?:[A-Za-z_]+|\d+)#", RegexOptions.Compiled);
        private static readonly Regex RxInlineFlag1 = new(@"#\$1\s+[A-Za-z0-9_]+#", RegexOptions.Compiled);
        private static readonly Regex RxStrayNumber = new(@"^\s*0?\.?\d+\s*", RegexOptions.Compiled);
        private static readonly Regex RxTrailingDollar = new(@"\$(?=[\s,\.!\?\)]|$)", RegexOptions.Compiled);
        private static readonly Regex RxCollapseWS = new(@"[ \t]+", RegexOptions.Compiled);
        private static readonly Regex RxVarToken = new(@"%[A-Za-z]+", RegexOptions.Compiled);
        private static readonly Regex RxAtToken = new(@"@", RegexOptions.Compiled);
        private static readonly Regex RxLeadingNarr = new(@"^\s*%+", RegexOptions.Compiled);
        private static readonly Regex RxWeeklyDelim = new(@"\|\|", RegexOptions.Compiled);
        private static readonly Regex RxCmdQuestion = new(@"#?\$q\s+[^#]*#(?<q>[^#]*)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RxCmdResponse = new(@"#?\$r\s+[^#]*#[^#]*#?", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RxCmdQuick = new(@"\$y\s+(['""])(?<p>.*?)\1", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RxCmdOther = new(@"#\$(?:action|k|t|v)\b[^#]*", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RxOnceOnlyLine = new(@"^\s*\$1\s+\S+#(?<first>.*?)(?:#\$e#.*)?$", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex PortraitRegex = new(@"\$(?:h|s|u|l|a|\d+)\b", RegexOptions.Compiled);

        private IEnumerable<VoiceEntryTemplate> BuildFromCharacterDialogue(string characterName, string languageCode, IGameContentHelper content, ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string asset = $"Characters/Dialogue/{characterName}{langSuffix}";

            Dictionary<string, string> sheet = null;
            try { sheet = content.Load<Dictionary<string, string>>(asset); }
            catch (ContentLoadException) { }

            if (sheet == null || sheet.Count == 0) return outList;

            foreach (var kv in sheet)
            {
                string key = kv.Key?.Trim();
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw)) continue;

                // Use our local splitter/sanitizer
                var pages = DialogueSplitAndSanitize(raw);
                foreach (var page in pages)
                {
                    string file = $"{entryNumber}{(string.IsNullOrEmpty(page.Gender) ? "" : "_" + page.Gender)}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = key,
                        DialogueText = page.Actor,
                        AudioPath = path,
                        TranslationKey = $"Characters/Dialogue/{characterName}:{key}",
                        PageIndex = page.PageIndex,
                        DisplayPattern = page.Display,
                        GenderVariant = page.Gender
                    });

                    entryNumber++;
                }
            }

            return outList;
        }

        // ── CharacterDialogue-only helpers ─────────────────────────────────────────
        private sealed class PageSeg
        {
            public string Actor;
            public string Display;
            public int PageIndex;
            public string Gender;
        }

        private List<PageSeg> DialogueSplitAndSanitize(string raw)
        {
            if (raw == null) raw = string.Empty;

            // 0) Weekly rotation: take the first variant before "||"
            int weeklyIdx = raw.IndexOf("||", StringComparison.Ordinal);
            if (weeklyIdx >= 0)
                raw = raw[..weeklyIdx];

            // split pages by "#$e#", and convert "#$b#" to newlines
            var pageChunks = raw.Split(new[] { "#$e#" }, StringSplitOptions.None);
            var normalizedPages = new List<string>(pageChunks.Length);
            foreach (var chunk in pageChunks)
                normalizedPages.Add((chunk ?? "").Replace("#$b#", "\n"));

            var pages = new List<PageSeg>();
            for (int i = 0; i < normalizedPages.Count; i++)
            {
                string text = normalizedPages[i]?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                // 1) Top-level random choice: "$c 0.5#A#B"  → emit A and B (same page index)
                if (TrySplitRandomChoice(text, out var cA, out var cB))
                {
                    AddPageIfNotEmpty(pages, cA, i, null);
                    AddPageIfNotEmpty(pages, cB, i, null);
                    continue;
                }

                // 2) Top-level conditional/query: "$d FLAG#A|B" or "$query ... #A|B" or "$p ..." → emit A and B
                if (TrySplitConditionalChoice(text, out var dA, out var dB))
                {
                    AddPageIfNotEmpty(pages, dA, i, null);
                    AddPageIfNotEmpty(pages, dB, i, null);
                    continue;
                }

                // 3) Caret gender split when NOT using ${...} (e.g., "Ugh...^Why?")
                if (TryTopLevelCaretGender(text, out var cgMale, out var cgFemale))
                {
                    AddPageIfNotEmpty(pages, cgMale, i, "male");
                    AddPageIfNotEmpty(pages, cgFemale, i, "female");
                    continue;
                }

                // 4) Gender variants inside ${m^f(^n)?}
                if (TryTopLevelGender(text, out var male, out var female, out var nb))
                {
                    if (!string.IsNullOrEmpty(male)) AddPageIfNotEmpty(pages, male, i, "male");
                    if (!string.IsNullOrEmpty(female)) AddPageIfNotEmpty(pages, female, i, "female");
                    if (!string.IsNullOrEmpty(nb)) AddPageIfNotEmpty(pages, nb, i, "nonbinary");
                }
                else
                {
                    AddPageIfNotEmpty(pages, text, i, null);
                }
            }

            return pages;
        }

        private void AddPageIfNotEmpty(List<PageSeg> pages, string body, int pageIndex, string gender)
        {
            if (string.IsNullOrWhiteSpace(body)) return;

            // Build both actor & display variants from the same body
            string actor = SanitizeForActor(body);
            string display = SanitizeForDisplay(body);
            if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(actor)) return;

            pages.Add(new PageSeg
            {
                Actor = actor,
                Display = display,
                PageIndex = pageIndex,
                Gender = gender
            });
        }

        // ---- Sanitizers ----------------------------------------------------------
        private string StripControlTokensKeepPortraits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // handle once-only $1 lines at start: prefer first-time text
            if (RxOnceOnlyLine.IsMatch(s))
                s = RxOnceOnlyLine.Replace(s, m => m.Groups["first"].Value);

            // remove leading gate tokens like "cc#", "joja#", or "27#"
            s = RxLeadingGate.Replace(s, "");

            // remove inline "#$1 FlagName#" tokens anywhere (e.g., "#$1 AbigailHAND#")
            s = RxInlineFlag1.Replace(s, "");

            // remove stray numeric weights that might leak (defensive)
            s = RxStrayNumber.Replace(s, "");

            // strip leading generic text-box '%' marker(s)
            s = RxLeadingNarr.Replace(s, "");

            // CHANGED: turn "$q ...#<question>" into just the question text (answers stripped below)
            s = RxCmdQuestion.Replace(s, m => " " + m.Groups["q"].Value);

            // CHANGED: drop all "$r ..." answer blobs from $q
            s = RxCmdResponse.Replace(s, "");

            // Cleans any remaining ##
            s = Regex.Replace(s, @"\s*#(?!\$)\s*", " ");

            // simplify $y quick responses: "$y 'A_B_A2_B2'" -> "A: B | A2: B2"
            s = RxCmdQuick.Replace(s, m =>
            {
                string payload = m.Groups["p"].Value ?? "";
                var bits = payload.Split('_');
                var pairs = new List<string>();
                for (int k = 0; k + 1 < bits.Length; k += 2)
                    pairs.Add($"{bits[k]}: {bits[k + 1]}");
                return pairs.Count > 0 ? string.Join(" | ", pairs) : "";
            });

            // remove other command blobs that shouldn't render
            s = RxCmdOther.Replace(s, "");

            // fix trailing leftover '$' from things like "... ${guy^lady}$"
            s = RxTrailingDollar.Replace(s, "");

            // Replace @ with {Farmer's Name}
            s = RxAtToken.Replace(s, "{Farmer's Name}");

            // Replace %tokens with friendly {Descriptions}
            s = RxVarToken.Replace(s, m =>
            {
                var key = m.Value.Substring(1); // drop leading '%'
                return VarMap.TryGetValue(key, out var desc) ? "{" + desc + "}" : m.Value;
            });

            // Trim + collapse whitespace
            s = RxCollapseWS.Replace(s, " ").Trim();
            return s;
        }

        // Actor-facing: keep line, convert $-portrait markers into {Portrait:*}
        private string SanitizeForActor(string s)
        {
            s = StripControlTokensKeepPortraits(s);
            s = PortraitRegex.Replace(s, m =>
            {
                string code = m.Value.Substring(1); // skip '$'
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

            // collapse any doubled spaces created by replacements
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s;
        }

        // Player-visible: strip portrait markers entirely
        private string SanitizeForDisplay(string s)
        {
            s = StripControlTokensKeepPortraits(s);
            s = PortraitRegex.Replace(s, ""); // remove markers
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s;
        }

        // ---- Existing split helpers ---------------------------------------------

        private bool TryTopLevelGender(string text, out string male, out string female, out string nb)
        {
            male = female = nb = null;
            var m = Regex.Match(text ?? "", @"\$\{([^{}]+)\}");
            if (!m.Success) return false;

            // support ^ or ¦ separators as per rules
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

        private bool TryTopLevelCaretGender(string text, out string male, out string female)
        {
            male = female = null;
            if (string.IsNullOrEmpty(text) || text.Contains("${"))
                return false;

            int caret = text.IndexOf('^');
            if (caret <= 0 || caret >= text.Length - 1)
                return false;
            int firstBreakAfterCaret = text.IndexOf('\n', caret + 1);

            if (firstBreakAfterCaret >= 0)
            {
                string tail = text.Substring(firstBreakAfterCaret);
                string maleHead = text.Substring(0, caret);
                string femaleHead = text.Substring(caret + 1, firstBreakAfterCaret - (caret + 1));

                male = maleHead + tail;
                female = femaleHead + tail;
            }
            else
            {
                male = text.Substring(0, caret);
                female = text.Substring(caret + 1);
            }

            return true;
        }

        private bool TrySplitRandomChoice(string text, out string a, out string b)
        {
            var m = Regex.Match(text ?? "", @"^\s*\$c\s*[0-9.]+\s*#(.+?)#(.+)$", RegexOptions.Singleline);
            if (m.Success)
            {
                a = m.Groups[1].Value;
                b = m.Groups[2].Value;
                return true;
            }
            a = b = null;
            return false;
        }

        private bool TrySplitConditionalChoice(string text, out string a, out string b)
        {
            var m = Regex.Match(text ?? "",
                @"^\s*#?\$(?:d|query|p)\b[^#]*#(.+?)\|(.+)$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success)
            {
                a = m.Groups[1].Value;
                b = m.Groups[2].Value;
                return true;
            }
            a = b = null;
            return false;
        }

        private static readonly Dictionary<string, string> VarMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["adj"] = "Random Adjective",
            ["noun"] = "Random Noun",
            ["place"] = "Random Place Name",
            ["spouse"] = "Spouse's Name",
            ["name"] = "Randomly Generated Name",
            ["firstnameletter"] = "First Letter of Farmer's Name",
            ["time"] = "Current Time",
            ["band"] = "Sam and Sebastian's Band Name",
            ["book"] = "Elliott's Book Title",
            ["pet"] = "Pet's Name",
            ["farm"] = "Farm Name",
            ["favorite"] = "Favorite Thing",
            ["kid1"] = "First Child's Name",
            ["kid2"] = "Second Child's Name",

            ["season"] = "Season",
        };
    }
}
