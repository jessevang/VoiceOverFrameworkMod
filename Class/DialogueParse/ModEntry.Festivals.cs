/*
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using System.Text.RegularExpressions;
using VoiceOverFrameworkMod;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromFestivals(
        string characterName, string languageCode, IGameContentHelper content,
        ref int entryNumber, string ext)
    {
        var outList = new List<VoiceEntryTemplate>();
        var fest = this.GetFestivalDialogueForCharacter(characterName, languageCode, content);
        if (fest == null || fest.Count == 0) return outList;

        foreach (var kv in fest)
        {
            // kv.Key is usually the festival id (e.g., "fall16")
            var item = kv.Value;
            string processingKey = item.SourceInfo ?? $"Festival/{kv.Key}/{characterName}";
            string raw = item.RawText ?? "";
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // split & sanitize with the same rules as Dialogue (supports #$e#, #$b#, ${m^f(^n)})
            var pages = DialogueSplitAndSanitize(raw);

            foreach (var p in pages)
            {
                string genderTail = string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender;
                string file = $"{entryNumber}{genderTail}.{ext}";
                string path = System.IO.Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                string tk = item.TranslationKey;
                if (string.IsNullOrWhiteSpace(tk))
                {
                    // fallback: Data/Festivals/{festivalKey}:{character or source suffix}
                    // if SourceInfo already encodes the final suffix (e.g., _spouse), prefer that.
                    tk = $"Data/Festivals/{kv.Key}:{characterName}";
                }

                outList.Add(new VoiceEntryTemplate
                {
                    DialogueFrom = processingKey,
                    DialogueText = p.Text,
                    AudioPath = path,
                    TranslationKey = tk,
                    PageIndex = p.PageIndex,
                    DisplayPattern = p.Text,
                    GenderVariant = p.Gender
                });

                if (this.Config?.developerModeOn == true)
                    this.Monitor?.Log($"[FEST] + {processingKey} (tk={tk}) p{p.PageIndex} g={p.Gender ?? "na"} -> {path}", LogLevel.Trace);

                entryNumber++;
            }
        }

        return outList;
    }


        /// <summary>
        /// Load festival dialogue lines for a specific character (language-aware) V2.
        /// Emits real TranslationKeys for sheet keys like:
        ///   Data/Festivals/{festId}:{Character}[_spouse][_y2][_spouse_y2]
        /// Also scans embedded script lines like:
        ///   speak {Character} "..."
        /// and assigns a synthetic TranslationKey:
        ///   Festivals/{festId}:{Character}:s{index}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        ///   - InnerId is just a unique handle inside this method (not used elsewhere).
        ///   - SourceInfo is a readable DialogueFrom base, e.g. "Festival/fall16/Abigail_spouse"
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetFestivalDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // 1) Discover festival IDs from FestivalDates (try target language, then English fallback)
            var festivalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> dates = null;
            try
            {
                dates = gameContent.Load<Dictionary<string, string>>($"Data/Festivals/FestivalDates{langSuffix}");
            }
            catch (ContentLoadException) {  }
            catch (Exception ex) { Monitor.Log($"Error loading FestivalDates{langSuffix}: {ex.Message}", LogLevel.Trace); }

            if (dates == null)
            {
                try
                {
                    dates = gameContent.Load<Dictionary<string, string>>("Data/Festivals/FestivalDates");
                }
                catch (ContentLoadException) {  }
                catch (Exception ex) { Monitor.Log($"Error loading FestivalDates (fallback): {ex.Message}", LogLevel.Trace); }
            }

            if (dates != null)
            {
                foreach (var k in dates.Keys)
                    festivalIds.Add(k);
            }

            // Vanilla fallback if nothing was discovered
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
                    catch (ContentLoadException) { }
                    catch (Exception ex2) { Monitor.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace); }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
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
                    string sourceInfo = $"Festival/{festId}/{fullKey}";
                    string tk = $"Data/Festivals/{festId}:{fullKey}";

                    // keep RAW text; V2 sanitizer runs later
                    results[innerId] = (raw, sourceInfo, tk);
                }

                // 2b) Embedded 'speak Character "..."' lines inside any festival field (e.g., set-up scripts)
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

                        // Unique inner id per match
                        string innerId = $"{festId}:{characterName}:speak:{speakIndex}";
                        string sourceInfo = $"Festival/{festId}/{characterName}:speak:{speakIndex}";
                        // Synthetic translation key (not a real content key, but stable & searchable)
                        string tk = $"Festivals/{festId}:{characterName}:s{speakIndex}";

                        results[innerId] = (raw, sourceInfo, tk);
                        speakIndex++;
                    }
                }

                if (Config.developerModeOn)
                {
                    int realCount = suffixes.Count(s => dict.ContainsKey(characterName + s));
                    int speakCount = dict.Values.Sum(v => speakRegex.Matches(v).Count);
                    Monitor.Log($"[Festival] {characterName}: '{festId}' -> {realCount} keyed + {speakCount} speak lines.", LogLevel.Trace);
                }
            }

            return results;
        }



    }
}

*/