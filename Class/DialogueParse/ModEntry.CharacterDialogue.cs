using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;
using Microsoft.Xna.Framework.Content;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromCharacterDialogue(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
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

                // Use shared splitter/sanitizer
                var pages = DialogueUtil.SplitAndSanitize(raw);
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
    }
}
