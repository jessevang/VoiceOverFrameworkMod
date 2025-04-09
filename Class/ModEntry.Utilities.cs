using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework.Content; // For IGameContentHelper
using StardewModdingAPI;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // List of known language codes used by Stardew Valley
        private readonly List<string> KnownStardewLanguages = new List<string> {
            "en", "es-ES", "zh-CN", "ja-JP", "pt-BR", "fr-FR", "ko-KR", "it-IT", "de-DE", "hu-HU", "ru-RU", "tr-TR"
            // Add others if supported/needed
        };

        // Fetches dialogue keys/text pairs from Strings/Characters asset for a specific character/language
        private Dictionary<string, string> GetVanillaCharacterStringKeys(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var keyTextPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            // Construct asset key based on language
            string assetKey = isEnglish ? "Strings/Characters" : $"Strings/Characters.{languageCode}";

            try
            {
                var characterStrings = gameContent.Load<Dictionary<string, string>>(assetKey);
                if (characterStrings != null)
                {
                    // Define prefixes to look for (standard and marriage dialogue)
                    string prefix = characterName + "_";
                    string marriagePrefix = "MarriageDialogue." + characterName + "_";

                    // Iterate through loaded strings and add matching ones
                    foreach (var kvp in characterStrings)
                    {
                        if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || kvp.Key.StartsWith(marriagePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            keyTextPairs[kvp.Key] = kvp.Value;
                        }
                    }
                    Monitor.Log($"Loaded {keyTextPairs.Count} matching keys from '{assetKey}' for character '{characterName}'.", LogLevel.Trace);
                }
                else
                {
                    // This case might happen if an asset exists but is empty or malformed, Load returns null.
                    Monitor.Log($"Asset '{assetKey}' loaded as null.", LogLevel.Trace);
                }
            }
            // Handle cases where the language-specific asset doesn't exist
            catch (ContentLoadException) { Monitor.Log($"Asset '{assetKey}' not found (ContentLoadException).", LogLevel.Trace); }
            // Catch other potential errors during loading or parsing
            catch (Exception ex) { Monitor.Log($"Error loading/parsing '{assetKey}': {ex.Message}", LogLevel.Error); Monitor.Log(ex.ToString(), LogLevel.Trace); }

            return keyTextPairs; // Always return the dictionary, even if empty
        }


        // Cleans a string to be safe for use as a filename component.
        private string SanitizeKeyForFileName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "invalid_or_empty_key";

            string originalKey = key; // Keep original for logging if needed

            // Replace common problematic characters with underscores
            key = key.Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace(" ", "_").Replace(".", "_").Replace("'", "");

            // Remove any remaining characters that are not alphanumeric, underscore, or hyphen
            // Allows Unicode letters/numbers (\w includes underscore)
            key = Regex.Replace(key, @"[^\w\-]", "", RegexOptions.None);

            // Limit length to prevent excessively long filenames
            const int MaxLength = 80; // Increased length slightly
            if (key.Length > MaxLength)
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"Sanitized key exceeded max length ({MaxLength}). Truncating original: '{originalKey}' -> '{key.Substring(0, MaxLength)}'", LogLevel.Trace);
                }
                
                key = key.Substring(0, MaxLength);
            }

            // Ensure the key is not empty after sanitization
            if (string.IsNullOrWhiteSpace(key))
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"Sanitization resulted in empty string for key: '{originalKey}'. Using fallback 'sanitized_key'.", LogLevel.Warn);
                }
                
                key = "sanitized_key"; // Provide a fallback name
            }

            // Optional: Prevent starting with hyphen or underscore? (Usually not necessary)

            return key;
        }


        // Checks if a character name corresponds to a known vanilla villager (including marriage candidates and others).
        private bool IsKnownVanillaVillager(string name)
        {
            // Using a HashSet for efficient lookups (case-insensitive)
            // Expanded list based on common villagers/NPCs involved in dialogue. Adjust as needed.
            var knownVanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                // Marriage Candidates
                "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey", "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane",
                // Villagers
                "Caroline", "Clint", "Demetrius", "Evelyn", "George", "Gus", "Jas", "Jodi", "Kent", "Lewis", "Linus", "Marnie",
                "Pam", "Pierre", "Robin", "Vincent", "Willy",
                // Other Key NPCs
                "Wizard", "Krobus", "Dwarf", "Sandy", "Leo", "Gunther", "Marlon", "Morris", // Added Gunther, Marlon, Morris
                "Governor", "Grandpa", "MrQi", "Birdie" // Added others sometimes involved
                // Potentially add festival-specific NPCs if needed? (e.g., Gil)
            };

            bool isKnown = knownVanilla.Contains(name);
            if (Config.developerModeOn)
            {
                Monitor.Log($"Checking if '{name}' is known vanilla: {isKnown}", LogLevel.Trace);
            }
           
            return isKnown;
        }


        // Validates and canonicalizes language codes.
        private string GetValidatedLanguageCode(string requestedLang)
        {
            if (string.IsNullOrWhiteSpace(requestedLang)) return null; // Handle null/empty input

            // Mapping from common inputs/codes to Stardew's specific codes
            var langMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                // Stardew Code -> Stardew Code (Ensure canonical codes map to themselves)
                { "en", "en" }, { "es-ES", "es-ES" }, { "zh-CN", "zh-CN" }, { "ja-JP", "ja-JP" },
                { "pt-BR", "pt-BR" }, { "fr-FR", "fr-FR" }, { "ko-KR", "ko-KR" }, { "it-IT", "it-IT" },
                { "de-DE", "de-DE" }, { "hu-HU", "hu-HU" }, { "ru-RU", "ru-RU" }, { "tr-TR", "tr-TR" },
                // Common Aliases -> Stardew Code
                { "english", "en" }, { "spanish", "es-ES" }, { "chinese", "zh-CN" }, { "japanese", "ja-JP" },
                { "portuguese", "pt-BR" }, { "french", "fr-FR" }, { "korean", "ko-KR" }, { "italian", "it-IT" },
                { "german", "de-DE" }, { "hungarian", "hu-HU" }, { "russian", "ru-RU" }, { "turkish", "tr-TR" },
                // Basic Codes -> Stardew Code (Handle with care, might be ambiguous)
                { "es", "es-ES" }, { "zh", "zh-CN" }, { "ja", "ja-JP" }, { "pt", "pt-BR" },
                { "fr", "fr-FR" }, { "ko", "ko-KR" }, { "it", "it-IT" }, { "de", "de-DE" },
                { "hu", "hu-HU" }, { "ru", "ru-RU" }, { "tr", "tr-TR" }
             };


            if (langMap.TryGetValue(requestedLang.Trim(), out string stardewCode))
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"Validated language '{requestedLang}' -> '{stardewCode}'", LogLevel.Trace);
                }
                
                return stardewCode;
            }

            // If not found in map, maybe it's already a valid (but less common) Stardew code?
            // Check against our known list.
            if (KnownStardewLanguages.Contains(requestedLang.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"Language code '{requestedLang}' not in map but found in KnownStardewLanguages. Using directly.", LogLevel.Trace);
                }
                
                return requestedLang.Trim(); // Return the validated known code
            }

            if (Config.developerModeOn)
            {
                Monitor.Log($"Language code '{requestedLang}' not recognized or mapped to a known Stardew language code. Cannot validate.", LogLevel.Warn);
            }
            
            // Return null or original? Returning null is safer to prevent unexpected asset load attempts.
            return null;
        }

        // Cleans dialogue text by removing game-specific codes and extra whitespace.
        // CRITICAL: Ensure this matches the cleaning done before lookup in TryToPlayVoice/GetRelativeAudioPath!
        // Make sure you have this at the top of the file:


        // Place this method inside your ModEntry class (ModEntry.Utilities.cs or wherever it belongs)
        private string SanitizeDialogueText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ""; // Return empty for null/whitespace input

            string originalText = text; // Keep for logging if needed
            string sanitized = text; // Work on a copy

            // --- ORDER OF OPERATIONS IS CRUCIAL ---
            // Remove complex blocks with '#' delimiters FIRST.

            // 1. Remove speaker prefix (e.g., "^Abigail ")
            sanitized = Regex.Replace(sanitized, @"^\^.+?\s+", "");

            // 2. *** UPDATED: Remove $q blocks, including potential starting '#' and ending '#' ***
            // Matches optional '#', $q, optional space(s), parameters (non-# chars), mandatory '#'
            sanitized = Regex.Replace(sanitized, @"#?\$q\s*[^#]+?#", "");

            // 3. *** NEW/UPDATED: Remove $r blocks (dialogue choices), including '#' delimiters ***
            // Matches optional '#', $r, space(s), digit(s), space(s), optional '-' + digit(s), space(s), key (non-space chars), mandatory '#'
            sanitized = Regex.Replace(sanitized, @"#?\$r\s+\d+\s+-?\d+\s+\S+#", "");

            // 4. Remove specific Stardew codes ($h, $s, $a, $l, $u, $p, $k, $b, $c[color], $t etc.)
            // This covers $letter and $letter[parameter].
            // Run this *after* the more complex $q/$r to avoid partial matches.
            sanitized = Regex.Replace(sanitized, @"\$[a-zA-Z](\[[^\]]+\])?", "");

            // 5. Remove $number pause codes (e.g., $1, $8)
            sanitized = Regex.Replace(sanitized, @"\$\d+", "");

            // 6. Remove SMAPI-style tokens (like %adj%, %noun%, etc.)
            sanitized = Regex.Replace(sanitized, @"%[a-zA-Z0-9_]+%", ""); // Match %token%

            // 7. Remove player name token (@) - Replace with space might be better for flow? Test needed.
            // sanitized = sanitized.Replace("@", " "); // Option: Replace with space
            sanitized = sanitized.Replace("@", "");    // Option: Remove completely

            // 8. Remove page/expression markers (#$e#, #$b#) - Often handled by splitting, but keep for safety.
            //    The optional starting '#' in rules 2 & 3 might catch some, but this is explicit.
            sanitized = Regex.Replace(sanitized, @"#\$[eb]#", "");

            // 9. Remove simple formatting characters (^ < > \) - ^ at start handled earlier.
            sanitized = Regex.Replace(sanitized, @"[\^<>\\]", "");

            // 10. Collapse multiple whitespace characters into a single space.
            sanitized = Regex.Replace(sanitized, @"\s+", " ");

            // 11. Final Trim: Remove leading/trailing whitespace.
            sanitized = sanitized.Trim();

            // Developer Logging
            if (sanitized != originalText && Config.developerModeOn)
            {
                // Use Verbose level for potentially spammy sanitization logs
                Monitor.Log($"Sanitized Text: \"{originalText}\" -> \"{sanitized}\"", LogLevel.Trace);
            }

            return sanitized;
        }

    }
}