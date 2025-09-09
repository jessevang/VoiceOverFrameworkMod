using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        /// <summary>
        /// Build entries from Data/Festivals/* using DialogueUtil for consistent sanitation/splitting.
        /// Emits VoiceEntryTemplate rows for both keyed lines and embedded event-ish speak/bubble captures
        /// produced by GetFestivalDialogueForCharacter.
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildFromFestivals(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            var fest = this.GetFestivalDialogueForCharacter(characterName, languageCode, content);
            if (fest == null || fest.Count == 0)
                return outList;

            foreach (var kv in fest)
            {
                // kv.Key is an internal handle; kv.Value holds the payload
                var item = kv.Value;
                string processingKey = item.SourceInfo ?? $"Festival/{kv.Key}/{characterName}";
                string raw = item.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Split & sanitize using shared rules (portraits kept in Actor; removed in Display)
                var segs = DialogueUtil.SplitAndSanitize(raw);
                if (segs == null || segs.Count == 0)
                    continue;

                // Prefer explicit TK if provided, else use the one we computed in GetFestivalDialogueForCharacter
                string tkBase = !string.IsNullOrWhiteSpace(item.TranslationKey) ? item.TranslationKey : processingKey;

                foreach (var seg in segs) // seg: Actor, Display, PageIndex, Gender
                {
                    string genderTail = string.IsNullOrEmpty(seg.Gender) ? "" : "_" + seg.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = seg.Actor,     // includes {Portrait:*}
                        AudioPath = path,
                        TranslationKey = tkBase,
                        PageIndex = seg.PageIndex,
                        DisplayPattern = seg.Display,   // portraits/tokens removed for display matching
                        GenderVariant = seg.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[FEST] + {processingKey} (tk={tkBase}) p{seg.PageIndex} g={seg.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }

        /// <summary>
        /// Load festival dialogue lines for a specific character (language-aware).
        /// Emits real TranslationKeys for ANY sheet key that starts with the character name, e.g.:
        ///   Data/Festivals/{festId}:{Character}*
        /// Also scans embedded script lines like:
        ///   speak {Character} "..."
        ///   textAboveHead/showTextAboveHead {Character} "..."
        /// and assigns a synthetic TranslationKey:
        ///   Festivals/{festId}:{Character}:s{index}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetFestivalDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // 1) Discover festival IDs from FestivalDates (language first, then fallback to English)
            var festivalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> dates = null;
            try
            {
                dates = gameContent.Load<Dictionary<string, string>>($"Data/Festivals/FestivalDates{langSuffix}");
            }
            catch (ContentLoadException) { /* ignore */ }
            catch (Exception ex) { this.Monitor.Log($"Error loading FestivalDates{langSuffix}: {ex.Message}", LogLevel.Trace); }

            if (dates == null)
            {
                try
                {
                    dates = gameContent.Load<Dictionary<string, string>>("Data/Festivals/FestivalDates");
                }
                catch (ContentLoadException) { /* ignore */ }
                catch (Exception ex) { this.Monitor.Log($"Error loading FestivalDates (fallback): {ex.Message}", LogLevel.Trace); }
            }

            if (dates != null)
            {
                foreach (var k in dates.Keys)
                    festivalIds.Add(k);
            }

            // Vanilla fallback if nothing was discovered (covers base game festivals)
            if (festivalIds.Count == 0)
            {
                foreach (var k in new[] { "spring13", "spring24", "summer11", "summer28", "fall16", "fall27", "winter8", "winter25" })
                    festivalIds.Add(k);
            }

            // 2) For each festival sheet, harvest dialogue
            // NOTE: allow escaped quotes within the string payloads: \" inside the quotes.
            // The capture group grabs the inner content (without quotes).
            var speakRegex = new Regex(
                $@"\bspeak\s+{Regex.Escape(characterName)}\s+""((?:[^""\\]|\\.)*)""",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            var textAboveHeadRegex = new Regex(
                $@"\b(?:textAboveHead|showTextAboveHead)\s+{Regex.Escape(characterName)}\s+""((?:[^""\\]|\\.)*)""",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            foreach (var festId in festivalIds.OrderBy(s => s))
            {
                // Try language-specific sheet, then English fallback
                Dictionary<string, string> dict = null;
                string assetLang = $"Data/Festivals/{festId}{langSuffix}";
                string assetEn = $"Data/Festivals/{festId}";

                try
                {
                    dict = gameContent.Load<Dictionary<string, string>>(assetLang);
                }
                catch (ContentLoadException)
                {
                    try { dict = gameContent.Load<Dictionary<string, string>>(assetEn); }
                    catch (ContentLoadException) { /* ignore */ }
                    catch (Exception ex2) { this.Monitor.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace); }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
                    continue;
                }

                if (dict == null || dict.Count == 0)
                    continue;

                // 2a) Real keyed entries: include ANY key that begins with the character's name (no hard-coded suffix list)
                foreach (var kvp in dict)
                {
                    if (!kvp.Key.StartsWith(characterName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var raw = kvp.Value;
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string innerId = $"{festId}:{kvp.Key}";
                    string source = $"Festival/{festId}/{kvp.Key}";
                    string tk = $"Data/Festivals/{festId}:{kvp.Key}";

                    results[innerId] = (raw, source, tk); // keep raw; sanitation happens later
                }

                // 2b) Embedded lines in event scripts (any field). Capture:
                //     - speak <Character> "..."
                //     - textAboveHead/showTextAboveHead <Character> "..."
                int speakIndex = 0;
                foreach (var kvp in dict)
                {
                    var value = kvp.Value ?? "";
                    if (value.Length == 0) continue;

                    void addCaptured(string kind, string captured)
                    {
                        if (string.IsNullOrWhiteSpace(captured)) return;
                        // Unescape \" -> "   and   \\ -> \
                        string rawCaptured = Regex.Unescape(captured);

                        string innerId = $"{festId}:{characterName}:{kind}:{speakIndex}";
                        string source = $"Festival/{festId}/{characterName}:{kind}:{speakIndex}";
                        string tk = $"Festivals/{festId}:{characterName}:s{speakIndex}"; // synthetic, stable

                        results[innerId] = (rawCaptured, source, tk);
                        speakIndex++;
                    }

                    foreach (Match m in speakRegex.Matches(value))
                        addCaptured("speak", m.Groups[1].Value);

                    foreach (Match m in textAboveHeadRegex.Matches(value))
                        addCaptured("textAboveHead", m.Groups[1].Value);
                }

                if (this.Config?.developerModeOn == true)
                {
                    int realCount = results.Keys.Count(k => k.StartsWith($"{festId}:{characterName}", StringComparison.OrdinalIgnoreCase));
                    int speakCount = dict.Values.Sum(v => speakRegex.Matches(v).Count);
                    int tahCount = dict.Values.Sum(v => textAboveHeadRegex.Matches(v).Count);
                    this.Monitor.Log(
                        $"[Festival] {characterName}: '{festId}' -> {realCount} keyed + {speakCount} speak + {tahCount} textAboveHead/showTextAboveHead.",
                        LogLevel.Trace);
                }
            }

            return results;
        }
    }
}
