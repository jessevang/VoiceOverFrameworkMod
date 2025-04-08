using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions; // For Regex used in GenerateSingleTemplate
using Microsoft.Xna.Framework.Content;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities; // For PathUtilities
using StardewValley; // For Game1 access in command

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

            // Add other commands here...

            this.Monitor.Log("Console commands registered.", LogLevel.Debug);
        }


        private void GenerateTemplateCommand(string command, string[] args)
        {
            // --- Argument Parsing ---
            if (args.Length < 4)
            {
                this.Monitor.Log("Invalid arguments. Use 'help vf_create_template' for details.", LogLevel.Error);
                // Log the help text directly for convenience
                this.Monitor.Log("Usage: vf_create_template <CharacterName|all> <LanguageCode|all> <YourPackID> <YourPackName>", LogLevel.Info);
                return;
            }

            // Basic validation - Check if world is ready (needed for 'all' characters)
            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase) && !Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before using 'all' characters to access game data.", LogLevel.Warn);
                return;
            }


            string targetCharacterArg = args[0];
            string targetLanguageArg = args[1];
            string baseUniqueModID = args[2].Trim(); // Base ID provided by user
            string baseVoicePackName = args[3].Trim(); // Base Name provided by user

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
                this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info);
            }
            else
            {
                string validatedLang = GetValidatedLanguageCode(targetLanguageArg); // Use utility method
                if (!string.IsNullOrWhiteSpace(validatedLang))
                {
                    languagesToProcess.Add(validatedLang);
                    this.Monitor.Log($"Processing for validated language: {validatedLang} (requested: '{targetLanguageArg}')", LogLevel.Info);
                }
            }

            if (!languagesToProcess.Any())
            {
                this.Monitor.Log($"No valid languages determined from input '{targetLanguageArg}'. Known languages: {string.Join(", ", KnownStardewLanguages)}", LogLevel.Error);
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
                        this.Monitor.Log($"Found {charactersToProcess.Count} known vanilla characters to process: {string.Join(", ", charactersToProcess)}", LogLevel.Info);
                    }
                    else
                    {
                        this.Monitor.Log("Game1.characterData is null or empty even though save is loaded. Cannot process 'all'.", LogLevel.Error);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error retrieving character list from Game1.characterData: {ex.Message}", LogLevel.Error);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                    return;
                }
            }
            else
            {
                // Allow processing any specified name (might be mod character)
                charactersToProcess.Add(targetCharacterArg);
                this.Monitor.Log($"Processing for specified character: {targetCharacterArg}", LogLevel.Info);
            }

            if (!charactersToProcess.Any() || charactersToProcess.Any(string.IsNullOrWhiteSpace))
            {
                this.Monitor.Log("No valid characters specified or found to process.", LogLevel.Error);
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
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                int langSuccessCount = 0;
                int langFailCount = 0;
                foreach (string characterName in charactersToProcess)
                {
                    // Generate names and IDs specific to this character/language instance
                    string instancePackId = $"{baseUniqueModID}.{characterName}.{languageCode}";
                    string instancePackName = $"{baseVoicePackName} ({characterName} - {languageCode})";

                    // Call the worker function. It handles saving to the correct subpath.
                    if (GenerateSingleTemplate(characterName, languageCode, outputBaseDir, instancePackId, instancePackName))
                    {
                        langSuccessCount++;
                    }
                    else
                    {
                        langFailCount++;
                    }
                }
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


        // Generates and saves a single JSON template file for one character/language combination.
        // Generates and saves a single JSON template file for one character/language combination.
        private bool GenerateSingleTemplate(string characterName, string languageCode, string outputBaseDir, string voicePackId, string voicePackName)
        {
            this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}). ID: '{voicePackId}', Name: '{voicePackName}'", LogLevel.Debug);

            // Key: Unique Dialogue Key (e.g., Dialogue Key, String Key, or Sanitized Event Text)
            // Value: Raw/Original Text corresponding to the key (used for splitting)
            var discoveredKeyTextPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Key: Unique Dialogue Key (matching above)
            // Value: Source information string (e.g., "Dialogue", "Strings/Characters", "Event:Town/123")
            var sourceTracking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // --- 1. Load Dialogue from Characters/Dialogue/... ---
                string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                string specificDialogueAssetKey = $"Characters/Dialogue/{characterName}{langSuffix}";
                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                    if (dialogueData != null)
                    {
                        foreach (var kvp in dialogueData)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key))
                            {
                                discoveredKeyTextPairs[kvp.Key] = kvp.Value; // Store Key -> Raw Value
                                sourceTracking[kvp.Key] = "Dialogue"; // Track source by Key
                            }
                        }
                        Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace);
                    }
                    // else { Monitor.Log($"Asset '{specificDialogueAssetKey}' loaded as null.", LogLevel.Trace); }
                }
                catch (ContentLoadException) { Monitor.Log($"Asset '{specificDialogueAssetKey}' not found.", LogLevel.Trace); }
                catch (Exception ex) { Monitor.Log($"Error loading '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); Monitor.Log(ex.ToString(), LogLevel.Trace); }


                // --- 2. Load Dialogue from Strings/Characters/... ---
                var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent); // Method in Utilities.cs
                Monitor.Log($"Found {stringCharData.Count} potential entries from Strings/Characters for '{characterName}' ({languageCode}).", LogLevel.Trace);
                foreach (var kvp in stringCharData)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key))
                    {
                        discoveredKeyTextPairs[kvp.Key] = kvp.Value; // Store Key -> Raw Value
                        sourceTracking[kvp.Key] = "Strings/Characters"; // Track source by Key
                    }
                    // else if (discoveredKeyTextPairs.ContainsKey(kvp.Key)) { Monitor.Log($"Key '{kvp.Key}' from Strings/Characters already present. Skipping.", LogLevel.Trace); }
                }


                // --- 3. Load Dialogue from Events ---
                // Call the helper method from ModEntry.Utilities.cs
                var eventDialogue = GetEventDialogueForCharacter(characterName, languageCode, this.Helper.GameContent);
                Monitor.Log($"Merging {eventDialogue.Count} unique dialogue lines found in events.", LogLevel.Trace);

                // Keep track of sanitized texts added from events to avoid internal duplicates
                var uniqueSanitizedEventTextsAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in eventDialogue) // Key: Sanitized Text, Value: Source Info
                {
                    string sanitizedEventText = kvp.Key;
                    string eventSourceInfo = kvp.Value;

                    // Check if this *sanitized text* already exists from Dialogue/Strings sources
                    bool alreadyExistsInStandardDialogue = discoveredKeyTextPairs
                        .Where(pair => !sourceTracking[pair.Key].StartsWith("Event:")) // Only check non-event sources
                        .Any(pair => SanitizeDialogueText(pair.Value).Equals(sanitizedEventText, StringComparison.OrdinalIgnoreCase));

                    // Also check if this exact sanitized text was already added from another event line
                    if (!alreadyExistsInStandardDialogue && !uniqueSanitizedEventTextsAdded.Contains(sanitizedEventText))
                    {
                        // Add the sanitized text as a key. Use the sanitized text itself as the value
                        // since event lines aren't typically split further. Source tracking uses this key too.
                        discoveredKeyTextPairs[sanitizedEventText] = sanitizedEventText;
                        sourceTracking[sanitizedEventText] = eventSourceInfo;
                        uniqueSanitizedEventTextsAdded.Add(sanitizedEventText); // Track that we added this event line
                        // Monitor.Log($"Added event text to processing list: '{sanitizedEventText}' from {eventSourceInfo}", LogLevel.Trace);
                    }
                    // else { Monitor.Log($"Event text '{sanitizedEventText}' skipped (already present from other source or duplicate event line).", LogLevel.Trace); }
                }


                // --- 4. Process and Generate Entries ---
                if (!discoveredKeyTextPairs.Any())
                {
                    Monitor.Log($"No dialogue keys/texts found from any source for '{characterName}' ({languageCode}). Skipping JSON generation.", LogLevel.Warn);
                    return false;
                }
                Monitor.Log($"Processing {discoveredKeyTextPairs.Count} unique dialogue keys/texts for '{characterName}' ({languageCode}).", LogLevel.Trace);

                var characterManifest = new VoicePackManifestTemplate
                {
                    Format = "1.0.0",
                    VoicePackId = voicePackId,
                    VoicePackName = voicePackName,
                    Character = characterName,
                    Language = languageCode,
                    Entries = new List<VoiceEntryTemplate>()
                };

                int entryNumber = 1;

                // Process sorted keys/texts for consistent output
                foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key))
                {
                    string processingKey = kvp.Key; // Dialogue Key, String Key, or Sanitized Event Text
                    string rawValueToProcess = kvp.Value; // Raw Dialogue/String text, or Sanitized Event text
                    string source = sourceTracking.TryGetValue(processingKey, out var src) ? src : "Unknown";

                    // Monitor.Log($"-- Processing Key/Text: '{processingKey}', Source: '{source}', ValueToProcess: \"{rawValueToProcess}\"", LogLevel.Trace);

                    // Split dialogue *only if* it came from Dialogue or Strings. Event text is treated as single segment.
                    string[] splitSegments;
                    if (source.StartsWith("Event:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Use the value directly (which is already the sanitized text in this case)
                        splitSegments = new[] { rawValueToProcess };
                        // Monitor.Log($"   -> Treating as single segment (from Event).", LogLevel.Trace);
                    }
                    else
                    {
                        // Split standard dialogue using known delimiters
                        splitSegments = Regex.Split(rawValueToProcess, @"(?:##|#\$e#|#\$b#)");
                        // Monitor.Log($"   -> Split into {splitSegments.Length} segment(s).", LogLevel.Trace);
                    }


                    for (int i = 0; i < splitSegments.Length; i++)
                    {
                        string rawPart = splitSegments[i];
                        // Monitor.Log($"     -> Raw Segment {i + 1}: \"{rawPart}\"", LogLevel.Trace);

                        // Sanitize the segment (critical step!)
                        string cleanedPart = SanitizeDialogueText(rawPart);
                        // Monitor.Log($"     -> Sanitized Segment {i + 1}: \"{cleanedPart}\"", LogLevel.Trace);

                        // Skip if the cleaned part is empty or just whitespace
                        if (string.IsNullOrWhiteSpace(cleanedPart))
                        {
                            // Monitor.Log($"        -> Skipping empty/whitespace segment {i + 1}.", LogLevel.Trace);
                            continue;
                        }

                        // Generate relative audio path
                        string numberedFileName = $"{entryNumber}.wav";
                        string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace('\\', '/');

                        // Create the entry for the JSON
                        var newEntry = new VoiceEntryTemplate
                        {
                            DialogueFrom = source, // Store where the text came from
                            DialogueText = cleanedPart, // Store the *cleaned* text - THIS IS THE LOOKUP KEY LATER
                            AudioPath = relativeAudioPath
                        };

                        characterManifest.Entries.Add(newEntry);
                        // Monitor.Log($"        -> Added Entry #{entryNumber}. Text: \"{newEntry.DialogueText}\", Path: \"{newEntry.AudioPath}\"", LogLevel.Trace);

                        entryNumber++; // Increment for the next unique file name
                    }
                } // End foreach key-value pair

                // --- 5. Save JSON File ---
                if (!characterManifest.Entries.Any())
                {
                    Monitor.Log($"No valid entries generated after processing for {characterName} ({languageCode}). Skipping JSON file.", LogLevel.Debug);
                    return false;
                }

                // Construct the output file path
                string sanitizedCharName = SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_");
                string filename = $"{sanitizedCharName}_{languageCode}.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, filename));

                Monitor.Log($"Attempting to serialize and save JSON to: {outputPath}", LogLevel.Debug);
                // Serialize with indentation for readability
                string jsonOutput = JsonConvert.SerializeObject(characterManifest, Formatting.Indented,
                                                               new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }); // Ignore null values

                // Write the JSON file
                File.WriteAllText(outputPath, jsonOutput);

                // Verify save (optional but good practice)
                if (!File.Exists(outputPath))
                {
                    Monitor.Log($"Failed to verify JSON file save at: {outputPath}", LogLevel.Error);
                    return false;
                }
                Monitor.Log($"Success! Saved template JSON ({characterManifest.Entries.Count} entries) to: {outputPath}", LogLevel.Info);


                // --- 6. Create Asset Folder Structure ---
                // Create the expected asset folder structure within the template directory
                string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, "assets", languageCode, characterName));
                Directory.CreateDirectory(assetsCharacterPath);
                // Monitor.Log($"Created/verified asset folder structure at: {assetsCharacterPath}", LogLevel.Debug);


                return true; // Success
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"ERROR during GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace); // Log stack trace for debugging
                return false; // Indicate failure
            }
        }




    }
}