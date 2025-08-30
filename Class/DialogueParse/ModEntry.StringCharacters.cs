/*
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using VoiceOverFrameworkMod;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromStringsCharacters(string characterName, string languageCode, IGameContentHelper content, ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            // assumes you already have this helper:
            // Dictionary<string,string> GetVanillaCharacterStringKeys(string name, string lang, IGameContentHelper content)
            var map = this.GetVanillaCharacterStringKeys(characterName, languageCode, content);
            if (map == null || map.Count == 0) return outList;

            foreach (var kv in map)
            {
                string key = kv.Key?.Trim();
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                    continue;

                // Strings/Characters entries generally don’t use page breaks; still normalize
                string text = SanitizeInlineTokens(raw);
                string file = $"{entryNumber}.{ext}";
                string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                outList.Add(new VoiceEntryTemplate
                {
                    DialogueFrom = key,
                    DialogueText = text,
                    AudioPath = path,
                    TranslationKey = key, // your existing GetVanillaCharacterStringKeys usually returns fully qualified keys
                    PageIndex = 0,
                    DisplayPattern = text
                });

                if (this.Config?.developerModeOn == true)
                    this.Monitor?.Log($"[STR] + {key} -> {path}", LogLevel.Trace);

                entryNumber++;
            }

            return outList;
        }

        

    }
}


*/