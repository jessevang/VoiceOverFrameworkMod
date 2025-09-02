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
                name: "vof_port",
                documentation: "Auto-port a V1 voice pack to V2.\n" +
                               "Usage: vof_port <V1 pack folder under Mods or absolute path>\n" +
                               "- Output goes to '<V1 folder>_output'.\n" +
                               "- Baseline V2 templates are generated automatically.",
                callback: Cmd_PortAuto
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

            if (this.Helper == null || this.Monitor == null || this.Config == null)
            {
                this.Monitor?.Log("GenerateSingleTemplate cannot run: Helper, Monitor, or Config is null.", LogLevel.Error);
                return false;
            }

            if (this.Config.developerModeOn)
                this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}). ID: '{voicePackId}', Name: '{voicePackName}' AudioFileStartsAt: {startAtThisNumber}", LogLevel.Debug);

            try
            {

                var characterManifest = new VoicePackManifestTemplate
                {
                    Format = "2.0.0",
                    VoicePackId = voicePackId,
                    VoicePackName = voicePackName,
                    Character = characterName,
                    Language = languageCode,
                    Entries = new List<VoiceEntryTemplate>()
                };

                int entryNumber = startAtThisNumber;

                //  Reactored all dialogue  sanitizer rules
                characterManifest.Entries.AddRange(BuildFromCharacterDialogue(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));

                if (IsMarriableCharacter(characterName))
                {
                    characterManifest.Entries.AddRange(BuildFromEngagement(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                    characterManifest.Entries.AddRange(BuildFromMarriageDialogue(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                }
                
                characterManifest.Entries.AddRange(BuildFromEvents(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));

                characterManifest.Entries.AddRange(BuildFromFestivals(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                characterManifest.Entries.AddRange(BuildFromGiftTastes(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                characterManifest.Entries.AddRange(BuildFromExtraDialogue(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                characterManifest.Entries.AddRange(BuildFromMovieReactions(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                //Did not include mail code yet because Don't have code read mail yet.
                //characterManifest.Entries.AddRange(BuildFromMail(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                characterManifest.Entries.AddRange(BuildFromOneSixStrings(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
                characterManifest.Entries.AddRange(BuildSpeechBubbleEntries(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));


                
                
                //string forced = $"1.{desiredExtension}"; //TEST 1.ogg disable to let dialogue audotio path build
               // foreach (var e in characterManifest.Entries)  //TEST disable to let dialogue audotio path build
                //    e.AudioPath = forced;
                

                // --- WRITE / SAVE (unchanged except for renumber) ---
                if (!characterManifest.Entries.Any())
                {
                    if (this.Config.developerModeOn)
                        this.Monitor.Log($"Skipping save for {characterName} ({languageCode}): No entries after processing.", LogLevel.Info);
                    return false;
                }

                GenerateTemplate_DialogueFromDeduplicated(characterManifest.Entries);


                RenumberPageIndicesPerKey(characterManifest.Entries);

                var finalOutputFileObject = new VoicePackFile
                {
                    Format = "2.0.0",
                    VoicePacks = { characterManifest }
                };

                string sanitizedCharName = this.SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_");
                string filename = $"{sanitizedCharName}_{languageCode}.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(outputBaseDir, filename));  //Original

                


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

                // ensure asset folder exists (unchanged)
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
            catch (Exception ex)
            {
                this.Monitor.Log($"FATAL ERROR during GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace);
                return false;
            }
        }


        // Renumber page indices so each TranslationKey has 0..N-1, in a stable order.
        private static void RenumberPageIndicesPerKey(List<VoiceEntryTemplate> entries)
        {
            if (entries == null || entries.Count == 0) return;

            foreach (var group in entries.GroupBy(e => e.TranslationKey ?? string.Empty))
            {
                int i = 0;
                foreach (var e in group
                    .OrderBy(e => e.PageIndex)                 // keep original page order first
                    .ThenBy(e => e.GenderVariant ?? string.Empty)
                    .ThenBy(e => e.AudioPath ?? string.Empty))
                {
                    e.PageIndex = i++;
                }
            }
        }


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