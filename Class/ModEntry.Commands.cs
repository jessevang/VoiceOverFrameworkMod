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
            this.Monitor.Log("Setting up console commands...", LogLevel.Debug);

            commands.Add(
                name: "create_template",
                documentation: "Generates template JSON voice files for characters.\n\n" +
                               "Usage: vfcreate <CharacterName|all> <LanguageCode|all> <YourPackID> <YourPackName>\n" +
                               "  - CharacterName: Specific NPC name (e.g., Abigail) or 'all'.\n" +
                               "  - LanguageCode: Specific code (en, es-ES, etc.) or 'all'.\n" +
                               "  - YourPackID: Base unique ID for your pack (e.g., YourName.FancyVoices).\n" +
                               "  - YourPackName: Display name for your pack (e.g., Fancy Voices).\n\n" +
                               "Example: create_template Abigail en MyName.AbigailVoice Abigail English Voice\n" +
                               "Example: create_template all en MyName.AllVanillaVoices All Vanilla (EN)\n" +
                               "Output files will be in 'Mods/VoiceOverFrameworkMod/YourPackName_Templates'.",
                callback: this.GenerateTemplateCommand
            );

   

            this.Monitor.Log("Console commands registered.", LogLevel.Debug);
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


        /// <summary>
        /// Generates a single voice pack template JSON file for a specific character and language,
        /// pulling dialogue from Dialogue files, Strings files, Events, and Festivals.
        /// </summary>
        private bool GenerateSingleTemplate(string characterName, string languageCode, string outputBaseDir, string voicePackId, string voicePackName, int startAtThisNumber)
        {
            // Basic Logging
            // **CHECK:** Does 'Config' exist and have 'developerModeOn'? Is it initialized?
            if (this.Config.developerModeOn) // Using 'this.' for clarity
            {
                this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}). ID: '{voicePackId}', Name: '{voicePackName}' AudioFileStartsAt: {startAtThisNumber}", LogLevel.Debug);
            }

            // Data Structures
            var discoveredKeyTextPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try // Master try-catch for the whole process
            {
                // 1. Load Dialogue from Characters/Dialogue/...
                string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                string specificDialogueAssetKey = $"Characters/Dialogue/{characterName}{langSuffix}";
                try // try-catch for specific asset load
                {
                    IAssetName dialogueAssetName = this.Helper.GameContent.ParseAssetName(specificDialogueAssetKey); // Use IAssetName
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(dialogueAssetName); // Use IAssetName

                    if (dialogueData != null)
                    {
                        foreach (var kvp in dialogueData)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key))
                            {
                                discoveredKeyTextPairs[kvp.Key] = kvp.Value;
                                sourceTracking[kvp.Key] = "Dialogue";
                            }
                        }
                        if (this.Config.developerModeOn) { this.Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace); }
                    }
                }
                catch (ContentLoadException)
                {
                    this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found.", LogLevel.Trace); // Log asset not found
                }
                catch (Exception ex) // Catch other potential errors during loading/processing this asset
                {
                    this.Monitor.Log($"Error loading/processing '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace); // Log stack trace for debugging
                }

                // 2. Load Dialogue from Strings/Characters/...
                // **CHECK:** Does GetVanillaCharacterStringKeys exist with the correct signature and handle errors?
                var stringCharData = this.GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent); // Using 'this.'
                if (this.Config.developerModeOn) { this.Monitor.Log($"Found {stringCharData.Count} potential entries from Strings/Characters for '{characterName}' ({languageCode}).", LogLevel.Trace); }
                foreach (var kvp in stringCharData) // Assuming stringCharData is never null (method should return empty dict if error)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key))
                    {
                        discoveredKeyTextPairs[kvp.Key] = kvp.Value;
                        sourceTracking[kvp.Key] = "Strings/Characters";
                    }
                }

                // 3. Load Dialogue from Events
                // **CHECK:** Does GetEventDialogueForCharacter exist with the correct signature and handle errors?
                var eventDialogue = this.GetEventDialogueForCharacter(characterName, languageCode, this.Helper.GameContent); // Using 'this.'
                if (this.Config.developerModeOn) { this.Monitor.Log($"Merging {eventDialogue.Count} unique dialogue lines found in events.", LogLevel.Trace); }

                var uniqueSanitizedEventTextsAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in eventDialogue) // Assuming eventDialogue is never null
                {
                    string sanitizedEventText = kvp.Key;
                    string eventSourceInfo = kvp.Value;

                    // **CHECK:** Does SanitizeDialogueText exist?
                    bool alreadyExistsInStandardDialogue = discoveredKeyTextPairs
                        .Where(pair => !sourceTracking[pair.Key].StartsWith("Event:", StringComparison.OrdinalIgnoreCase))
                        .Any(pair => this.SanitizeDialogueText(pair.Value).Equals(sanitizedEventText, StringComparison.OrdinalIgnoreCase)); // Using 'this.'

                    if (!alreadyExistsInStandardDialogue && uniqueSanitizedEventTextsAdded.Add(sanitizedEventText))
                    {
                        discoveredKeyTextPairs[sanitizedEventText] = sanitizedEventText;
                        sourceTracking[sanitizedEventText] = eventSourceInfo;
                    }
                }

                // 4. Prepare Manifest, Entry Counter, and Duplicate Tracking
                // **CHECK:** Does VoicePackManifestTemplate exist and have these properties?
                var characterManifest = new VoicePackManifestTemplate
                {
                    Format = "1.0.0", // Ensure this matches your template format version
                    VoicePackId = voicePackId,
                    VoicePackName = voicePackName,
                    Character = characterName,
                    Language = languageCode,
                    Entries = new List<VoiceEntryTemplate>() // **CHECK:** Does VoiceEntryTemplate exist?
                };

                int entryNumber = startAtThisNumber;
                var addedSanitizedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 5. Process Dialogue, Strings, and Unique Event Lines from discoveredKeyTextPairs
                if (this.Config.developerModeOn) { this.Monitor.Log($"Processing {discoveredKeyTextPairs.Count} unique keys/texts from Dialogue/Strings/Events.", LogLevel.Trace); }

                foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key)) // OrderBy requires System.Linq
                {
                    string processingKey = kvp.Key;
                    string rawValueToProcess = kvp.Value;
                    string source = sourceTracking.TryGetValue(processingKey, out var src) ? src : "Unknown"; // C# 7.0+ feature (out var)

                    string[] splitSegments;
                    if (source.StartsWith("Event:", StringComparison.OrdinalIgnoreCase))
                    {
                        splitSegments = new[] { rawValueToProcess };
                    }
                    else
                    {
                        // Regex.Split requires System.Text.RegularExpressions
                        splitSegments = Regex.Split(rawValueToProcess, @"(?:##|#\$e#|#\$b#)");
                    }

                    for (int i = 0; i < splitSegments.Length; i++)
                    {
                        string rawPart = splitSegments[i];
                        // **CHECK:** Does SanitizeDialogueText exist?
                        string cleanedPart = this.SanitizeDialogueText(rawPart); // Using 'this.'

                        if (!string.IsNullOrWhiteSpace(cleanedPart))
                        {
                            if (addedSanitizedTexts.Add(cleanedPart)) // HashSet.Add requires System.Collections.Generic
                            {
                                string numberedFileName = $"{entryNumber}.wav";
                                // Path.Combine requires System.IO
                                string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace('\\', '/'); // Normalize path separators

                                // **CHECK:** Does VoiceEntryTemplate exist with these properties?
                                var newEntry = new VoiceEntryTemplate
                                {
                                    DialogueFrom = source,
                                    DialogueText = cleanedPart,
                                    AudioPath = relativeAudioPath
                                };
                                characterManifest.Entries.Add(newEntry);
                                entryNumber++;
                            }
                        }
                    }
                }

                // 6. Load and Process Festival Dialogue
                // **CHECK:** Does GetFestivalDialogueForCharacter exist with the correct signature and handle errors?
                var festivalDialogue = this.GetFestivalDialogueForCharacter(characterName, languageCode, this.Helper.GameContent); // Using 'this.'
                if (this.Config.developerModeOn) { this.Monitor.Log($"Processing {festivalDialogue.Count} potential dialogue lines found in Festivals.", LogLevel.Trace); }

                foreach (var kvp in festivalDialogue.OrderBy(p => p.Key)) // Assuming festivalDialogue is never null
                {
                    string rawFestivalText = kvp.Value.RawText; // Requires C# 7.0+ for tuple access kvp.Value.RawText
                    string festivalSourceInfo = kvp.Value.SourceInfo;

                    string[] splitSegments = Regex.Split(rawFestivalText, @"(?:##|#\$e#|#\$b#)");

                    for (int i = 0; i < splitSegments.Length; i++)
                    {
                        string rawPart = splitSegments[i];
                        // **CHECK:** Does SanitizeDialogueText exist?
                        string cleanedPart = this.SanitizeDialogueText(rawPart); // Using 'this.'

                        if (!string.IsNullOrWhiteSpace(cleanedPart))
                        {
                            if (addedSanitizedTexts.Add(cleanedPart))
                            {
                                string numberedFileName = $"{entryNumber}.wav";
                                string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace('\\', '/');
                                var newEntry = new VoiceEntryTemplate
                                {
                                    DialogueFrom = festivalSourceInfo,
                                    DialogueText = cleanedPart,
                                    AudioPath = relativeAudioPath
                                };
                                characterManifest.Entries.Add(newEntry);
                                entryNumber++;
                            }
                        }
                    }
                }

                // 7. Save JSON File
                if (!characterManifest.Entries.Any()) // .Any() requires System.Linq
                {
                    if (this.Config.developerModeOn) { this.Monitor.Log($"No valid, unique entries generated after processing all sources for {characterName} ({languageCode}). Skipping JSON file.", LogLevel.Debug); }
                    return false;
                }

                // **CHECK:** Does SanitizeKeyForFileName exist?
                // PathUtilities requires StardewModdingAPI.Utilities
                string sanitizedCharName = this.SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_"); // Using 'this.'
                string filename = $"{sanitizedCharName}_{languageCode}.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, filename));

                if (this.Config.developerModeOn) { this.Monitor.Log($"Attempting to serialize and save JSON ({characterManifest.Entries.Count} total unique entries) to: {outputPath}", LogLevel.Debug); }

                // JsonConvert requires Newtonsoft.Json
                string jsonOutput = JsonConvert.SerializeObject(characterManifest, Formatting.Indented,
                                                               new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                // File.WriteAllText requires System.IO
                File.WriteAllText(outputPath, jsonOutput);

                // File.Exists requires System.IO
                if (!File.Exists(outputPath))
                {
                    if (this.Config.developerModeOn) { this.Monitor.Log($"Failed to verify JSON file save at: {outputPath}", LogLevel.Error); }
                    return false;
                }

                this.Monitor.Log($"Success! Saved template JSON ({characterManifest.Entries.Count} entries from all sources) to: {outputPath}", LogLevel.Info);

                // 8. Create Asset Folder Structure
                // PathUtilities requires StardewModdingAPI.Utilities
                string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, "assets", languageCode, characterName));
                // Directory.CreateDirectory requires System.IO
                Directory.CreateDirectory(assetsCharacterPath);

                return true; // Indicate success
            }
            catch (Exception ex) // Catch-all for the entire method's execution
            {
                // Log the top-level error that occurred during generation
                this.Monitor.Log($"FATAL ERROR during GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace); // Log detailed stack trace
                return false; // Indicate failure
            }
        } // End of GenerateSingleTemplate method


    }
}