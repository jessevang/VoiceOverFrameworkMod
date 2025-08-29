
using Microsoft.Xna.Framework.Content; // For IGameContentHelper
using StardewModdingAPI;
using StardewValley;
using System.Text.RegularExpressions;


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
            var knownVanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Marriage Candidates
                "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey", "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane",

                // Villagers
                "Caroline", "Clint", "Demetrius", "Evelyn", "George", "Gus", "Jas", "Jodi", "Kent", "Lewis", "Linus", "Marnie",
                "Pam", "Pierre", "Robin", "Vincent", "Willy",

                // Key NPCs / Special Characters
                "Wizard", "Krobus", "Dwarf", "Sandy", "Leo", "LeoMainland", "Gunther", "Marlon", "Gil", "Morris",
                "Governor", "Grandpa", "Mister Qi", "Birdie", "Bouncer", "Henchman", "Professor Snail"
            };

            bool isKnown = knownVanilla.Contains(name);
            if (Config.developerModeOn)
            {
                Monitor.Log($"Checking if '{name}' is known vanilla: {isKnown}", LogLevel.Trace);
            }

            return isKnown;
        }


        private static readonly string[] SpouseNames = new[]
        {
            "Abigail","Alex","Elliott","Emily","Haley","Harvey","Leah",
            "Maru","Penny","Sam","Sebastian","Shane","Krobus"
        };

        /// <summary>
        /// Load marriage dialogue (language-aware) + marriage dilaogue for a specific spouse.
        /// Merges:
        ///   - Characters/Dialogue/MarriageDialogue(.lang)   [generic]
        ///       * includes generic keys WITHOUT any "_Name" suffix
        ///       * includes generic keys WITH "_{characterName}" suffix only
        ///   - Characters/Dialogue/MarriageDialogue{Name}(.lang)  [per-spouse override]
        ///
        /// TranslationKey:
        ///   - Generic:      "Characters/Dialogue/MarriageDialogue:{Key}"
        ///   - Per-spouse:   "Characters/Dialogue/MarriageDialogue{Name}:{Key}"
        ///
        /// Returns List of (RawText, SourceInfo, TranslationKey)
        ///   - SourceInfo is a readable tag like "Marriage/{Name}/{Key}" (you'll store this in sourceTracking)
        ///   - RawText is unsanitized; your existing Split/Sanitize runs later
        /// </summary>
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetMarriageDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new List<(string, string, string)>();

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // 1) Load generic sheet (MarriageDialogue)
            Dictionary<string, string> generic = null;
            try
            {
                try
                {
                    generic = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    generic = gameContent.Load<Dictionary<string, string>>("Characters/Dialogue/MarriageDialogue");
                }
            }
            catch (ContentLoadException) { /* ignore */ }
            catch (Exception ex) { Monitor.Log($"Error reading MarriageDialogue{langSuffix}: {ex.Message}", LogLevel.Trace); }

            // 2) Load per-spouse sheet (MarriageDialogue{Name})
            Dictionary<string, string> perSpouse = null;
            try
            {
                try
                {
                    perSpouse = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{characterName}{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    perSpouse = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{characterName}");
                }
            }
            catch (ContentLoadException) { /* ignore */ }
            catch (Exception ex) { Monitor.Log($"Error reading MarriageDialogue{characterName}{langSuffix}: {ex.Message}", LogLevel.Trace); }

            // 3) Build merged set (key -> tuple), so per-spouse can override
            var merged = new Dictionary<string, (string Raw, string SourceInfo, string TK)>(StringComparer.OrdinalIgnoreCase);

            // 3a) From generic sheet:
            if (generic != null && generic.Count > 0)
            {
                foreach (var kvp in generic)
                {
                    string key = kvp.Key?.Trim();
                    string raw = kvp.Value;

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    // keep keys that have NO spouse suffix OR have _{characterName}
                    bool endsWithAnySpouse = SpouseNames.Any(n => key.EndsWith("_" + n, StringComparison.OrdinalIgnoreCase));
                    bool endsWithThisSpouse = key.EndsWith("_" + characterName, StringComparison.OrdinalIgnoreCase);

                    if (!endsWithAnySpouse || endsWithThisSpouse)
                    {
                        // generic translation key base
                        string tk = $"Characters/Dialogue/MarriageDialogue:{key}";
                        string src = $"Marriage/{characterName}/{key}";
                        merged[key] = (raw, src, tk);
                    }
                }
            }

            // 3b) Overlay with per-spouse sheet (wins on conflicts)
            if (perSpouse != null && perSpouse.Count > 0)
            {
                foreach (var kvp in perSpouse)
                {
                    string key = kvp.Key?.Trim();
                    string raw = kvp.Value;
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    string tk = $"Characters/Dialogue/MarriageDialogue{characterName}:{key}";
                    string src = $"Marriage/{characterName}/{key}";
                    merged[key] = (raw, src, tk); // override/add
                }
            }

            // 4) Emit
            foreach (var kvp in merged.OrderBy(k => k.Key))
                results.Add((kvp.Value.Raw, kvp.Value.SourceInfo, kvp.Value.TK));

            if (Config.developerModeOn)
                Monitor.Log($"[Marriage] {characterName}: {results.Count} merged lines.", LogLevel.Trace);

            return results;
        }


        /// <summary>
        /// Load "Strings/MovieReactions" for a specific character (language-aware) V2.
        /// Emits a stable TranslationKey per JSON key:
        ///   Strings/MovieReactions:{JsonKey}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        ///   - InnerId is just a unique handle inside this method (not used elsewhere)
        ///   - SourceInfo becomes your DialogueFrom base, e.g. "MovieReactions/Penny_*_BeforeMovie"
        /// Notes:
        ///   - We include all lines for the NPC (including "*", per-movie, and stage directions like "DuringMovie_2").
        ///   - Placeholders like {0} {2} are preserved as-is; your sanitizer will handle them later.
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetMovieReactionsForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // Try localized first, then English fallback
            Dictionary<string, string> dict = null;
            string assetLang = $"Strings/MovieReactions{langSuffix}";
            string assetEn = "Strings/MovieReactions";

            try
            {
                dict = gameContent.Load<Dictionary<string, string>>(assetLang);
            }
            catch (ContentLoadException)
            {
                try { dict = gameContent.Load<Dictionary<string, string>>(assetEn); }
                catch (ContentLoadException) { /* ignore */ }
                catch (Exception ex2) { Monitor?.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace); }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
                return results;
            }

            if (dict == null || dict.Count == 0)
                return results;

            string prefix = characterName + "_";
            foreach (var kvp in dict)
            {
                string jsonKey = kvp.Key ?? "";
                if (!jsonKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = kvp.Value ?? "";
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Stable identifiers
                string innerId = $"MovieReactions:{jsonKey}";
                string sourceInfo = $"MovieReactions/{jsonKey}";
                string translationKey = $"Strings/MovieReactions:{jsonKey}";

                results[innerId] = (raw, sourceInfo, translationKey);
            }

            if (Config?.developerModeOn == true)
                Monitor?.Log($"[MovieReactions] {characterName}: found {results.Count} entries.", LogLevel.Trace);

            return results;
        }


        /// <summary>
        /// Load 'Data/Mail' lines authored by a specific character (language-aware) V2.
        /// Uses the JSON key when possible (e.g., "Robin", "EmilyCooking", "quest10")
        /// and/or parses the trailing signature in the body (e.g., "^-Lewis", "-M. Rasmodius, Wizard").
        /// Emits a real TranslationKey: "Data/Mail:{JsonKey}" and a readable DialogueFrom: "Mail/{JsonKey}".
        /// Returns List of (RawText, SourceInfo, TranslationKey).
        /// </summary>
        private List<(string RawText, string SourceInfo, string TranslationKey)>
    GetMailDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent, bool keepSignature = true)
        {
            var results = new List<(string, string, string)>();
            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            Dictionary<string, string> dict = null;
            try { dict = gameContent.Load<Dictionary<string, string>>($"Data/Mail{langSuffix}"); }
            catch (ContentLoadException)
            {
                try { dict = gameContent.Load<Dictionary<string, string>>("Data/Mail"); }
                catch (ContentLoadException) { /* ignore */ }
                catch (Exception ex2) { Monitor?.Log($"Error loading 'Data/Mail' fallback: {ex2.Message}", LogLevel.Trace); }
            }
            catch (Exception ex) { Monitor?.Log($"Error loading 'Data/Mail{langSuffix}': {ex.Message}", LogLevel.Trace); }

            if (dict == null || dict.Count == 0) return results;

            foreach (var kvp in dict)
            {
                string jsonKey = kvp.Key ?? "";
                string raw = kvp.Value ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // detect the author (uses signature first, then key prefix)
                string author = TryDetectMailAuthor(jsonKey, raw);
                if (!author.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // keep only the readable body (before % or [ meta)
                string body = raw;
                int metaCut = IndexOfAny(body, new[] { '%', '[' });
                if (metaCut >= 0) body = body.Substring(0, metaCut);

                body = body.Trim();

                // >>> changed: optionally keep or strip the trailing "-Name" signature <<<
                if (!keepSignature)
                    body = StripTrailingSignature(body);

                // convert caret line breaks
                body = NormalizeMailBreaks(body);

                if (string.IsNullOrWhiteSpace(body)) continue;

                string sourceInfo = $"Mail/{jsonKey}";
                string tk = $"Data/Mail:{jsonKey}";
                results.Add((body, sourceInfo, tk));
            }

            return results;

            // ------- helpers for getting dialogue for mail -------
            static int IndexOfAny(string s, char[] any)
            {
                if (string.IsNullOrEmpty(s)) return -1;
                int best = -1;
                foreach (var c in any)
                {
                    int i = s.IndexOf(c);
                    if (i >= 0 && (best < 0 || i < best)) best = i;
                }
                return best;
            }

            static string NormalizeMailBreaks(string body)
            {
                body = body.Replace("^^", "#$e#");
                body = body.Replace("^", "#$b#");
                return body.Trim();
            }

            static string StripTrailingSignature(string body)
            {
                var sigRegex = new System.Text.RegularExpressions.Regex(
                    @"(\^|\s|^)-\s*[^\^%\[\r\n]+$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                string trimmed = body.TrimEnd();
                var m = sigRegex.Match(trimmed);
                if (m.Success && m.Index >= 0)
                {
                    trimmed = trimmed.Substring(0, m.Index).TrimEnd();
                    while (trimmed.EndsWith("^", StringComparison.Ordinal)) // clean dangling carets
                        trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                }
                return trimmed;
            }

            // --- Replace TryDetectMailAuthor(...) with this ---
            string TryDetectMailAuthor(string jsonKey, string raw)
            {
                // 1) try explicit trailing signature first
                string body = raw;
                int metaCut = IndexOfAny(body, new[] { '%', '[' });
                if (metaCut >= 0) body = body.Substring(0, metaCut);

                var sigRegex = new System.Text.RegularExpressions.Regex(
                    @"-\s*([^\^%\[\r\n]+)$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Multiline);

                string sigName = null;
                var m = sigRegex.Match(body.TrimEnd());
                if (m.Success) sigName = m.Groups[1].Value.Trim();

                string normSig = NormalizeSignatureToNpcName(sigName);
                if (!string.IsNullOrEmpty(normSig))
                    return normSig;

                // 2) fall back to the JSON key: split into words (handles camelCase, underscores, digits)
                foreach (var token in SplitKeyWords(jsonKey))
                {
                    string mapped = NormalizeSignatureToNpcName(token);
                    if (!string.IsNullOrEmpty(mapped))
                        return mapped;
                }

                // 3) last resort: prefix letters (old behavior)
                string fromKeyPrefix = null;
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (char ch in jsonKey) { if (char.IsLetter(ch)) sb.Append(ch); else break; }
                    fromKeyPrefix = sb.ToString();
                }

                string normPrefix = NormalizeSignatureToNpcName(fromKeyPrefix);
                return normPrefix ?? "";
            }

            // --- Replace NormalizeSignatureToNpcName(...) with this ---
            static string NormalizeSignatureToNpcName(string rawName)
            {
                if (string.IsNullOrWhiteSpace(rawName)) return "";

                // canonical alias map (add more here if you encounter them)
                // keys are compared case-insensitively
                var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // nicknames / variants
                    ["abby"] = "Abigail",
                    ["m. rasmodius"] = "Wizard",
                    ["rasmodius"] = "Wizard",
                    ["wizard"] = "Wizard",
                    ["mr. qi"] = "Qi",
                    ["qi"] = "Qi",
                    ["granny evelyn"] = "Evelyn",
                    ["granny"] = "Evelyn",
                    ["mayor lewis"] = "Lewis",
                    ["hat mouse"] = "Hat Mouse",

                    // common “sign-off” phrases that shouldn’t be taken as names
                    ["love"] = "",
                    ["your friend"] = "",
                    ["your"] = "",
                    ["friend"] = "",
                    ["the mayor"] = "Lewis"
                };

                // scrub punctuation/honorifics, normalize spaces
                string s = rawName.Trim();
                int comma = s.IndexOf(','); if (comma >= 0) s = s.Substring(0, comma).Trim();

                // remove leading honorifics/titles
                string[] drop = { "Mr.", "Mr", "Mrs.", "Mrs", "Ms.", "Ms", "Dr.", "Dr", "Doctor", "Mayor", "Sir", "Madam", "Professor", "Granny" };
                foreach (var d in drop)
                    if (s.StartsWith(d + " ", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(d.Length).TrimStart(); break; }

                if (alias.TryGetValue(s, out var mappedFull)) return mappedFull;

                var tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) return "";

                if (alias.TryGetValue(tokens[0], out var mappedFirst) && !string.IsNullOrEmpty(mappedFirst)) return mappedFirst;
                if (alias.TryGetValue(tokens[^1], out var mappedLast) && !string.IsNullOrEmpty(mappedLast)) return mappedLast;

                // heuristic: if there are multiple capitalized tokens, prefer the last (e.g., "Mayor Lewis" → "Lewis")
                if (tokens.Length >= 2 && char.IsUpper(tokens[^1][0]))
                    return tokens[^1];

                // single token: keep it only if it matches a known NPC-ish token (avoid returning things like "afterSamShow")
                if (LooksLikeNpcName(tokens[0]))
                    return tokens[0];

                return "";
            }

            // --- Add these two small helpers somewhere in the same class ---

            static IEnumerable<string> SplitKeyWords(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) yield break;

                // split on non-letters/underscores first
                foreach (var chunk in System.Text.RegularExpressions.Regex.Split(key, @"[^A-Za-z]+"))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;

                    // further split camelCase into parts: abbySpiritBoard -> abby, Spirit, Board
                    var parts = System.Text.RegularExpressions.Regex
                        .Matches(chunk, @"([A-Z]?[a-z]+|[A-Z]+(?![a-z]))")
                        .Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => m.Value);

                    foreach (var p in parts)
                        if (!string.IsNullOrEmpty(p))
                            yield return p;
                }
            }

            static bool LooksLikeNpcName(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                // quick allow-list to guard false positives; extend as needed
                string[] npc =
                {
                    "Abigail","Alex","Caroline","Clint","Demetrius","Dwarf","Elliott","Emily","Evelyn",
                    "George","Gus","Haley","Harvey","Jas","Jodi","Kent","Krobus","Leah","Leo","Lewis",
                    "Linus","Marnie","Maru","Pam","Penny","Pierre","Robin","Sam","Sandy","Sebastian",
                    "Shane","Vincent","Willy","Wizard","Morris","Gunther","Marlon","Qi","Hat","Mouse"
                };

                // plus “Abby” special-case already handled
                return npc.Any(n => n.Equals(token, StringComparison.OrdinalIgnoreCase));
            }



        }




        /// <summary>
        /// Load festival dialogue lines for a specific character (language-aware) V2.
        /// Emits real TranslationKeys for sheet keys like:
        ///   Data/Festivals/{festId}:{Character}[_spouse][_y2][_spouse_y2]
        /// Also scans embedded script lines like:
        ///   speak {Character} "..."
        /// and assigns a synthetic TranslationKey:
        ///   Festivals/{festId}:{Character}:s{index}
        /// Returns: InnerId -> (RawText, SourceInfo, TranslationKey)
        ///   - InnerId is just a unique handle inside this method (not used elsewhere).
        ///   - SourceInfo is a readable DialogueFrom base, e.g. "Festival/fall16/Abigail_spouse"
        /// </summary>
        private Dictionary<string, (string RawText, string SourceInfo, string TranslationKey)>
            GetFestivalDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // 1) Discover festival IDs from FestivalDates (try target language, then English fallback)
            var festivalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> dates = null;
            try
            {
                dates = gameContent.Load<Dictionary<string, string>>($"Data/Festivals/FestivalDates{langSuffix}");
            }
            catch (ContentLoadException) { /* ignore */ }
            catch (Exception ex) { Monitor.Log($"Error loading FestivalDates{langSuffix}: {ex.Message}", LogLevel.Trace); }

            if (dates == null)
            {
                try
                {
                    dates = gameContent.Load<Dictionary<string, string>>("Data/Festivals/FestivalDates");
                }
                catch (ContentLoadException) { /* ignore */ }
                catch (Exception ex) { Monitor.Log($"Error loading FestivalDates (fallback): {ex.Message}", LogLevel.Trace); }
            }

            if (dates != null)
            {
                foreach (var k in dates.Keys)
                    festivalIds.Add(k);
            }

            // Vanilla fallback if nothing was discovered
            if (festivalIds.Count == 0)
            {
                foreach (var k in new[] { "spring13", "spring24", "summer11", "summer28", "fall16", "fall27", "winter8", "winter25" })
                    festivalIds.Add(k);
            }

            // 2) For each festival sheet, harvest dialogue
            string[] suffixes = new[] { "", "_spouse", "_y2", "_spouse_y2" };
            var speakRegex = new Regex($@"\bspeak\s+{Regex.Escape(characterName)}\s+""([^""]+)""",
                                       RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            foreach (var festId in festivalIds.OrderBy(s => s))
            {
                // Try language-specific sheet, then English fallback
                Dictionary<string, string> dict = null;
                string assetLang = $"Data/Festivals/{festId}{langSuffix}";
                string assetEn = $"Data/Festivals/{festId}";

                try
                {
                    dict = gameContent.Load<Dictionary<string, string>>(assetLang);
                }
                catch (ContentLoadException)
                {
                    try { dict = gameContent.Load<Dictionary<string, string>>(assetEn); }
                    catch (ContentLoadException) { /* skip */ }
                    catch (Exception ex2) { Monitor.Log($"Error loading '{assetEn}': {ex2.Message}", LogLevel.Trace); }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error loading '{assetLang}': {ex.Message}", LogLevel.Trace);
                    continue;
                }

                if (dict == null || dict.Count == 0)
                    continue;

                // 2a) Real keyed entries (Abigail, Abigail_spouse, Abigail_y2, Abigail_spouse_y2)
                foreach (var suffix in suffixes)
                {
                    string fullKey = suffix.Length == 0 ? characterName : (characterName + suffix);
                    if (!dict.TryGetValue(fullKey, out var raw) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    string innerId = $"{festId}:{fullKey}";
                    string sourceInfo = $"Festival/{festId}/{fullKey}";
                    string tk = $"Data/Festivals/{festId}:{fullKey}";

                    // keep RAW text; V2 sanitizer runs later
                    results[innerId] = (raw, sourceInfo, tk);
                }

                // 2b) Embedded 'speak Character "..."' lines inside any festival field (e.g., set-up scripts)
                int speakIndex = 0;
                foreach (var kvp in dict)
                {
                    var value = kvp.Value ?? "";
                    if (value.Length == 0) continue;

                    foreach (Match m in speakRegex.Matches(value))
                    {
                        string raw = m.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(raw))
                            continue;

                        // Unique inner id per match
                        string innerId = $"{festId}:{characterName}:speak:{speakIndex}";
                        string sourceInfo = $"Festival/{festId}/{characterName}:speak:{speakIndex}";
                        // Synthetic translation key (not a real content key, but stable & searchable)
                        string tk = $"Festivals/{festId}:{characterName}:s{speakIndex}";

                        results[innerId] = (raw, sourceInfo, tk);
                        speakIndex++;
                    }
                }

                if (Config.developerModeOn)
                {
                    int realCount = suffixes.Count(s => dict.ContainsKey(characterName + s));
                    int speakCount = dict.Values.Sum(v => speakRegex.Matches(v).Count);
                    Monitor.Log($"[Festival] {characterName}: '{festId}' -> {realCount} keyed + {speakCount} speak lines.", LogLevel.Trace);
                }
            }

            return results;
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








        // Place this method inside your ModEntry class (ModEntry.Utilities.cs or wherever it belongs)
        public string SanitizeDialogueText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ""; // Return empty for null/whitespace input

            string originalText = text; // Keep for logging if needed
            string sanitized = text; // Work on a copy

            // --- ORDER OF OPERATIONS IS CRUCIAL ---

            // 1. Remove speaker prefix (e.g., "^Abigail ")
            sanitized = Regex.Replace(sanitized, @"^\^.+?\s+", ""); // This ^ is for speaker prefix

            // 1.5  NEW RULE (1.5): Remove descriptive prefixes like '%George is ...' ***
            sanitized = Regex.Replace(sanitized, @"^%\S+\s+", "");

            // 2. Remove $q blocks
            sanitized = Regex.Replace(sanitized, @"#?\$q\s*[^#]+?#", "");

            // 3. Remove $r blocks
            sanitized = Regex.Replace(sanitized, @"#?\$r\s+\d+\s+-?\d+\s+\S+#", "");

            // 4. Remove specific Stardew codes ($h, $s, $a, etc.)
            sanitized = Regex.Replace(sanitized, @"\$[a-zA-Z](\[[^\]]+\])?", "");

            // 5. Remove $number pause codes
            sanitized = Regex.Replace(sanitized, @"\$\d+", "");

            // 6. *** MODIFIED: Remove SMAPI-style tokens (%adj%, %noun%, %noturn, etc.) ***
            // Matches '%' followed by letters/numbers/underscore, THEN an OPTIONAL closing '%'.
            // This catches both %token% and %token formats.
            sanitized = Regex.Replace(sanitized, @"%[a-zA-Z0-9_]+%?", ""); // Added '?' to make closing % optional

            // 7. Remove player name token (@)
            sanitized = sanitized.Replace("@", "");

            // 8. Remove page/expression markers (#$e#, #$b#)
            sanitized = Regex.Replace(sanitized, @"#\$[eb]#", "");

            // 9. Remove simple formatting characters (< > \) BUT KEEP ^
            sanitized = Regex.Replace(sanitized, @"[<>\\]", "");

            // 9.5 Remove #emotes#
            sanitized = Regex.Replace(sanitized, @"#.*?#", "");

            // 9.6 Remove mod metadata like [(O)mod.id]
            string beforeBrackets = sanitized;
            sanitized = Regex.Replace(sanitized, @"\[[^\]]+\]", ""); // 🔥 This is the working version

            // 10. Collapse multiple whitespace characters
            sanitized = Regex.Replace(sanitized, @"\s+", " ");

            // 11. Final Trim
            sanitized = sanitized.Trim();

            // Developer Logging
            if (sanitized != originalText && Config.developerModeOn)
            {
                Monitor.Log($"Sanitized Text (Improved %): \"{originalText}\" -> \"{sanitized}\"", LogLevel.Trace);
            }

            return sanitized;
        }



        // ====================== V2 Helpers & Sanitizers ======================

        /// <summary>
        /// Split a raw dialogue value into 'pages' the same way the game intends:
        /// - '#$e#' => end of a dialogue page (new bubble)
        /// - '#$b#' => line break within the same page
        /// We remove both markers; '#$b#' becomes "\n".
        /// </summary>
        internal static List<string> SplitStandardDialogueSegmentsV2(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new List<string>();

            // normalize newlines for easier handling
            string s = raw.Replace(Environment.NewLine, " ");

            // turn '#$b#' into a literal newline (kept inside the same page)
            s = Regex.Replace(s, @"#\$b#", "\n", RegexOptions.CultureInvariant);

            // split by '#$e#' into pages
            var pages = Regex.Split(s, @"#\$e#", RegexOptions.CultureInvariant)
                             .Select(p => p.Trim())
                             .Where(p => !string.IsNullOrEmpty(p))
                             .ToList();

            return pages;
        }

        /// <summary>
        /// Try to split a page into gender variants.
        /// Supports the official syntax '${male^female(^neutral)?}' (preferred),
        /// and a lenient fallback 'male^female' if seen in content.
        /// Returns true and fills variants if a split happened; otherwise false.
        /// </summary>
        internal static bool TrySplitGenderVariants(string page, out List<(string text, string gender)> variants)
        {
            variants = null;
            if (string.IsNullOrWhiteSpace(page))
                return false;

            // 1) Official SV syntax: ${male^female(^neutral)?}
            //    We replace the whole token with the chosen branch later; for pack building
            //    we want *three* separate candidate lines.
            var m = Regex.Match(page, @"\$\{([^}]+)\}", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                // split content inside ${...}
                var bits = m.Groups[1].Value.Split('^');
                if (bits.Length >= 2)
                {
                    string male = bits[0].Trim();
                    string female = bits[1].Trim();
                    string neutral = bits.Length >= 3 ? bits[2].Trim() : null;

                    variants = new List<(string, string)>();
                    // Replace the entire ${...} with each branch and create variants
                    if (!string.IsNullOrEmpty(male))
                        variants.Add((Regex.Replace(page, @"\$\{[^}]+\}", male), "male"));
                    if (!string.IsNullOrEmpty(female))
                        variants.Add((Regex.Replace(page, @"\$\{[^}]+\}", female), "female"));
                    if (!string.IsNullOrEmpty(neutral))
                        variants.Add((Regex.Replace(page, @"\$\{[^}]+\}", neutral), "neutral"));

                    // if for some reason no variants were valid, fall through to the loose splitter
                    if (variants.Count > 0)
                        return true;
                }
            }

            // 2) Loose fallback: a single caret separates male^female (seen in some older content or mods)
            //    We only split if there's exactly one caret and the halves are non-empty.
            int caretIdx = page.IndexOf('^');
            if (caretIdx > 0 && caretIdx < page.Length - 1 && page.Count(c => c == '^') == 1)
            {
                string male = page.Substring(0, caretIdx).Trim();
                string female = page.Substring(caretIdx + 1).Trim();
                if (!string.IsNullOrEmpty(male) && !string.IsNullOrEmpty(female))
                {
                    variants = new List<(string, string)>
            {
                (male, "male"),
                (female, "female")
            };
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// V2: Normalize dynamic content to a stable display pattern for storage/matching.
        /// - Keep the actual words, but normalize dynamic inserts:
        ///   - Player name token '@' -> '{PLAYER}'  (assumes caller already replaced farmer's real name with '@' if present)
        ///   - %tokens% (Lexicon etc) -> '{VAR:token}' (token name kept)
        ///   - Remove $moods ($h,$s,...) and $N pauses
        ///   - Remove inline emotes '#...#' and metadata '[...]'
        ///   - Remove speaker '^Name ' prefixes
        ///   - Collapse whitespace; preserve \n inserted by '#$b#'
        /// This is used both during template generation and (optionally) as a fuzzy fallback at runtime.
        /// </summary>
        private string SanitizeDialogueTextV2(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string original = text;
            string s = text;

            // 0) normalize newlines (protect \n we inserted from '#$b#')
            //    (if you already split & replaced '#$b#' earlier, this will just keep them)
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");

            // 1) Remove speaker prefix like '^Abigail ' (only if it's truly a leading '^word ')
            s = Regex.Replace(s, @"^\^\S+\s+", "", RegexOptions.CultureInvariant);

            // 2) Strip $q/$r blocks (branch headers), but you’ll track BranchId separately at runtime
            s = Regex.Replace(s, @"#?\$q\s*[^#]+?#", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"#?\$r\s+\d+\s+-?\d+\s+\S+#", "", RegexOptions.CultureInvariant);

            // 3) Remove $mood ($h, $s, $a, etc) + $number pauses
            s = Regex.Replace(s, @"\$[a-zA-Z](\[[^\]]+\])?", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\$\d+", "", RegexOptions.CultureInvariant);

            // 4) Normalize %tokens% to {VAR:token}
            //    Note: keep token name to disambiguate lines like "%adj% apple" vs "%noun% apple".
            s = Regex.Replace(
                s,
                @"%([a-zA-Z0-9_]+)%?",
                m => $"{{VAR:{m.Groups[1].Value}}}",
                RegexOptions.CultureInvariant
            );

            // 5) Player name token '@' -> {PLAYER}  (we do NOT remove it in V2)
            s = s.Replace("@", "{PLAYER}");

            // 6) Remove inline #emotes# / #meta#
            s = Regex.Replace(s, @"#.+?#", "", RegexOptions.CultureInvariant);

            // 7) Remove bracketed metadata like [(O)mod.id]
            s = Regex.Replace(s, @"\[[^\]]+\]", "", RegexOptions.CultureInvariant);

            // 8) Remove simple formatting characters < > \
            s = Regex.Replace(s, @"[<>\\]", "", RegexOptions.CultureInvariant);

            // 9) Collapse spaces around newlines, then collapse internal whitespace
            //    Keep newlines (line breaks) as structural separators inside the page.
            s = Regex.Replace(s, @"[ \t]*\n[ \t]*", "\n", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant);

            // 10) Trim
            s = s.Trim();

            if (Config.developerModeOn && !string.Equals(original, s, StringComparison.Ordinal))
                Monitor.Log($"[V2] DisplayPattern: \"{original}\" -> \"{s}\"", LogLevel.Trace);

            return s;
        }





    }
}