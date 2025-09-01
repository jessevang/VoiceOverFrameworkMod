using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromStringsCharacters(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            // Assumes this helper exists and returns keys → raw text
            // Keys are usually fully-qualified like "Strings/Characters:Abigail_Something"
            var map = this.GetVanillaCharacterStringKeys(characterName, languageCode, content);
            if (map == null || map.Count == 0)
                return outList;

            foreach (var kv in map)
            {
                string key = kv.Key?.Trim();
                string raw = kv.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                    continue;

                // Use shared splitter/sanitizer for consistent behavior
                var pages = DialogueUtil.SplitAndSanitize(raw);
                if (pages == null || pages.Count == 0)
                    continue;

                string tk = EnsureStringsCharactersTranslationKey(key);

                foreach (var seg in pages)
                {
                    string genderTail = string.IsNullOrEmpty(seg.Gender) ? "" : "_" + seg.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = key,        // original key from the sheet
                        DialogueText = seg.Actor,  // actor-facing (keeps {Portrait:*})
                        AudioPath = path,
                        TranslationKey = tk,         // normalized/stable key
                        PageIndex = seg.PageIndex,
                        DisplayPattern = seg.Display,
                        GenderVariant = seg.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[STR-CHAR] + {key} (tk={tk}) p{seg.PageIndex} g={seg.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }

        private static string EnsureStringsCharactersTranslationKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return key;
            // Most helpers already return fully-qualified keys; this is a safe fallback.
            return key.Contains(":") ? key : $"Strings/Characters:{key}";
        }
    }
}
