// File: ModEntry.SpeechBubbles.cs
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        /// <summary>
        /// Build speech-bubble entries for a given NPC from Strings/SpeechBubbles[.lang].json.
        /// Filters keys by character name and maps to V2 VoiceEntryTemplate rows.
        /// Keeps numbered placeholders {0},{1},… in DisplayPattern; converts
        /// {0}->{Farmer_Name}, {1}->{Farm_Name} in DialogueText only.
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildSpeechBubbleEntries(
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
            string asset = $"Strings/SpeechBubbles{langSuffix}";

            Dictionary<string, string> sheet = null;
            try
            {
                sheet = content.Load<Dictionary<string, string>>(asset);
            }
            catch (ContentLoadException)
            {
                //if (Config.developerModeOn)
                    //Monitor.Log($"[BUBBLES] No SpeechBubbles asset for '{languageCode}' at {asset}.", LogLevel.Trace);
            }

            if (sheet == null || sheet.Count == 0)
                return outList;

            foreach (var kv in sheet)
            {
                string key = kv.Key?.Trim();
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                    continue;

                if (!KeyTargetsCharacter(key, characterName))
                    continue;

                // Run through DialogueUtil so DisplayPattern uses the same sanitization rules
                var pages = DialogueUtil.SplitAndSanitize(raw, splitBAsPage: false);
                if (pages.Count == 0)
                    continue;

                var p = pages[0];

                // DialogueText for actor TTS:
                // - keep SDV emote/portrait expansions already handled by DialogueUtil
                // - convert @ -> {Farmer_Name} already done by DialogueUtil in Actor text
                // - additionally map {0}->{Farmer_Name}, {1}->{Farm_Name}
                string actorText = ReplaceNumberedPlaceholdersForActorText(p.Actor ?? string.Empty);

                // DisplayPattern used for runtime matching: leave {0},{1},… untouched
                string display = p.Display ?? string.Empty;

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

            if (Config.developerModeOn)
                Monitor.Log($"[BUBBLES] {characterName}: added {outList.Count} entries from {asset}.", LogLevel.Info);

            return outList;
        }

        /// <summary>
        /// True if a SpeechBubbles key targets the given character (underscore/name matching).
        /// Examples: "SeedShop_Pierre_Greeting1", "ScienceHouse_Robin_Raining2".
        /// </summary>
        private static bool KeyTargetsCharacter(string key, string characterName)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(characterName))
                return false;

            string k = key;
            string n = characterName;

            return k.IndexOf("_" + n + "_", StringComparison.OrdinalIgnoreCase) >= 0
                || k.EndsWith("_" + n, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(n + "_", StringComparison.OrdinalIgnoreCase)
                || k.Equals(n, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// For actor TTS only: convert numbered placeholders
        /// {0} → {Farmer_Name}, {1} → {Farm_Name}. Handles optional whitespace (e.g. "{1 }").
        /// </summary>
        private static string ReplaceNumberedPlaceholdersForActorText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // {0} -> {Farmer_Name}
            s = Regex.Replace(s, @"\{\s*0\s*\}", "{Farmer_Name}", RegexOptions.CultureInvariant);

            // {1} -> {Farm_Name}
            s = Regex.Replace(s, @"\{\s*1\s*\}", "{Farm_Name}", RegexOptions.CultureInvariant);

            return s;
        }
    }
}
