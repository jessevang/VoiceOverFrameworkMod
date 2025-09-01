using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // ──────────────────────────────────────────────────────────────────────────
        // Movie Reactions → manifest entries
        // ──────────────────────────────────────────────────────────────────────────
        private IEnumerable<VoiceEntryTemplate> BuildFromMovieReactions(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var reactions = this.GetMovieReactionsForCharacter(characterName, languageCode, content);
            if (reactions == null || reactions.Count == 0)
                return outList;

            foreach (var kv in reactions)
            {
                var item = kv.Value;
                string processingKey = item.SourceInfo ?? $"MovieReactions/{characterName}";
                string raw = item.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Use the shared DialogueUtil to split & sanitize consistently
                var pages = DialogueUtil.SplitAndSanitize(raw);

                foreach (var p in pages)
                {
                    string genderTail = string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    // Prefer the computed TranslationKey; fall back to processingKey (shouldn't happen)
                    string tk = !string.IsNullOrWhiteSpace(item.TranslationKey)
                        ? item.TranslationKey
                        : processingKey;

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = p.Actor,     // actor-facing (keeps {Portrait:*})
                        AudioPath = path,
                        TranslationKey = tk,        // Strings/MovieReactions:{JsonKey}
                        PageIndex = p.PageIndex,
                        DisplayPattern = p.Display, // clean player-visible text
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
        /// Load "Strings/MovieReactions" for a specific character (language-aware).
        /// Emits stable TranslationKeys for JSON keys:
        ///   Strings/MovieReactions:{JsonKey}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        ///   - InnerId: unique within this loader.
        ///   - SourceInfo: DialogueFrom base (e.g., "MovieReactions/Abigail_*_BeforeMovie").
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetMovieReactionsForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // Try localized asset first, then English fallback
            Dictionary<string, string> dict = null;
            string assetLang = $"Strings/MovieReactions{langSuffix}";
            string assetEn = "Strings/MovieReactions";

            try
            {
                dict = gameContent.Load<Dictionary<string, string>>(assetLang);
            }
            catch (ContentLoadException)
            {
                try
                {
                    dict = gameContent.Load<Dictionary<string, string>>(assetEn);
                }
                catch (ContentLoadException) { /* ignore */ }
                catch (Exception ex2)
                {
                    Monitor?.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
            }

            if (dict == null || dict.Count == 0)
                return results;

            string prefix = characterName + "_"; // e.g., "Abigail_"
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
