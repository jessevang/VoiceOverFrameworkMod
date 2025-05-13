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

            commands.Add(
                "update_template",
                "Checks and appends any missing dialogue entries to the template.\n\n" +
                "Usage: update_template <TemplateFolderName> [wav|ogg]",
                this.UpdateTemplateCommand
            );




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
            string desiredExtension = "wav"; // default

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

            // --- METHOD START ---
            if (this.Config.developerModeOn)
            {
                this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}). ID: '{voicePackId}', Name: '{voicePackName}' AudioFileStartsAt: {startAtThisNumber}", LogLevel.Debug);
            }

            // Stores DialogueKey -> RawText or EventSourceKey -> SanitizedText (initially)
            var initialSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Tracks the type of source for each key above (e.g., "Dialogue", "Event:Town/123")
            var sourceTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Tracks unique FINAL cleaned text segments added to prevent duplicates in the output.
            var addedSanitizedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


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
                                initialSources[kvp.Key] = kvp.Value; // Store RAW text
                                sourceTracking[kvp.Key] = "Dialogue"; // Track source type
                            }
                        }
                        if (this.Config.developerModeOn) { this.Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace); }
                    }
                }
                catch (ContentLoadException) { if (this.Config.developerModeOn) this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found or failed to load.", LogLevel.Trace); }
                catch (Exception ex) { this.Monitor.Log($"Error processing '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }

                
                // --- 2. Load Strings (Strings/Characters) ---
                var stringCharData = this.GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent);
                if (this.Config.developerModeOn) { this.Monitor.Log($"Found {stringCharData?.Count ?? 0} potential entries from Strings/Characters.", LogLevel.Trace); }
                if (stringCharData != null)
                {
                    foreach (var kvp in stringCharData)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !initialSources.ContainsKey(kvp.Key))
                        {
                            initialSources[kvp.Key] = kvp.Value; // Store RAW text
                            sourceTracking[kvp.Key] = "Strings/Characters"; // Track source type
                        }
                    }
                }
                
                // --- 3. Load Event Dialogue (Using updated GetEventDialogueForCharacter) ---
                // This getter should return Dictionary<EventSourceInfo, SanitizedText>
                var eventDataSanitized = this.GetEventDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                if (this.Config.developerModeOn) { this.Monitor.Log($"Retrieved {eventDataSanitized?.Count ?? 0} potential event dialogue lines (sanitized by getter).", LogLevel.Trace); }
                if (eventDataSanitized != null)
                {
                    foreach (var kvp in eventDataSanitized) // Key=SourceInfo, Value=SanitizedText
                    {
                        if (!initialSources.ContainsKey(kvp.Key)) // Avoid overriding if somehow key collision happens (unlikely)
                        {
                            initialSources[kvp.Key] = kvp.Value; // Store PRE-SANITIZED text from getter
                            sourceTracking[kvp.Key] = kvp.Key; // Track source type using the key itself (e.g., "Event:Town/123")
                        }
                    }
                }

                // --- 4. Load Other Data Files (Festivals, Gifts, etc.) ---
                var additionalDialogueSources = new List<(string RawText, string SourceInfo)>();
                try { additionalDialogueSources.AddRange(this.GetFestivalDialogueForCharacter(characterName, languageCode, this.Helper.GameContent)?.Select(kvp => (kvp.Value.RawText, kvp.Value.SourceInfo)) ?? Enumerable.Empty<(string, string)>()); } catch (Exception ex) { this.Monitor.Log($"Error loading Festival data for {characterName}: {ex.Message}", LogLevel.Trace); }
                try { additionalDialogueSources.AddRange(this.GetGiftTasteDialogueForCharacter(characterName, languageCode, this.Helper.GameContent) ?? Enumerable.Empty<(string, string)>()); } catch (Exception ex) { this.Monitor.Log($"Error loading GiftTaste data for {characterName}: {ex.Message}", LogLevel.Trace); }
                try { additionalDialogueSources.AddRange(this.GetEngagementDialogueForCharacter(characterName, languageCode, this.Helper.GameContent) ?? Enumerable.Empty<(string, string)>()); } catch (Exception ex) { this.Monitor.Log($"Error loading Engagement data for {characterName}: {ex.Message}", LogLevel.Trace); }
                try { additionalDialogueSources.AddRange(this.GetExtraDialogueForCharacter(characterName, languageCode, this.Helper.GameContent) ?? Enumerable.Empty<(string, string)>()); } catch (Exception ex) { this.Monitor.Log($"Error loading Extra data for {characterName}: {ex.Message}", LogLevel.Trace); }

                // Add these to the initialSources dictionary for unified processing
                foreach (var item in additionalDialogueSources)
                {
                    // Use a unique key based on source info + a counter if needed, though less likely for these sources.
                    string key = item.SourceInfo ?? $"UnknownData_{Guid.NewGuid()}"; // Generate a fallback key
                    string baseKey = key;
                    int collisionCounter = 1;
                    while (initialSources.ContainsKey(key))
                    {
                        key = $"{baseKey}_{collisionCounter++}";
                    }
                    initialSources[key] = item.RawText; // Store RAW text
                    sourceTracking[key] = item.SourceInfo ?? "UnknownDataFile"; // Track source type
                }


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

                // --- 6. Central Processing Loop ---
                if (this.Config.developerModeOn) { this.Monitor.Log($"Processing {initialSources.Count} collected sources...", LogLevel.Trace); }

                // Order by source type then key for consistent output order
                foreach (var kvp in initialSources.OrderBy(p => sourceTracking.GetValueOrDefault(p.Key, "zzz_Unknown")).ThenBy(p => p.Key))
                {
                    string processingKey = kvp.Key;
                    // *** CRITICAL ASSUMPTION: kvp.Value contains RAW text, including '^' if present ***
                    // *** If GetEventDialogueForCharacter pre-sanitizes and removes '^', this won't work! ***
                    string rawTextFromSource = kvp.Value;
                    string sourceType = sourceTracking.GetValueOrDefault(processingKey, "Unknown");

                    if (this.Config.developerModeOn) Monitor.Log($"Processing Key: '{processingKey}', Raw: '{rawTextFromSource}'", LogLevel.Trace);

                    // Step 6.1: Split by STANDARD delimiters (##, #$b#, #$e#) FIRST
                    IEnumerable<string> standardSegments = this.SplitStandardDialogueSegments(rawTextFromSource);

                    foreach (string segment in standardSegments)
                    {
                        // Step 6.2: Sanitize standard game codes (NOW PRESERVING '^')
                        string sanitizedSegment = this.SanitizeDialogueText(segment); // Uses modified sanitizer

                        // Step 6.3: Remove text between single # symbols (non-greedy)
                        string cleanedSegment = Regex.Replace(sanitizedSegment, @"#.+?#", "").Trim();

                        // Step 6.4: *** NEW: Split the cleaned segment by '^' ***
                        var finalPartsData = new List<(string text, string genderSuffix)>();

                        if (cleanedSegment.Contains("^"))
                        {
                            var genderParts = cleanedSegment.Split('^'); // Split on all occurrences
                            if (genderParts.Length >= 2) // Check if split actually occurred meaningfully
                            {
                                // Assume first part is male, second is female for standard SV format
                                // We only take the first two parts if more splits occurred strangely.
                                string malePart = genderParts[0].Trim();
                                string femalePart = genderParts[1].Trim();

                                if (!string.IsNullOrEmpty(malePart))
                                    finalPartsData.Add((text: malePart, genderSuffix: "_male"));
                                if (!string.IsNullOrEmpty(femalePart))
                                    finalPartsData.Add((text: femalePart, genderSuffix: "_female"));

                                if (this.Config.developerModeOn) Monitor.Log($"Split segment by '^': M='{malePart}', F='{femalePart}' (From Cleaned: '{cleanedSegment}')", LogLevel.Trace);
                            }
                            else
                            {
                                // '^' present but split failed (e.g., "^Text", "Text^") - treat as single line
                                string singlePart = cleanedSegment.Replace("^", "").Trim(); // Remove the caret just in case
                                if (!string.IsNullOrEmpty(singlePart))
                                    finalPartsData.Add((text: singlePart, genderSuffix: ""));
                                if (this.Config.developerModeOn) Monitor.Log($"Contained '^' but split failed or resulted in <2 parts. Treating as single: '{singlePart}' (From Cleaned: '{cleanedSegment}')", LogLevel.Trace);
                            }
                        }
                        else
                        {
                            // No caret found, add the cleaned segment as is
                            if (!string.IsNullOrEmpty(cleanedSegment))
                                finalPartsData.Add((text: cleanedSegment, genderSuffix: ""));
                        }


                        // Step 6.5: Add each final part to the manifest if valid and unique
                        foreach (var partData in finalPartsData)
                        {
                            string finalCleanedPart = partData.text; // This is the final text for the JSON entry
                            string genderSuffix = partData.genderSuffix;

                            // Check uniqueness based on the FINAL text string
                            if (!string.IsNullOrWhiteSpace(finalCleanedPart) && addedSanitizedTexts.Add(finalCleanedPart))
                            {
                                // Construct filename WITH gender suffix if applicable
                                string numberedFileName = $"{entryNumber}{genderSuffix}.{desiredExtension}"; // e.g., "65_male.wav", "66_female.wav", "67.wav" or now 55.ogg
                                string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace('\\', '/');

                                var newEntry = new VoiceEntryTemplate
                                {
                                    DialogueFrom = processingKey, // Link back to the original source key
                                    DialogueText = finalCleanedPart, // The final, unique text for this specific entry (male/female/neutral)
                                    AudioPath = relativeAudioPath
                                };
                                characterManifest.Entries.Add(newEntry);
                                if (this.Config.developerModeOn) Monitor.Log($"ADDED Entry - Text: '{newEntry.DialogueText}', Path: '{newEntry.AudioPath}', SourceKey: '{processingKey}'", LogLevel.Debug);
                                entryNumber++; // Increment for EACH valid file/entry
                            }
                            else if (!string.IsNullOrWhiteSpace(finalCleanedPart) && this.Config.developerModeOn)
                            {
                                Monitor.Log($"Skipped duplicate final text: '{finalCleanedPart}' (SourceKey: {processingKey}, Suffix: {genderSuffix})", LogLevel.Trace);
                            }
                        }
                    }
                }


                // --- 7. Prepare Final Output Object and Save JSON File ---
                if (!characterManifest.Entries.Any())
                {
                    if (this.Config.developerModeOn) this.Monitor.Log($"Skipping save for {characterName} ({languageCode}): No unique, non-empty entries found after processing.", LogLevel.Info);
                    return false; // Indicate nothing was generated
                }

                var finalOutputFileObject = new VoicePackFile();
                finalOutputFileObject.VoicePacks.Add(characterManifest);

                string sanitizedCharName = this.SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_");
                string filename = $"{sanitizedCharName}_{languageCode}.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, filename));

                if (this.Config.developerModeOn) { this.Monitor.Log($"Attempting to serialize and save JSON ({characterManifest.Entries.Count} entries for {characterName}) to: {outputPath}", LogLevel.Debug); }

                string jsonOutput = JsonConvert.SerializeObject(finalOutputFileObject, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)); // Ensure directory exists
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

                // --- 8. Create Asset Folder Structure ---
                try
                {
                    string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, "assets", languageCode, characterName));
                    Directory.CreateDirectory(assetsCharacterPath);
                    if (this.Config.developerModeOn) { this.Monitor.Log($"Ensured asset directory exists: {assetsCharacterPath}", LogLevel.Trace); }
                }
                catch (Exception ex) { this.Monitor.Log($"Warning: Failed to create asset directory structure for {characterName} ({languageCode}). Path: {Path.Combine(outputBaseDir, "assets", languageCode, characterName)}. Error: {ex.Message}", LogLevel.Warn); }

                return true; // Success
            }
            catch (Exception ex) // Master catch block
            {
                this.Monitor.Log($"FATAL ERROR during GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace);
                return false; // Failure
            }
        }



        // Adds new dialogue entries to an existing character_language.json template
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
                    File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(existingPack, Formatting.Indented));
                    this.Monitor.Log($"Updated '{Path.GetFileName(jsonFilePath)}' with {addedCount} new lines.", LogLevel.Info);
                }
                else
                {
                    this.Monitor.Log($"No new lines found for '{Path.GetFileName(jsonFilePath)}'.", LogLevel.Debug);
                }
            }
        }


    }
}