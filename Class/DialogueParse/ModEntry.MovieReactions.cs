/*
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using VoiceOverFrameworkMod;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromMovieReactions(
        string characterName, string languageCode, IGameContentHelper content,
        ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var reactions = this.GetMovieReactionsForCharacter(characterName, languageCode, content);
            if (reactions == null || reactions.Count == 0) return outList;

            foreach (var kv in reactions)
            {
                var item = kv.Value;
                string processingKey = item.SourceInfo ?? $"MovieReactions/{characterName}";
                string raw = item.RawText ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // generally one-liners; still normalize
                var pages = DialogueSplitAndSanitize(raw);

                foreach (var p in pages)
                {
                    string genderTail = string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = System.IO.Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    string tk = !string.IsNullOrWhiteSpace(item.TranslationKey) ? item.TranslationKey : processingKey;

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
                        this.Monitor?.Log($"[MOVIE] + {processingKey} (tk={tk}) p{p.PageIndex} g={p.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }


        /// <summary>
        /// Load "Strings/MovieReactions" for a specific character (language-aware) V2.
        /// Emits a stable TranslationKey per JSON key:
        ///   Strings/MovieReactions:{JsonKey}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        ///   - InnerId is just a unique handle inside this method (not used elsewhere)
        ///   - SourceInfo becomes your DialogueFrom base, e.g. "MovieReactions/Penny_*_BeforeMovie"
        /// Notes:
        ///   - We include all lines for the NPC (including "*", per-movie, and stage directions like "DuringMovie_2").
        ///   - Placeholders like {0} {2} are preserved as-is; your sanitizer will handle them later.
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetMovieReactionsForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // Try localized first, then English fallback
            Dictionary<string, string> dict = null;
            string assetLang = $"Strings/MovieReactions{langSuffix}";
            string assetEn = "Strings/MovieReactions";

            try
            {
                dict = gameContent.Load<Dictionary<string, string>>(assetLang);
            }
            catch (ContentLoadException)
            {
                try { dict = gameContent.Load<Dictionary<string, string>>(assetEn); }
                catch (ContentLoadException) {  }
                catch (Exception ex2) { Monitor?.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace); }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
                return results;
            }

            if (dict == null || dict.Count == 0)
                return results;

            string prefix = characterName + "_";
            foreach (var kvp in dict)
            {
                string jsonKey = kvp.Key ?? "";
                if (!jsonKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = kvp.Value ?? "";
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Stable identifiers
                string innerId = $"MovieReactions:{jsonKey}";
                string sourceInfo = $"MovieReactions/{jsonKey}";
                string translationKey = $"Strings/MovieReactions:{jsonKey}";

                results[innerId] = (raw, sourceInfo, translationKey);
            }

            if (Config?.developerModeOn == true)
                Monitor?.Log($"[MovieReactions] {characterName}: found {results.Count} entries.", LogLevel.Trace);

            return results;
        }


    }
}
*/