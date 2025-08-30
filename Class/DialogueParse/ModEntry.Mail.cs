/*
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
        private IEnumerable<VoiceEntryTemplate> BuildFromCharacterDialogue(
            string characterName, string languageCode, IGameContentHelper content, ref int entryNumber, string ext)
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

                var pages = DialogueSplitAndSanitize(raw);
                foreach (var page in pages)
                {
                    string file = $"{entryNumber}{(string.IsNullOrEmpty(page.Gender) ? "" : "_" + page.Gender)}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = key,
                        DialogueText = page.Actor, // actor-facing (with {Portrait:*})
                        AudioPath = path,
                        TranslationKey = $"Characters/Dialogue/{characterName}:{key}",
                        PageIndex = page.PageIndex,
                        DisplayPattern = page.Display, // clean player-visible text
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
            public string Actor;   // with {Portrait:*} tokens for VO direction
            public string Display; // clean, what players see (for matching/QA)
            public int PageIndex;
            public string Gender;
        }

        private List<PageSeg> DialogueSplitAndSanitize(string raw)
        {
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

                // 1) Top-level random choice: "$c 0.5#A#B"  → A and B (same page index)
                if (TrySplitRandomChoice(text, out var cA, out var cB))
                {
                    AddPageIfNotEmpty(pages, cA, i, null);
                    AddPageIfNotEmpty(pages, cB, i, null);
                    continue;
                }

                // 2) Top-level conditional/query: "$d FLAG#A|B" or "$query ... #A|B" → A and B
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

            string actor = SanitizeForActor(body);
            string display = SanitizeForDisplay(body);
            if (string.IsNullOrWhiteSpace(actor) && string.IsNullOrWhiteSpace(display)) return;

            pages.Add(new PageSeg
            {
                Actor = actor,
                Display = display,
                PageIndex = pageIndex,
                Gender = gender
            });
        }

        // ---- Sanitizers ----------------------------------------------------------

        // keep portrait markers for actor; strip control/gating tokens for both
        private string StripControlTokensKeepPortraits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // remove leading gate tokens like "cc#", "joja#", or "27#"
            s = Regex.Replace(s, @"^\s*(?:[A-Za-z_]+|\d+)#", "");

            // remove inline "#$1 FlagName#" tokens anywhere (e.g., "#$1 AbigailHAND#")
            s = Regex.Replace(s, @"#\$1\s+[A-Za-z0-9_]+#", "");

            // remove stray numeric weights that might leak (defensive)
            s = Regex.Replace(s, @"^\s*0?\.?\d+\s*", "");

            // fix trailing leftover '$' from gender macros like "... ${guy^lady}$," (rare)
            s = Regex.Replace(s, @"\$(?=[\s,\.!\?\)]|$)", "");

            // @ => {PLAYER}
            s = s.Replace("@", "{PLAYER}");

            // collapse spaces
            s = Regex.Replace(s, @"[ \t]+", " ").Trim();
            return s;
        }

        private static readonly Regex PortraitRegex =
            new Regex(@"\$(?:h|s|u|l|a|\d+)\b", RegexOptions.Compiled);

        // Actor-facing: convert $-portrait markers into {Portrait:*}
        private string SanitizeForActor(string s)
        {
            s = StripControlTokensKeepPortraits(s);
            s = PortraitRegex.Replace(s, m =>
            {
                string code = m.Value.Substring(1); // remove '$'
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

        // Player-visible: strip portrait markers entirely
        private string SanitizeForDisplay(string s)
        {
            s = StripControlTokensKeepPortraits(s);
            s = PortraitRegex.Replace(s, ""); // remove markers
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        // ---- Existing split helpers (unchanged) ---------------------------------

        private bool TryTopLevelGender(string text, out string male, out string female, out string nb)
        {
            male = female = nb = null;
            var m = Regex.Match(text ?? "", @"\$\{([^{}]+)\}");
            if (!m.Success) return false;

            var parts = m.Groups[1].Value.Split('^');
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
            var m = Regex.Match(text ?? "", @"^\s*\$(?:d|query)\b[^#]*#(.+?)\|(.+)$",
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
    }
}


*/