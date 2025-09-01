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

                // Split & sanitize using the shared rules (portraits kept in Actor; removed in Display)
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
                        DialogueText = seg.Actor,     // includes {Portrait:...}
                        AudioPath = path,
                        TranslationKey = tkBase,
                        PageIndex = seg.PageIndex,
                        DisplayPattern = seg.Display,   // portraits removed
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
        /// Emits real TranslationKeys for sheet keys like:
        ///   Data/Festivals/{festId}:{Character}[_spouse][_y2][_spouse_y2]
        /// Also scans embedded script lines like:
        ///   speak {Character} "..."
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
            string[] suffixes = new[] { "", "_spouse", "_y2", "_spouse_y2" };
            var speakRegex = new Regex($@"\bspeak\s+{Regex.Escape(characterName)}\s+""([^""]+)""",
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

                // 2a) Real keyed entries (Abigail, Abigail_spouse, Abigail_y2, Abigail_spouse_y2)
                foreach (var suffix in suffixes)
                {
                    string fullKey = suffix.Length == 0 ? characterName : (characterName + suffix);
                    if (!dict.TryGetValue(fullKey, out var raw) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    string innerId = $"{festId}:{fullKey}";
                    string source = $"Festival/{festId}/{fullKey}";
                    string tk = $"Data/Festivals/{festId}:{fullKey}";

                    results[innerId] = (raw, source, tk); // keep raw; sanitation happens in BuildFromFestivals
                }

                // 2b) Embedded 'speak Character "..."' lines inside any festival field (e.g., setup scripts)
                int speakIndex = 0;
                foreach (var kvp in dict)
                {
                    var value = kvp.Value ?? "";
                    if (value.Length == 0) continue;

                    foreach (Match m in speakRegex.Matches(value))
                    {
                        string raw = m.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(raw))
                            continue;

                        string innerId = $"{festId}:{characterName}:speak:{speakIndex}";
                        string source = $"Festival/{festId}/{characterName}:speak:{speakIndex}";
                        // Synthetic key (stable & searchable; not a vanilla sheet key)
                        string tk = $"Festivals/{festId}:{characterName}:s{speakIndex}";

                        results[innerId] = (raw, source, tk);
                        speakIndex++;
                    }
                }

                if (this.Config?.developerModeOn == true)
                {
                    int realCount = suffixes.Count(s => dict.ContainsKey(characterName + s));
                    int speakCount = dict.Values.Sum(v => speakRegex.Matches(v).Count);
                    this.Monitor.Log($"[Festival] {characterName}: '{festId}' -> {realCount} keyed + {speakCount} speak lines.", LogLevel.Trace);
                }
            }

            return results;
        }
    }
}
