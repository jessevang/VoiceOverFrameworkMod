using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        /// <summary>
        /// Load rainy.json (with language suffix) and return only the entries for the given character.
        /// Example call (inside GenerateSingleTemplate): 
        ///   entries.AddRange(BuildFromRainyDialogueForCharacter(character, language, Helper.GameContent, ref entryNo, ext));
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildFromRainyDialogueForCharacter(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            if (string.IsNullOrWhiteSpace(characterName))
                return outList;

            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string asset = $"Characters/Dialogue/rainy{langSuffix}";

            Dictionary<string, string> sheet = null;
            try { sheet = content.Load<Dictionary<string, string>>(asset); }
            catch (ContentLoadException) { /* file for that language may not exist */ }

            if (sheet == null || sheet.Count == 0)
                return outList;

            if (!sheet.TryGetValue(characterName, out var raw) || string.IsNullOrWhiteSpace(raw))
                return outList;

            // Use your shared splitter/sanitizer.
            var pages = DialogueUtil.SplitAndSanitize(raw);
            foreach (var page in pages)
            {
                string file = $"{entryNumber}{(string.IsNullOrEmpty(page.Gender) ? "" : "_" + page.Gender)}.{ext}";
                string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                outList.Add(new VoiceEntryTemplate
                {
                    DialogueFrom = "rainy",
                    DialogueText = page.Actor,
                    AudioPath = path,
                    TranslationKey = $"Characters/Dialogue/rainy:{characterName}",
                    PageIndex = page.PageIndex,
                    DisplayPattern = page.Display,
                    GenderVariant = page.Gender
                });

                entryNumber++;
            }

            return outList;
        }

        /// <summary>
        /// Bulk variant: load rainy.json and produce entries for ALL characters present in that file.
        /// Useful for one-shot template generation across every NPC in rainy.json.
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildFromRainyDialogue_AllCharacters(
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string asset = $"Characters/Dialogue/rainy{langSuffix}";

            Dictionary<string, string> sheet = null;
            try { sheet = content.Load<Dictionary<string, string>>(asset); }
            catch (ContentLoadException) { /* missing in this language */ }

            if (sheet == null || sheet.Count == 0)
                return outList;

            foreach (var kv in sheet)
            {
                string characterName = kv.Key?.Trim();
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(raw))
                    continue;

                var pages = DialogueUtil.SplitAndSanitize(raw);
                foreach (var page in pages)
                {
                    string file = $"{entryNumber}{(string.IsNullOrEmpty(page.Gender) ? "" : "_" + page.Gender)}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = "rainy",
                        DialogueText = page.Actor,
                        AudioPath = path,
                        TranslationKey = $"Characters/Dialogue/rainy:{characterName}",
                        PageIndex = page.PageIndex,
                        DisplayPattern = page.Display,
                        GenderVariant = page.Gender
                    });

                    entryNumber++;
                }
            }

            return outList;
        }
    }
}
