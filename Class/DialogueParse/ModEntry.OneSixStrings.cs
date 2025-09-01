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
        // ──────────────────────────────────────────────────────────────────────────
        // Strings/1_6_Strings → manifest entries
        // ──────────────────────────────────────────────────────────────────────────
        private IEnumerable<VoiceEntryTemplate> BuildFromOneSixStrings(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var oneSix = this.GetOneSixStringsDialogueForCharacter(characterName, languageCode, content);
            if (oneSix == null || oneSix.Count == 0)
                return outList;

            foreach (var item in oneSix)
            {
                // item.SourceInfo already includes the unique key, e.g. "Strings/1_6_Strings:Abigail_Something"
                string processingKey = item.SourceInfo ?? $"Strings/1_6_Strings:{characterName}";
                string raw = item.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Use shared splitter/sanitizer so we match Dialogue/Event behavior
                var pages = DialogueUtil.SplitAndSanitize(raw);

                foreach (var p in pages)
                {
                    string genderTail = string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    string tk = !string.IsNullOrWhiteSpace(item.TranslationKey)
                        ? item.TranslationKey
                        : processingKey; // should always be the same

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = p.Actor,     // actor-facing (keeps {Portrait:*})
                        AudioPath = path,
                        TranslationKey = tk,        // Strings/1_6_Strings:{Key}
                        PageIndex = p.PageIndex,
                        DisplayPattern = p.Display, // clean player-visible text
                        GenderVariant = p.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[1_6] + {processingKey} (tk={tk}) p{p.PageIndex} g={p.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }

        /// <summary>
        /// Load dialogue-like lines from Strings/1_6_Strings for a specific character (language-aware).
        /// We consider an entry to "belong" to the character if any underscore-delimited key segment
        /// equals one of the character's aliases (case-insensitive) after stripping trailing digits.
        /// Returns List of (RawText, SourceInfo, TranslationKey):
        ///   - SourceInfo: "Strings/1_6_Strings:{Key}"
        ///   - TranslationKey: "Strings/1_6_Strings:{Key}"
        /// </summary>
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetOneSixStringsDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new List<(string, string, string)>();
            try
            {
                bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
                string langSuffix = isEnglish ? "" : $".{languageCode}";

                Dictionary<string, string> dict = null;

                // Try language first, fallback to English
                try
                {
                    dict = gameContent.Load<Dictionary<string, string>>($"Strings/1_6_Strings{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    try { dict = gameContent.Load<Dictionary<string, string>>("Strings/1_6_Strings"); }
                    catch (ContentLoadException) { /* ignore */ }
                }
                catch
                {
                    // ignore non-critical load errors, we'll just return empty
                }

                if (dict == null || dict.Count == 0)
                    return results;

                // Build alias list (canonical + known nicknames)
                var aliases = GetCharacterAliases(characterName);

                // Match if any underscore-delimited key segment is equal to an alias (ignoring trailing digits)
                bool TargetsCharacter(string key)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        return false;

                    var parts = key.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawPart in parts)
                    {
                        var part = Regex.Replace(rawPart, @"\d+$", ""); // strip trailing digits: Emily2 -> Emily
                        if (aliases.Any(a => part.Equals(a, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                    return false;
                }

                foreach (var kvp in dict)
                {
                    string key = kvp.Key ?? "";
                    string raw = kvp.Value ?? "";
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    if (!TargetsCharacter(key))
                        continue;

                    string sourceInfo = $"Strings/1_6_Strings:{key}";
                    string tk = $"Strings/1_6_Strings:{key}";
                    results.Add((raw, sourceInfo, tk));
                }
            }
            catch (Exception ex)
            {
                this.Monitor?.Log($"Error in GetOneSixStringsDialogueForCharacter for {characterName}: {ex.Message}", LogLevel.Trace);
            }

            return results;
        }

        private static List<string> GetCharacterAliases(string characterName)
        {
            var aliases = new List<string> { characterName };

            // Minimal nickname mapping (extend as needed). Example: Abigail ⇒ Abby
            if (characterName.Equals("Abigail", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Abby");

            // Keep canonical names for others; extend with real nicknames if needed.
            // (This structure makes it easy to add more in the future.)
            return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
