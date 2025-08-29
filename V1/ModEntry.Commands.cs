using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions; 
using Microsoft.Xna.Framework.Content;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities; 
using StardewValley;
using System.Reflection;
using StardewValley.Util;
using static StardewValley.LocalizedContentManager;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private void SetupConsoleCommands(ICommandHelper commands)
        {

            
            commands.Add(
                name: "create_template",
                documentation: "Generates template JSON voice files for characters.\n\n" +
                               "Usage: create_template <CharacterName|all|modded|*match*|!*exclude*> <LanguageCode|all> <YourPackID> <YourPackName> <StartingAudioFileNumber> [wav|ogg]\n" +
                               "  - CharacterName: Specific NPC name (e.g., Abigail), 'all', 'modded', wildcard '*text', or negate with '!*text'.\n" +
                               "  - LanguageCode: Specific code (en, es-ES, etc.) or 'all'.\n" +
                               "  - YourPackID: Base unique ID for your pack (e.g., YourName.FancyVoices).\n" +
                               "  - YourPackName: Display name for your pack (e.g., Fancy Voices).\n" +
                               "  - StartingAudioFileNumber: The starting number to use for generated audio paths (e.g., 1).\n" +
                               "  - [Optional] AudioFormat: 'wav' or 'ogg' (defaults to 'wav' if not specified).\n\n" +
                               "Examples:\n" +
                               "  create_template Abigail en MyName.AbigailVoice AbigailVoice 1\n" +
                               "  create_template all en MyName.AllVanillaVoices AllVanillaVoices 1\n" +
                               "  create_template *moddedContentName* en My.ModdedContentPack ModdedVoices 1 ogg\n\n" +
                               "Output files will be in 'Mods/VoiceOverFrameworkMod/YourPackName_Templates'.",
                callback: this.GenerateTemplateCommand
            );

            

            commands.Add(
                name: "list_characters",
                documentation: "Lists all loaded characters and shows whether they are vanilla or modded.\n\n" +
                                "Usage: list_characters",
                callback: this.ListAllNPCCharacterData
            );

            /*
            commands.Add(
                "update_template",
                "Checks and appends any missing dialogue entries to the template.\n\n" +
                "Usage: update_template <TemplateFolderName> [wav|ogg]",
                this.UpdateTemplateCommand
            );

            /*/

            /*
            //used to fix current voice packs to add increments to duplicate Dialogue From
            this.Helper.ConsoleCommands.Add(
                 "fix_duplicate_dialoguefrom",
                 "Fix duplicate DialogueFrom keys in all JSON voice packs within the specified folder."
                 + "\nUsage: fix_duplicate_dialoguefrom <FolderName>\n",
                 FixDuplicateDialogueFromCommand);

            */

        }


        
        private void FixDuplicateDialogueFromCommand(string command, string[] args)
        {
            if (args.Length < 1)
            {
                Monitor.Log("Usage: fix_duplicate_dialoguefrom <FolderName>", StardewModdingAPI.LogLevel.Info);
                return;
            }

            string folderName = args[0];
            string folderPath = Path.Combine(Helper.DirectoryPath, folderName);
            if (!Directory.Exists(folderPath))
            {
                Monitor.Log($"Folder not found: {folderPath}", StardewModdingAPI.LogLevel.Error);
                return;
            }

            var jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);
            foreach (var file in jsonFiles)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var packFile = JsonConvert.DeserializeObject<VoicePackFile>(json);
                    bool changed = false;

                    foreach (var pack in packFile?.VoicePacks ?? new List<VoicePackManifestTemplate>())
                    {
                        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < pack.Entries.Count; i++)
                        {
                            var entry = pack.Entries[i];
                            if (string.IsNullOrWhiteSpace(entry.DialogueFrom))
                                continue;

                            string baseKey = entry.DialogueFrom;
                            if (!seen.ContainsKey(baseKey))
                            {
                                seen[baseKey] = 0;
                            }
                            else
                            {
                                seen[baseKey]++;
                                entry.DialogueFrom = $"{baseKey}_{seen[baseKey]}";
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        string updatedJson = JsonConvert.SerializeObject(packFile, Formatting.Indented);
                        File.WriteAllText(file, updatedJson);
                        Monitor.Log($"Updated: {file}", StardewModdingAPI.LogLevel.Info);
                    }
                    else
                    {
                        Monitor.Log($"No duplicates found in: {file}", StardewModdingAPI.LogLevel.Trace);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Failed to process {file}: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
                }
            }

            Monitor.Log("Duplicate DialogueFrom fix completed.", StardewModdingAPI.LogLevel.Info);
        }
        


        private void GenerateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 5)
            {
                this.Monitor.Log("Invalid arguments. Use 'help create_template' for details.", LogLevel.Error);
                this.Monitor.Log("Usage: create_template <CharacterName|all|*match|!*exclude> <LanguageCode|all> <YourPackID> <YourPackName> <AudioPathNumber.Wav-StartsAtThisNumber>", LogLevel.Info);
                return;
            }

            if ((args[0].Equals("all", StringComparison.OrdinalIgnoreCase) || args[0].Contains("*")) && !Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before using wildcard or 'all' to access game data.", LogLevel.Warn);
                return;
            }

            string targetCharacterArg = args[0];
            string targetLanguageArg = args[1];
            string baseUniqueModID = args[2].Trim();
            string baseVoicePackName = args[3].Trim();
            int startsAtThisNumber = Convert.ToInt32(args[4]);
            string desiredExtension = "wav"; 

            if (args.Length >= 6)
            {
                string extArg = args[5].Trim().ToLower();
                if (extArg == "ogg" || extArg == "wav")
                    desiredExtension = extArg;
                else
                    this.Monitor.Log($"[create_template] Unknown extension '{extArg}', defaulting to wav.", LogLevel.Warn);
            }


            if (string.IsNullOrWhiteSpace(baseUniqueModID) || string.IsNullOrWhiteSpace(baseVoicePackName))
            {
                this.Monitor.Log("YourPackID and YourPackName cannot be empty.", LogLevel.Error);
                return;
            }

            // --- Determine Languages ---
            List<string> languagesToProcess = new();
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            {
                languagesToProcess.AddRange(this.KnownStardewLanguages);
                if (Config.developerModeOn)
                    this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info);
            }
            else
            {
                string validatedLang = GetValidatedLanguageCode(targetLanguageArg);
                if (!string.IsNullOrWhiteSpace(validatedLang))
                {
                    languagesToProcess.Add(validatedLang);
                    if (Config.developerModeOn)
                        this.Monitor.Log($"Processing for validated language: {validatedLang}", LogLevel.Info);
                }
            }

            if (!languagesToProcess.Any())
            {
                this.Monitor.Log($"No valid languages found from '{targetLanguageArg}'.", LogLevel.Error);
                return;
            }

            // --- Determine Characters ---
            List<string> charactersToProcess;
            try
            {
                var allCharacters = GetAllKnownCharacterNames();

                if (targetCharacterArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetCharacterArg == "*")
                {
                    charactersToProcess = allCharacters;
                }
                else if (targetCharacterArg.Equals("modded", StringComparison.OrdinalIgnoreCase))
                {
                    charactersToProcess = allCharacters
                        .Where(name => !IsKnownVanillaVillager(name))
                        .ToList();
                }
                else if (targetCharacterArg.StartsWith("!*"))
                {
                    string exclude = targetCharacterArg.Substring(2);
                    charactersToProcess = allCharacters
                        .Where(name => !name.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else if (targetCharacterArg.StartsWith("*"))
                {
                    string include = targetCharacterArg.Substring(1);
                    charactersToProcess = allCharacters
                        .Where(name => name.Contains(include, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else
                {
                    charactersToProcess = new List<string> { targetCharacterArg };
                }


                if (Config.developerModeOn)
                    this.Monitor.Log($"[create_template] Found {charactersToProcess.Count} character(s) after filtering.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error getting filtered character list: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                return;
            }

            if (!charactersToProcess.Any() || charactersToProcess.Any(string.IsNullOrWhiteSpace))
            {
                this.Monitor.Log("[create_template] No valid characters specified or found to process.", LogLevel.Error);
                return;
            }


            //Determines if modded character has language set to game language due modded dialoge using i18n
            string currentGameLang = LocalizedContentManager.CurrentLanguageCode.ToString();
            bool includesModdedCharacters = charactersToProcess
                .Any(name => !IsKnownVanillaVillager(name));

            // Compare each target language against current game language
            foreach (string lang in languagesToProcess)
            {
                if (!lang.Equals(currentGameLang, StringComparison.OrdinalIgnoreCase) && includesModdedCharacters)
                {
                    this.Monitor.Log("⚠️  Language Mismatch Warning", LogLevel.Warn);
                    this.Monitor.Log($"    Game Language: {currentGameLang} | Target Language: {lang}", LogLevel.Warn);
                    this.Monitor.Log("    Some modded dialogue may rely on i18n tokens and resolve using the *current game language*.", LogLevel.Warn);
                    this.Monitor.Log("    To ensure correct results, please switch your game language to match your target language before generating templates.", LogLevel.Warn);
                    break; 
                }
            }


            // --- Output Directory ---
            string sanitizedPackName = SanitizeKeyForFileName(baseVoicePackName);
            if (string.IsNullOrWhiteSpace(sanitizedPackName)) sanitizedPackName = "UntitledVoicePack";
            string outputBaseDir = PathUtilities.NormalizePath(Path.Combine(this.Helper.DirectoryPath, $"{sanitizedPackName}_Templates"));
            Directory.CreateDirectory(outputBaseDir);

            this.Monitor.Log($"Template files will be generated in: {outputBaseDir}", LogLevel.Info);

            // --- Run per Language ---
            int totalSuccessCount = 0;
            int totalFailCount = 0;

            foreach (string languageCode in languagesToProcess)
            {
                if (Config.developerModeOn)
                    this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);

                int langSuccessCount = 0;
                int langFailCount = 0;

                foreach (string characterName in charactersToProcess)
                {
                    string instancePackId = $"{baseUniqueModID}.{characterName}.{languageCode}";
                    string instancePackName = $"{baseVoicePackName} ({characterName} - {languageCode})";

                    if (GenerateSingleTemplate(characterName, languageCode, outputBaseDir, instancePackId, instancePackName, startsAtThisNumber, desiredExtension))
                        langSuccessCount++;
                    else
                        langFailCount++;
                }

                if (Config.developerModeOn)
                    this.Monitor.Log($"Language {languageCode} Summary - Generated: {langSuccessCount}, Failed/Skipped: {langFailCount}", langFailCount > 0 ? LogLevel.Warn : LogLevel.Info);

                totalSuccessCount += langSuccessCount;
                totalFailCount += langFailCount;
            }

            // --- Final Summary ---
            this.Monitor.Log($"--- Overall Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Total Successfully generated: {totalSuccessCount}", LogLevel.Info);
            if (totalFailCount > 0)
                this.Monitor.Log($"Total Failed/Skipped: {totalFailCount}", LogLevel.Warn);
            this.Monitor.Log($"Output location: {outputBaseDir}", LogLevel.Info);
        }





        private bool GenerateSingleTemplate(string characterName, string languageCode, string outputBaseDir, string voicePackId, string voicePackName, int startAtThisNumber, string desiredExtension)
        {
            // --- PRE-CHECKS ---
            if (this.Helper == null || this.Monitor == null || this.Config == null)
            {
                this.Monitor?.Log("GenerateSingleTemplate cannot run: Helper, Monitor, or Config is null.", LogLevel.Error);
                return false;
            }

            if (this.Config.developerModeOn)
                this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}). ID: '{voicePackId}', Name: '{voicePackName}' AudioFileStartsAt: {startAtThisNumber}", LogLevel.Debug);

            // Stores DialogueKey -> RawText or EventSourceKey -> Raw (we’ll sanitize later)
            var initialSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Tracks the type of source for each key above (e.g., "Dialogue", "Event:Town/123" or "Festival/spring13/Abigail")
            var sourceTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // NEW: For sources that already have a stable translation key (Festivals / Extra / Gifts / Engagement), track it by processingKey
            var translationKeyTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Tracks uniqueness using a composite key (pattern + page + gender + (event split if any) + source)
            var addedCompositeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // --- 1. Load Dialogue (Characters/Dialogue/{Name}) ---
                string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                string specificDialogueAssetKey = $"Characters/Dialogue/{characterName}{langSuffix}";
                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                    if (dialogueData != null)
                    {
                        foreach (var kvp in dialogueData)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !initialSources.ContainsKey(kvp.Key))
                            {
                                initialSources[kvp.Key] = kvp.Value;                 // RAW text
                                sourceTracking[kvp.Key] = "Dialogue";                // mark source type
                            }
                        }
                        if (this.Config.developerModeOn)
                            this.Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace);
                    }
                }
                catch (ContentLoadException)
                {
                    if (this.Config.developerModeOn)
                        this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found or failed to load.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error processing '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                }

                // --- 2. Load Strings (Strings/Characters) ---
                var stringCharData = this.GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent);
                if (this.Config.developerModeOn)
                    this.Monitor.Log($"Found {stringCharData?.Count ?? 0} potential entries from Strings/Characters.", LogLevel.Trace);

                if (stringCharData != null)
                {
                    foreach (var kvp in stringCharData)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !initialSources.ContainsKey(kvp.Key))
                        {
                            initialSources[kvp.Key] = kvp.Value;                 // RAW text
                            sourceTracking[kvp.Key] = "Strings/Characters";      // mark source type
                        }
                    }
                }

                // --- 3. Load Event Dialogue ---
                var eventDataRaw = this.GetEventDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                if (this.Config.developerModeOn)
                    this.Monitor.Log($"Retrieved {eventDataRaw?.Count ?? 0} potential event dialogue lines.", LogLevel.Trace);

                if (eventDataRaw != null)
                {
                    foreach (var kvp in eventDataRaw) // Key=SourceInfo ("Event:Backwoods/6963327/..."), Value=raw text
                    {
                        if (!initialSources.ContainsKey(kvp.Key))
                        {
                            initialSources[kvp.Key] = kvp.Value;
                            sourceTracking[kvp.Key] = kvp.Key; // keep the full "Event:..." string as the source tag
                        }
                    }
                }

                // --- 4. Load Other Data Files (Festivals, Gifts, etc.) ---
                // Festivals now provide a stable TranslationKey per line. We preserve that here, and do the same for Extra/Gifts/Engagement.

                // Festivals (Dictionary<string,(RawText,SourceInfo,TranslationKey)>)
                try
                {
                    var fest = this.GetFestivalDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                    if (fest != null)
                    {
                        foreach (var kvp in fest) // kvp.Value = (RawText, SourceInfo, TranslationKey)
                        {
                            string key = kvp.Value.SourceInfo ?? $"Festival/{kvp.Key}/{characterName}";
                            string baseKey = key;
                            int collision = 1;
                            while (initialSources.ContainsKey(key))
                                key = $"{baseKey}_{collision++}";

                            initialSources[key] = kvp.Value.RawText;
                            sourceTracking[key] = key; // e.g., "Festival/fall16/Abigail_spouse"
                            if (!string.IsNullOrWhiteSpace(kvp.Value.TranslationKey))
                                translationKeyTracking[key] = kvp.Value.TranslationKey; // e.g., "Data/Festivals/fall16:Abigail_spouse"
                        }
                    }
                }
                catch (Exception ex) { this.Monitor.Log($"Error loading Festival data for {characterName}: {ex.Message}", LogLevel.Trace); }

                // Gifts / Engagement / Extra now return List<(RawText, SourceInfo, TranslationKey)>
                try
                {
                    var gifts = this.GetGiftTasteDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                    if (gifts != null)
                    {
                        foreach (var item in gifts)
                        {
                            string key = item.SourceInfo ?? $"NPCGiftTastes/{characterName}:{Guid.NewGuid()}";
                            string baseKey = key;
                            int collision = 1;
                            while (initialSources.ContainsKey(key))
                                key = $"{baseKey}_{collision++}";

                            initialSources[key] = item.RawText;
                            sourceTracking[key] = item.SourceInfo ?? "NPCGiftTastes";
                            if (!string.IsNullOrWhiteSpace(item.TranslationKey))
                                translationKeyTracking[key] = item.TranslationKey; // "Data/NPCGiftTastes:{Name}:sN"
                        }
                    }
                }
                catch (Exception ex) { this.Monitor.Log($"Error loading GiftTaste data for {characterName}: {ex.Message}", LogLevel.Trace); }

                try
                {
                    var engage = this.GetEngagementDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                    if (engage != null)
                    {
                        foreach (var item in engage)
                        {
                            string key = item.SourceInfo ?? $"EngagementDialogue/{characterName}:{Guid.NewGuid()}";
                            string baseKey = key;
                            int collision = 1;
                            while (initialSources.ContainsKey(key))
                                key = $"{baseKey}_{collision++}";

                            initialSources[key] = item.RawText;
                            sourceTracking[key] = item.SourceInfo ?? "EngagementDialogue";
                            if (!string.IsNullOrWhiteSpace(item.TranslationKey))
                                translationKeyTracking[key] = item.TranslationKey; // "Data/EngagementDialogue:{jsonKey}"
                        }
                    }
                }
                catch (Exception ex) { this.Monitor.Log($"Error loading Engagement data for {characterName}: {ex.Message}", LogLevel.Trace); }

                try
                {
                    var extra = this.GetExtraDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                    if (extra != null)
                    {
                        foreach (var item in extra)
                        {
                            string key = item.SourceInfo ?? $"ExtraDialogue/{characterName}:{Guid.NewGuid()}";
                            string baseKey = key;
                            int collision = 1;
                            while (initialSources.ContainsKey(key))
                                key = $"{baseKey}_{collision++}";

                            initialSources[key] = item.RawText;
                            sourceTracking[key] = item.SourceInfo ?? "ExtraDialogue";
                            if (!string.IsNullOrWhiteSpace(item.TranslationKey))
                                translationKeyTracking[key] = item.TranslationKey; // "Data/ExtraDialogue:{jsonKey}"
                        }
                    }
                }
                catch (Exception ex) { this.Monitor.Log($"Error loading Extra data for {characterName}: {ex.Message}", LogLevel.Trace); }

                // --- 5. Prepare Manifest Object ---
                var characterManifest = new VoicePackManifestTemplate
                {
                    Format = "1.0.0",
                    VoicePackId = voicePackId,
                    VoicePackName = voicePackName,
                    Character = characterName,
                    Language = languageCode,
                    Entries = new List<VoiceEntryTemplate>()
                };

                int entryNumber = startAtThisNumber;

                // --- Speak index tracking for events ---
                // Key = "Events/{Map}:{EventId}" -> next speak index to assign
                var eventSpeakCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // Key = "Events/{Map}:{EventId}" + "|" + processingKey -> assigned speak index
                var eventSpeakAssigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Helper to parse Event:... into "Events/{Map}:{Id}"
                string TryGetEventBaseKey(string sourceTag)
                {
                    // Expected forms like: "Event:Backwoods/6963327/..." or "Event:Town/1234/..."
                    var m = System.Text.RegularExpressions.Regex.Match(sourceTag ?? "", @"^Event:(?<map>[^/]+)/(?<id>\d+)", RegexOptions.CultureInvariant);
                    if (!m.Success) return null;
                    string map = m.Groups["map"].Value.Trim();
                    string id = m.Groups["id"].Value.Trim();
                    return $"Events/{map}:{id}";
                }

                // Helper to get a stable speak index per event *source line* (per processingKey)
                int GetEventSpeakIndex(string baseKey, string processingKey)
                {
                    string assignKey = $"{baseKey}|{processingKey}";
                    if (eventSpeakAssigned.TryGetValue(assignKey, out int idx))
                        return idx;

                    int next = eventSpeakCounters.TryGetValue(baseKey, out int cur) ? cur : 0;
                    eventSpeakCounters[baseKey] = next + 1;
                    eventSpeakAssigned[assignKey] = next;
                    return next;
                }

                // --- 6. Central Processing Loop (V2) ---
                if (this.Config.developerModeOn)
                    this.Monitor.Log($"Processing {initialSources.Count} collected sources...", LogLevel.Trace);

                foreach (var kvp in initialSources
                         .OrderBy(p => sourceTracking.GetValueOrDefault(p.Key, "zzz_Unknown"))
                         .ThenBy(p => p.Key))
                {
                    string processingKey = kvp.Key;              // e.g., "Introduction" or "Event:Backwoods/6963327/f Abigail ... " or "Festival/fall16/Abigail"
                    string rawTextFromSource = kvp.Value;        // RAW (unsanitized) source text
                    string sourceType = sourceTracking.GetValueOrDefault(processingKey, "Unknown");

                    if (this.Config.developerModeOn)
                        Monitor.Log($"Processing Key: '{processingKey}', Raw: '{rawTextFromSource}'", LogLevel.Trace);

                    // 6.0 Handle SplitSpeak branches for events ( '~' delimited )
                    var branchSegments = new List<(string BranchText, int? BranchIndex)>();
                    bool isEventLike = (sourceType?.StartsWith("Event:", StringComparison.OrdinalIgnoreCase) ?? false)
                                       || (processingKey?.StartsWith("Event:", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (isEventLike && !string.IsNullOrEmpty(rawTextFromSource) && rawTextFromSource.Contains("~"))
                    {
                        var parts = rawTextFromSource.Split('~');
                        for (int b = 0; b < parts.Length; b++)
                            branchSegments.Add((parts[b], b));
                    }
                    else
                    {
                        branchSegments.Add((rawTextFromSource, (int?)null));
                    }

                    // Event base key (if applicable), e.g., "Events/Backwoods:6963327"
                    string eventBaseKey = isEventLike ? TryGetEventBaseKey(processingKey) : null;
                    int? eventSpeakIndexForThisProcessingKey = null;
                    if (eventBaseKey != null)
                    {
                        // Assign speak index ONCE per processingKey (not per page/variant)
                        eventSpeakIndexForThisProcessingKey = GetEventSpeakIndex(eventBaseKey, processingKey);
                    }

                    foreach (var (branchRaw, branchIndex) in branchSegments)
                    {
                        // 6.1 Split into pages first (handles '#$b#' as intra-page newline)
                        var pages = SplitStandardDialogueSegmentsV2(branchRaw);

                        for (int pageIdx = 0; pageIdx < pages.Count; pageIdx++)
                        {
                            string page = pages[pageIdx];

                            // 6.2 Gender variants (official ${m^f(^n)?} or fallback m^f)
                            List<(string text, string gender)> variants;
                            if (!TrySplitGenderVariants(page, out variants))
                            {
                                variants = new List<(string, string)> { (page, null) }; // single neutral variant
                            }

                            foreach (var (variantText, gender) in variants)
                            {
                                // 6.3 V2 sanitize to a stable DisplayPattern (keeps placeholders)
                                string pattern = SanitizeDialogueTextV2(variantText);
                                if (string.IsNullOrWhiteSpace(pattern))
                                    continue;

                                // 6.4 Compose uniqueness key (avoid dupes across sources/pages/genders/branches)
                                string uniqueKey = $"{processingKey}|branch{(branchIndex?.ToString() ?? "x")}|p{pageIdx}|g{(gender ?? "")}|{pattern}";
                                if (!addedCompositeKeys.Add(uniqueKey))
                                {
                                    if (this.Config.developerModeOn)
                                        Monitor.Log($"Skipped duplicate composite: {uniqueKey}", LogLevel.Trace);
                                    continue;
                                }

                                // 6.5 Compute translation key
                                string translationKey = null;

                                if (string.Equals(sourceType, "Dialogue", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Full key: "Characters/Dialogue/Abigail:Introduction"
                                    translationKey = $"Characters/Dialogue/{characterName}:{processingKey}";
                                }
                                else if (eventBaseKey != null && eventSpeakIndexForThisProcessingKey.HasValue)
                                {
                                    // Synthetic key for event Speak/SplitSpeak:
                                    //   Events/{Map}:{EventId}:s{SpeakIndex}[:split{Branch}]
                                    translationKey = $"{eventBaseKey}:s{eventSpeakIndexForThisProcessingKey.Value}";
                                    if (branchIndex.HasValue)
                                        translationKey += $":split{branchIndex.Value}";
                                }

                                // NEW: if still null, use any provided TK we captured from Festivals / Gifts / Engagement / Extra
                                if (translationKey == null && translationKeyTracking.TryGetValue(processingKey, out var tkProvided))
                                {
                                    translationKey = tkProvided;
                                }

                                // 6.6 Build audio path; add gender suffix if present
                                string genderSuffix = string.IsNullOrEmpty(gender) ? "" : $"_{gender}";
                                string numberedFileName = $"{entryNumber}{genderSuffix}.{desiredExtension}";
                                string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace('\\', '/');

                                // 6.7 Add entry
                                var newEntry = new VoiceEntryTemplate
                                {
                                    // V1 legacy
                                    DialogueFrom = processingKey,   // keep raw provenance, never suffixed
                                    DialogueText = pattern,

                                    AudioPath = relativeAudioPath,

                                    // V2 fields
                                    TranslationKey = translationKey, // real/synthetic key or null
                                    PageIndex = pageIdx,             // reserved ONLY for '#$e#' page breaks
                                    DisplayPattern = pattern,
                                    GenderVariant = gender           // "male" | "female" | "neutral"/null
                                };

                                characterManifest.Entries.Add(newEntry);
                                if (this.Config.developerModeOn)
                                    Monitor.Log($"ADDED Entry - TK='{translationKey ?? "null"}' Pg={pageIdx} G={(gender ?? "na")} Branch={(branchIndex?.ToString() ?? "na")} Text: '{pattern}' Path: '{relativeAudioPath}' From: '{processingKey}'", LogLevel.Debug);

                                entryNumber++; // increment per entry created
                            }
                        }
                    }
                }

                // --- 7. Save JSON file ---
                if (!characterManifest.Entries.Any())
                {
                    if (this.Config.developerModeOn)
                        this.Monitor.Log($"Skipping save for {characterName} ({languageCode}): No entries after processing.", LogLevel.Info);
                    return false;
                }

                // Keep DialogueFrom untouched (this now only logs in dev mode)
                GenerateTemplate_DialogueFromDeduplicated(characterManifest.Entries);

                var finalOutputFileObject = new VoicePackFile
                {
                    Format = "2.0.0",
                    VoicePacks = { characterManifest }
                };

                string sanitizedCharName = this.SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_");
                string filename = $"{sanitizedCharName}_{languageCode}.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, filename));

                if (this.Config.developerModeOn)
                    this.Monitor.Log($"Saving JSON ({characterManifest.Entries.Count} entries for {characterName}) to: {outputPath}", LogLevel.Debug);

                string jsonOutput = JsonConvert.SerializeObject(finalOutputFileObject, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, jsonOutput);
                    if (!File.Exists(outputPath))
                    {
                        this.Monitor.Log($"ERROR: Failed to verify file existence after writing to: {outputPath}", LogLevel.Error);
                        return false;
                    }
                    this.Monitor.Log($"Success! Saved template JSON ({characterManifest.Entries.Count} entries for {characterName}) to: {outputPath}", LogLevel.Info);
                }
                catch (IOException ioEx) { this.Monitor.Log($"IO ERROR saving JSON to {outputPath}: {ioEx.Message}", LogLevel.Error); return false; }
                catch (UnauthorizedAccessException uaEx) { this.Monitor.Log($"ACCESS DENIED saving JSON to {outputPath}: {uaEx.Message}. Check permissions.", LogLevel.Error); return false; }
                catch (Exception ex) { this.Monitor.Log($"UNEXPECTED ERROR saving JSON to {outputPath}: {ex.GetType().Name} - {ex.Message}", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); return false; }

                // --- 8. Ensure asset folder exists ---
                try
                {
                    string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, "assets", languageCode, characterName));
                    Directory.CreateDirectory(assetsCharacterPath);
                    if (this.Config.developerModeOn)
                        this.Monitor.Log($"Ensured asset directory exists: {assetsCharacterPath}", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Warning: Failed to create asset directory structure for {characterName} ({languageCode}). Error: {ex.Message}", LogLevel.Warn);
                }

                return true;
            }
            catch (Exception ex) // Master catch
            {
                this.Monitor.Log($"FATAL ERROR during GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace);
                return false;
            }
        }




        /* Removing method as we'll start fresh with V2
        // Adds new dialogue entries to an existing character_language.json template
        private void UpdateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 1)
            {
                this.Monitor.Log("Usage: update_template <TemplateFolderName>", LogLevel.Info);
                return;
            }

            string folderName = args[0];
            string desiredExtension = "wav"; // default


            if (args.Length >= 2)
            {
                string extArg = args[1].Trim().ToLower();
                if (extArg == "ogg" || extArg == "wav")
                    desiredExtension = extArg;
                else
                    this.Monitor.Log($"[update_template] Unknown extension '{extArg}', defaulting to wav.", LogLevel.Warn);
            }

            string templateFolderPath = Path.Combine(this.Helper.DirectoryPath, folderName);

            if (!Directory.Exists(templateFolderPath))
            {
                this.Monitor.Log($"Directory not found: {templateFolderPath}", LogLevel.Error);
                return;
            }

            var jsonFiles = Directory.GetFiles(templateFolderPath, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var jsonFilePath in jsonFiles)
            {
                VoicePackFile existingPack = null;
                try
                {
                    string json = File.ReadAllText(jsonFilePath);
                    existingPack = JsonConvert.DeserializeObject<VoicePackFile>(json);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Failed to load existing template: {jsonFilePath}: {ex.Message}", LogLevel.Warn);
                    continue;
                }

                if (existingPack?.VoicePacks == null || !existingPack.VoicePacks.Any())
                    continue;

                var voiceManifest = existingPack.VoicePacks.First();
                string character = voiceManifest.Character;
                string language = voiceManifest.Language;

                var existingEntries = new HashSet<string>(voiceManifest.Entries.Select(e => e.DialogueText), StringComparer.OrdinalIgnoreCase);
                int nextAudioNumber = voiceManifest.Entries
                    .Select(e => Path.GetFileNameWithoutExtension(e.AudioPath))
                    .Where(name => int.TryParse(name.Split('_')[0], out _))
                    .Select(name => int.Parse(name.Split('_')[0]))
                    .DefaultIfEmpty(0)
                    .Max() + 1;

                var initialSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var sourceTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Collect dialogue from the same sources as GenerateSingleTemplate
                string langSuffix = language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{language}";
                string specificDialogueAssetKey = $"Characters/Dialogue/{character}{langSuffix}";
                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                    foreach (var kvp in dialogueData)
                    {
                        if (!initialSources.ContainsKey(kvp.Key))
                        {
                            initialSources[kvp.Key] = kvp.Value;
                            sourceTracking[kvp.Key] = "Dialogue";
                        }
                    }
                }
                catch { }

                var stringCharData = this.GetVanillaCharacterStringKeys(character, language, this.Helper.GameContent);
                foreach (var kvp in stringCharData)
                {
                    if (!initialSources.ContainsKey(kvp.Key))
                    {
                        initialSources[kvp.Key] = kvp.Value;
                        sourceTracking[kvp.Key] = "Strings/Characters";
                    }
                }

                var eventData = this.GetEventDialogueForCharacter(character, language, this.Helper.GameContent);
                foreach (var kvp in eventData)
                {
                    if (!initialSources.ContainsKey(kvp.Key))
                    {
                        initialSources[kvp.Key] = kvp.Value;
                        sourceTracking[kvp.Key] = kvp.Key;
                    }
                }

                void AddExtraData(List<(string RawText, string SourceInfo)> sourceList)
                {
                    for (int i = 0; i < sourceList.Count; i++)
                    {
                        string key = $"{sourceList[i].SourceInfo}:{i}";
                        if (!initialSources.ContainsKey(key))
                        {
                            initialSources[key] = sourceList[i].RawText;
                            sourceTracking[key] = sourceList[i].SourceInfo;
                        }
                    }
                }

                AddExtraData(GetFestivalDialogueForCharacter(character, language, this.Helper.GameContent).Values.ToList());
                AddExtraData(GetGiftTasteDialogueForCharacter(character, language, this.Helper.GameContent));
                AddExtraData(GetEngagementDialogueForCharacter(character, language, this.Helper.GameContent));
                AddExtraData(GetExtraDialogueForCharacter(character, language, this.Helper.GameContent));

                int addedCount = 0;

                foreach (var kvp in initialSources.OrderBy(p => sourceTracking.GetValueOrDefault(p.Key, "zzz_Unknown")).ThenBy(p => p.Key))
                {
                    IEnumerable<string> segments = SplitStandardDialogueSegments(kvp.Value);
                    foreach (string seg in segments)
                    {
                        string sanitized = SanitizeDialogueText(seg);
                        string cleaned = Regex.Replace(sanitized, "#.+?#", "").Trim();
                        var parts = cleaned.Contains("^") ? cleaned.Split('^') : new[] { cleaned };

                        foreach (var (text, suffix) in parts.Length == 2
                            ? new[] { (parts[0].Trim(), "_male"), (parts[1].Trim(), "_female") }
                            : new[] { (parts[0].Trim(), "") })
                        {
                            if (string.IsNullOrWhiteSpace(text) || existingEntries.Contains(text))
                                continue;

                            //string audioPath = Path.Combine("assets", language, character, $"{nextAudioNumber}{suffix}.wav").Replace('\\', '/');
                            string audioPath = Path.Combine("assets", language, character, $"{nextAudioNumber}{suffix}.{desiredExtension}").Replace('\\', '/');

                            voiceManifest.Entries.Add(new VoiceEntryTemplate
                            {
                                DialogueFrom = kvp.Key,
                                DialogueText = text,
                                AudioPath = audioPath
                            });

                            existingEntries.Add(text);
                            nextAudioNumber++;
                            addedCount++;
                        }
                    }
                }

                if (addedCount > 0)
                {
                    GenerateTemplate_DialogueFromDeduplicated(voiceManifest.Entries);
                    File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(existingPack, Formatting.Indented));
                    this.Monitor.Log($"Updated '{Path.GetFileName(jsonFilePath)}' with {addedCount} new lines.", LogLevel.Info);
                }
                else
                {
                    this.Monitor.Log($"No new lines found for '{Path.GetFileName(jsonFilePath)}'.", LogLevel.Debug);
                }
            }
        }
        */


        //used to fix multi-dialogue page that used to force suffixes for duplicate DialogueFrom.
        //Do not mutate DialogueFrom at all. It’s provenance, not a unique key.
        private void GenerateTemplate_DialogueFromDeduplicated(List<VoiceEntryTemplate> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            // We purposely leave DialogueFrom unchanged. If you want to see if a pack
            // has many identical DialogueFrom values, we can log it in developer mode.
            if (Config.developerModeOn)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    var k = e.DialogueFrom ?? "Unknown";
                    counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
                }

                foreach (var kv in counts.Where(p => p.Value > 1))
                    Monitor.Log($"[Info] DialogueFrom '{kv.Key}' appears {kv.Value} times (expected for multi-page/variants). Not renaming.", LogLevel.Trace);
            }

            
        }





    }

}