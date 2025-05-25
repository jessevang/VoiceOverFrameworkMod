using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;


/**
 * Logic used to allow dub Voice pack over different desired language dialogue
 * 1. Loads all language dialogue in dictionary if config option allows it
 * 2. Will later sanitize the language dialogue using dictioinary to find the actualy voice pack language then compare to find that audio to play.
 * 
 * 
 */
namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        public class MultilingualDictionary
        {
            private readonly ModEntry Mod;
            private readonly IMonitor Monitor;
            private readonly string dictionaryPath;

            // cache[Character][SanitizedText] = DialogueFrom
            private readonly Dictionary<string, Dictionary<string, string>> cache = new();

            public MultilingualDictionary(ModEntry mod, IMonitor monitor, string modFolderPath)
            {
                this.Mod = mod;
                this.Monitor = monitor;
                this.dictionaryPath = Path.Combine(modFolderPath, "Dictionary");
            }

            public void LoadAllForCharacter(string character)
            {
                var mergedDict = new Dictionary<string, string>();
                foreach (string file in Directory.EnumerateFiles(dictionaryPath, $"{character}_*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var data = JsonConvert.DeserializeObject<VoicePackFile>(json);

                        foreach (var pack in data.VoicePacks)
                        {
                            foreach (var entry in pack.Entries)
                            {
                                string preSanitized = entry.DialogueText?.Trim();
                                if (!string.IsNullOrEmpty(preSanitized) && !mergedDict.ContainsKey(preSanitized))
                                    mergedDict[preSanitized] = entry.DialogueFrom;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"[MultilingualDictionary] Failed loading file {file}: {ex.Message}", LogLevel.Warn);
                    }
                }

                cache[character] = mergedDict;
                Monitor.Log($"[MultilingualDictionary] Loaded {mergedDict.Count} dictionary entries for {character}", LogLevel.Trace);
            }


            
            public string GetDialogueFrom(string character, string gameLanguage, string voicePackLanguage, string rawText)
            {
                if (gameLanguage == voicePackLanguage)
                    return null;

                if (!cache.TryGetValue(character, out var dict))
                    return null;

                // Step 1: Replace player's name with @
                string farmerName = Game1.player?.Name ?? "";
                string reconstructed = rawText?.Replace(farmerName, "@");

                // Step 2: Apply main sanitization
                string sanitizedStep1 = Mod.SanitizeDialogueText(reconstructed);

                // Step 3: Remove #tag# formatting after sanitization
                string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                // Step 4: Attempt dictionary lookup
                return dict.TryGetValue(finalLookupKey, out var fromKey) ? fromKey : null;
            }



            public void ClearCache()
            {
                cache.Clear();
                Monitor.Log("[MultilingualDictionary] Cleared all cached dictionary entries.", LogLevel.Trace);
            }
        }
    }
}
