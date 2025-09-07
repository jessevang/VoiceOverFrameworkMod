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



        private void ApplyDictionaryV1()
        {
            this.Multilingual = new MultilingualDictionary(this, this.Monitor, this.Helper.DirectoryPath);

        }


        public class MultilingualDictionary
        {
            private readonly ModEntry Mod;
            private readonly IMonitor Monitor;
            private readonly string dictionaryPath;

            // cache[Character][SanitizedText] = DialogueFrom
            private readonly Dictionary<string, Dictionary<string, string>> cache =
                new(StringComparer.OrdinalIgnoreCase);

            // Which languages have already been merged per character
            // loadedLangs[Character] = { "en", "ja", ... }
            private readonly Dictionary<string, HashSet<string>> loadedLangs =
                new(StringComparer.OrdinalIgnoreCase);

            public MultilingualDictionary(ModEntry mod, IMonitor monitor, string modFolderPath)
            {
                this.Mod = mod;
                this.Monitor = monitor;
                this.dictionaryPath = Path.Combine(modFolderPath, "Dictionary", "V1");
            }

            /// <summary>
            /// Merge dictionaries for the given languages into the character’s cache.
            /// Re-entrant and idempotent: only loads langs not already loaded.
            /// </summary>
            public void EnsureLoaded(string character, IEnumerable<string> languagesToInclude)
            {
                if (string.IsNullOrWhiteSpace(character)) return;

                var langs = new HashSet<string>(
                    (languagesToInclude ?? Array.Empty<string>())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase
                );
                if (langs.Count == 0) return;

                if (!cache.TryGetValue(character, out var charMap))
                    cache[character] = charMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!loadedLangs.TryGetValue(character, out var loaded))
                    loadedLangs[character] = loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Only load the delta
                langs.ExceptWith(loaded);
                if (langs.Count == 0) return;

                int added = 0;

                foreach (string file in Directory.EnumerateFiles(dictionaryPath, $"{character}_*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var data = JsonConvert.DeserializeObject<VoicePackFile>(json);
                        if (data?.VoicePacks == null) continue;

                        foreach (var pack in data.VoicePacks)
                        {
                            var packLang = (pack?.Language ?? "").ToLowerInvariant();
                            if (string.IsNullOrWhiteSpace(packLang) || !langs.Contains(packLang)) continue;
                            if (pack?.Entries == null) continue;

                            foreach (var entry in pack.Entries)
                            {
                                string preSanitized = entry.DialogueText?.Trim();
                                if (string.IsNullOrWhiteSpace(preSanitized)) continue;

                                // Insert if not already present
                                if (!charMap.ContainsKey(preSanitized))
                                {
                                    charMap[preSanitized] = entry.DialogueFrom;
                                    added++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"[MultilingualDictionary V1] Failed loading file {file}: {ex.Message}", LogLevel.Warn);
                    }
                }

                foreach (var lang in langs)
                    loaded.Add(lang);

                Monitor.Log(
                    $"[MultilingualDictionary V1] {character}: +{added} entries; langs loaded [{string.Join(",", loaded)}]; mapSize={charMap.Count}",
                    LogLevel.Trace
                );
            }

            /// <summary>
            /// Back-compat shim for older call sites. Loads all languages found on disk for the character.
            /// Prefer EnsureLoaded(character, langs) in new code.
            /// </summary>
            public void LoadAllForCharacter(string character)
            {
                if (string.IsNullOrWhiteSpace(character)) return;

                var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(dictionaryPath, $"{character}_*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var data = JsonConvert.DeserializeObject<VoicePackFile>(json);
                        if (data?.VoicePacks == null) continue;

                        foreach (var p in data.VoicePacks)
                        {
                            var lang = (p?.Language ?? "").ToLowerInvariant();
                            if (!string.IsNullOrWhiteSpace(lang))
                                langs.Add(lang);
                        }
                    }
                    catch { /* ignore file errors for back-compat shim */ }
                }

                if (langs.Count > 0)
                    EnsureLoaded(character, langs);
            }

            /// <summary>
            /// Legacy V1 lookup; assumes EnsureLoaded was called with game+pack langs when they differ.
            /// </summary>
            public string GetDialogueFrom(string character, string gameLanguage, string voicePackLanguage, string rawText)
            {
                if (string.IsNullOrWhiteSpace(character)) return null;

                string gameLang = (gameLanguage ?? "").ToLowerInvariant();
                string packLang = (voicePackLanguage ?? "").ToLowerInvariant();
                if (gameLang == packLang) return null;

                if (!cache.TryGetValue(character, out var dict) || dict == null || dict.Count == 0)
                    return null;

                // Replace player's name with @ then sanitize the same way V1 matching expects
                string farmerName = Game1.player?.Name ?? "";
                string reconstructed = rawText?.Replace(farmerName, "@");
                string sanitizedStep1 = Mod.SanitizeDialogueText(reconstructed);
                string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                return dict.TryGetValue(finalLookupKey, out var fromKey) ? fromKey : null;
            }

            public void ClearCache()
            {
                cache.Clear();
                loadedLangs.Clear();
                Monitor.Log("[MultilingualDictionary V1] Cleared all cached dictionary entries.", LogLevel.Trace);
            }
        }


    }
}
