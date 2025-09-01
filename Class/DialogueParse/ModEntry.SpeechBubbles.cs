// File: ModEntry.SpeechBubbles.cs
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        /// <summary>
        /// Build speech-bubble entries for a given NPC from Strings/SpeechBubbles[.lang].json.
        /// Keys are filtered by character name (case-insensitive) and mapped to V2 VoiceEntryTemplate rows.
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildSpeechBubbleEntries(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            if (string.IsNullOrWhiteSpace(characterName)) return outList;

            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string asset = $"Strings/SpeechBubbles{langSuffix}";

            Dictionary<string, string> sheet = null;
            try { sheet = content.Load<Dictionary<string, string>>(asset); }
            catch (ContentLoadException) { }

            if (sheet == null || sheet.Count == 0) return outList;

            foreach (var kv in sheet)
            {
                string key = kv.Key?.Trim();
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw)) continue;

                if (!KeyTargetsCharacter(key, characterName)) continue;

                var pages = DialogueUtil.SplitAndSanitize(raw, splitBAsPage: false);
                if (pages.Count == 0) continue;

                var p = pages[0];

                string actorText = (p.Actor ?? string.Empty)
                    .Replace("{0}", "{Farmer_Name}")
                    .Replace("{1}", "{Farm_Name}");

                string display = (p.Display ?? string.Empty)
                    .Replace("{0}", string.Empty)
                    .Replace("{1}", string.Empty)
                    .Trim();

                string file = $"{entryNumber}{(string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender)}.{ext}";
                string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                outList.Add(new VoiceEntryTemplate
                {
                    DialogueFrom = key,
                    DialogueText = actorText,
                    AudioPath = path,
                    TranslationKey = $"Strings/SpeechBubbles:{key}",
                    PageIndex = 0,
                    DisplayPattern = display,
                    GenderVariant = p.Gender
                });

                entryNumber++;
            }

            return outList;
        }

        /// <summary>
        /// Returns true if the speech-bubble key targets the given character name.
        /// Matches tokens case-insensitively on underscores (e.g., "SeedShop_Pierre_Greeting1").
        /// </summary>
        private static bool KeyTargetsCharacter(string key, string characterName)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(characterName)) return false;
            string k = key;
            string n = characterName;
            return k.IndexOf("_" + n + "_", StringComparison.OrdinalIgnoreCase) >= 0
                || k.EndsWith("_" + n, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(n + "_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
