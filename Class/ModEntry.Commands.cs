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

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private void SetupConsoleCommands(ICommandHelper commands)
        {
            //this.Monitor.Log("Setting up console commands...", LogLevel.Debug);

            commands.Add(
                name: "create_template",
                documentation: "Generates template JSON voice files for characters.\n\n" +
                               "Usage: create <CharacterName|all> <LanguageCode|all> <YourPackID> <YourPackName>\n" +
                               "  - CharacterName: Specific NPC name (e.g., Abigail) or 'all'.\n" +
                               "  - LanguageCode: Specific code (en, es-ES, etc.) or 'all'.\n" +
                               "  - YourPackID: Base unique ID for your pack (e.g., YourName.FancyVoices).\n" +
                               "  - YourPackName: Display name for your pack (e.g., Fancy Voices).\n\n" +
                               "Example: create_template Abigail en MyName.AbigailVoice Abigail English Voice\n" +
                               "Example: create_template all en MyName.AllVanillaVoices All Vanilla (EN)\n" +
                               "Output files will be in 'Mods/VoiceOverFrameworkMod/YourPackName_Templates'.",
                callback: this.GenerateTemplateCommand
            );

   

            //this.Monitor.Log("Console commands registered.", LogLevel.Debug);
        }


        private void GenerateTemplateCommand(string command, string[] args)
        {

            if (args.Length < 5)
            {
                this.Monitor.Log("Invalid arguments. Use 'help vf_create_template' for details.", LogLevel.Error);

                this.Monitor.Log("Usage: create_template <CharacterName|all> <LanguageCode|all> <YourPackID> <YourPackName> <AudioPathNumber.Wav-StartsAtThisNumber>", LogLevel.Info);
                return;
            }


            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase) && !Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before using 'all' characters to access game data.", LogLevel.Warn);
                return;
            }

            
            string targetCharacterArg = args[0];
            string targetLanguageArg = args[1];
            string baseUniqueModID = args[2].Trim(); 
            string baseVoicePackName = args[3].Trim(); 
            int startsAtThisNumber = Convert.ToInt32(args[4]);

            // Validate base ID and Name aren't empty
            if (string.IsNullOrWhiteSpace(baseUniqueModID) || string.IsNullOrWhiteSpace(baseVoicePackName))
            {
                this.Monitor.Log("YourPackID and YourPackName cannot be empty.", LogLevel.Error);
                return;
            }


            // --- Determine Languages ---
            List<string> languagesToProcess = new List<string>();
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            {
                languagesToProcess.AddRange(this.KnownStardewLanguages); // Assumes KnownStardewLanguages exists in ModEntry.cs (Core)

                if (Config.developerModeOn)
                {
                    this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info);
                }

            }
            else
            {
                string validatedLang = GetValidatedLanguageCode(targetLanguageArg); // Use utility method
                if (!string.IsNullOrWhiteSpace(validatedLang))
                {
                    languagesToProcess.Add(validatedLang);
                    if (Config.developerModeOn)
                    {
                        this.Monitor.Log($"Processing for validated language: {validatedLang} (requested: '{targetLanguageArg}')", LogLevel.Info);
                    }
                   
                }
            }

            if (!languagesToProcess.Any())
            {
                if (Config.developerModeOn)
                {
                    this.Monitor.Log($"No valid languages determined from input '{targetLanguageArg}'. Known languages: {string.Join(", ", KnownStardewLanguages)}", LogLevel.Error);
                }
               
                return;
            }

            // --- Determine Characters ---
            List<string> charactersToProcess = new List<string>();
            if (targetCharacterArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetCharacterArg == "*")
            {
                // Ensure world is ready checked earlier
                this.Monitor.Log("Gathering list of known vanilla villagers from Game1.characterData...", LogLevel.Info);
                try
                {
                    // Use Game1.characterData for a more dynamic list if available
                    if (Game1.characterData != null && Game1.characterData.Any())
                    {
                        // Filter using IsKnownVanillaVillager utility method for consistency, but could be expanded
                        charactersToProcess = Game1.characterData.Keys
                                                .Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name)) // Use utility
                                                .OrderBy(name => name)
                                                .ToList();
                        if (Config.developerModeOn)
                        {
                        this.Monitor.Log($"Found {charactersToProcess.Count} known vanilla characters to process: {string.Join(", ", charactersToProcess)}", LogLevel.Info);
                        }

                    }
                    else
                    {
                        if (Config.developerModeOn)
                        {
                        this.Monitor.Log("Game1.characterData is null or empty even though save is loaded. Cannot process 'all'.", LogLevel.Error);
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (Config.developerModeOn)
                    {


                        this.Monitor.Log($"Error retrieving character list from Game1.characterData: {ex.Message}", LogLevel.Error);
                        this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                    }
                    return;
                }
            }
            else
            {
                // Allow processing any specified name (might be mod character)
                charactersToProcess.Add(targetCharacterArg);
                if (Config.developerModeOn)
                {
                    this.Monitor.Log($"Processing for specified character: {targetCharacterArg}", LogLevel.Info);
                }

            }

            if (!charactersToProcess.Any() || charactersToProcess.Any(string.IsNullOrWhiteSpace))
            {
                if (Config.developerModeOn)
                {
                    this.Monitor.Log("No valid characters specified or found to process.", LogLevel.Error);
                }
             
                return;
            }

            // --- Setup Output Directory ---
            // Create a subdirectory based on the provided Pack Name to avoid clutter
            string sanitizedPackName = SanitizeKeyForFileName(baseVoicePackName); // Use utility
            if (string.IsNullOrWhiteSpace(sanitizedPackName)) sanitizedPackName = "UntitledVoicePack";
            string outputBaseDir = PathUtilities.NormalizePath(Path.Combine(this.Helper.DirectoryPath, $"{sanitizedPackName}_Templates"));
            Directory.CreateDirectory(outputBaseDir); // Ensure base output dir exists


            this.Monitor.Log($"Template files will be generated in: {outputBaseDir}", LogLevel.Info);


            // --- Execution Loop ---
            int totalSuccessCount = 0;
            int totalFailCount = 0;

            foreach (string languageCode in languagesToProcess)
            {
                if (Config.developerModeOn)
                {
                    this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                }
              
                int langSuccessCount = 0;
                int langFailCount = 0;
                foreach (string characterName in charactersToProcess)
                {
                    // Generate names and IDs specific to this character/language instance
                    string instancePackId = $"{baseUniqueModID}.{characterName}.{languageCode}";
                    string instancePackName = $"{baseVoicePackName} ({characterName} - {languageCode})";

                    // Call the worker function. It handles saving to the correct subpath.
                    if (GenerateSingleTemplate(characterName, languageCode, outputBaseDir, instancePackId, instancePackName, startsAtThisNumber))
                    {
                        langSuccessCount++;
                    }
                    else
                    {
                        langFailCount++;
                    }
                }
                if (Config.developerModeOn)
                {
                    this.Monitor.Log($"Language {languageCode} Summary - Generated: {langSuccessCount}, Failed/Skipped: {langFailCount}", langFailCount > 0 ? LogLevel.Warn : LogLevel.Info);
                }

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




        private bool GenerateSingleTemplate(string characterName, string languageCode, string outputBaseDir, string voicePackId, string voicePackName, int startAtThisNumber)
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
                                string numberedFileName = $"{entryNumber}{genderSuffix}.wav"; // e.g., "65_male.wav", "66_female.wav", "67.wav"
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

    }
}