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
                               "  create_template Vanilla en MyName.AllVanillaVoices AllVanillaVoices 1 ogg\n\n" +
                               "  create_template all en MyName.AllVoices AllVoices 1\n" +
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
                "update_template",
                "update_template <FolderPath> <StartNumber> [ogg|wav]\n" +
                "Adds only missing lines to existing template JSONs in <FolderPath>,\n" +
                "assigning new AudioPaths starting at <StartNumber>.",
                UpdateTemplateCommand
            );



            commands.Add(
                name: "list_characters",
                documentation: "Lists all loaded characters and shows whether they are vanilla or modded.\n\n" +
                                "Usage: list_characters",
                callback: this.ListAllNPCCharacterData
            );


            DialogueTester(commands);

            
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



       
        private static bool TryParseStartNumber(string raw, out int value, out string extFromArg)
        {
            value = 0;
            extFromArg = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // sniff extension inside arg (e.g., "12.wav" or "start_12.ogg")
            var mext = Regex.Match(raw, @"\.(wav|ogg)\b", RegexOptions.IgnoreCase);
            if (mext.Success)
                extFromArg = mext.Groups[1].Value.ToLowerInvariant();

            // take the last integer found in the string
            var m = Regex.Match(raw, @"(\d+)(?!.*\d)");
            if (!m.Success)
                return false;

            return int.TryParse(m.Groups[1].Value, out value);
        }



        private void GenerateTemplateCommand(string command, string[] args)
        {
            // args: <Character|all|vanilla|modded|*match|!*exclude> <Language|all> <YourPackID> <YourPackName> <StartNumber[.wav|.ogg]> [wav|ogg]
            if (args == null || args.Length < 5)
            {
                this.Monitor.Log("Invalid arguments. Use 'help create_template' for details.", LogLevel.Error);
                this.Monitor.Log("Usage: create_template <CharacterName|all|vanilla|modded|*match|!*exclude> <LanguageCode|all> <YourPackID> <YourPackName> <StartNumber[.wav|.ogg]> [wav|ogg]", LogLevel.Info);
                this.Monitor.Log("Examples:", LogLevel.Info);
                this.Monitor.Log("  create_template Marlon en My.PackID \"My Pack\" 1", LogLevel.Info);
                this.Monitor.Log("  create_template Marlon en My.PackID \"My Pack\" 1.wav", LogLevel.Info);
                this.Monitor.Log("  create_template * en My.PackID \"My Pack\" 42 ogg", LogLevel.Info);
                return;
            }

            // For options that enumerate across the roster, require a loaded save so content is fully patched.
            if ((args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                || args[0].Contains("*"))
                && !Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before using wildcard, 'all', or 'vanilla' to access game data.", LogLevel.Warn);
                return;
            }

            string targetCharacterArg = args[0];
            string targetLanguageArg = args[1];
            string baseUniqueModID = (args[2] ?? "").Trim();
            string baseVoicePackName = (args[3] ?? "").Trim();

            // --- parse start number & optional extension from arg[4] ---
            int startsAtThisNumber;
            string extFromArg4;
            if (!TryParseStartNumber(args[4], out startsAtThisNumber, out extFromArg4))
            {
                this.Monitor.Log($"[create_template] Could not parse start number from '{args[4]}'. Defaulting to 1.", LogLevel.Warn);
                startsAtThisNumber = 1;
            }
            if (startsAtThisNumber < 1)
            {
                this.Monitor.Log($"[create_template] Start number '{startsAtThisNumber}' is less than 1. Coercing to 1.", LogLevel.Warn);
                startsAtThisNumber = 1;
            }

            // desired extension: default ogg, overridden by arg[5], else by extension embedded in arg[4]
            string desiredExtension = "ogg";
            if (args.Length >= 6)
            {
                string extArg = (args[5] ?? "").Trim().ToLowerInvariant();
                if (extArg == "ogg" || extArg == "wav")
                {
                    desiredExtension = extArg;
                }
                else if (!string.IsNullOrWhiteSpace(extArg))
                {
                    this.Monitor.Log($"[create_template] Unknown extension '{extArg}', defaulting to ogg.", LogLevel.Warn);
                }
            }
            else if (!string.IsNullOrEmpty(extFromArg4))
            {
                desiredExtension = extFromArg4; // respect inline ext if no explicit 6th arg
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
                else if (targetCharacterArg.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                {
                    charactersToProcess = allCharacters.Where(IsKnownVanillaVillager).ToList();
                }
                else if (targetCharacterArg.Equals("modded", StringComparison.OrdinalIgnoreCase))
                {
                    charactersToProcess = allCharacters.Where(n => !IsKnownVanillaVillager(n)).ToList();
                }
                else if (targetCharacterArg.StartsWith("!*"))
                {
                    string exclude = targetCharacterArg.Substring(2);
                    charactersToProcess = allCharacters.Where(n => !n.Contains(exclude, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                else if (targetCharacterArg.StartsWith("*"))
                {
                    string include = targetCharacterArg.Substring(1);
                    charactersToProcess = allCharacters.Where(n => n.Contains(include, StringComparison.OrdinalIgnoreCase)).ToList();
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

            charactersToProcess = charactersToProcess
                .Where(n => !ShouldSkipCharacterForTemplates(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!charactersToProcess.Any() || charactersToProcess.Any(string.IsNullOrWhiteSpace))
            {
                this.Monitor.Log("[create_template] No valid characters specified or found to process.", LogLevel.Error);
                return;
            }

            // Language mismatch warning (modded i18n note)
            string currentGameLang = LocalizedContentManager.CurrentLanguageCode.ToString();
            bool includesModdedCharacters = charactersToProcess.Any(name => !IsKnownVanillaVillager(name));
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
            if (ShouldSkipCharacterForTemplates(characterName))
            {
                this.Monitor.Log($"Skipping '{characterName}': placeholder/invalid for template generation.", LogLevel.Info);
                return false;
            }

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
                characterManifest.Entries.AddRange(BuildFromRainyDialogueForCharacter(characterName, languageCode, this.Helper.GameContent, ref entryNumber, desiredExtension));
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

                var sanitizedCharName = this.SanitizeKeyForFileName(characterName);
                if (string.IsNullOrWhiteSpace(sanitizedCharName)
                    || sanitizedCharName.Equals("sanitized_key", StringComparison.OrdinalIgnoreCase)
                    || sanitizedCharName.Equals("invalid_or_empty_key", StringComparison.OrdinalIgnoreCase))
                {
                    this.Monitor.Log($"Skipping save for '{characterName}': invalid/unsafe filename after sanitization.", LogLevel.Warn);
                    return false;
                }

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




        private void UpdateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 2)
            {
                this.Monitor.Log("Usage: update_template <FolderPath> <StartNumber> [ogg|wav]", LogLevel.Error);
                return;
            }

            string folder = args[0];
            if (!Directory.Exists(folder))
            {
                this.Monitor.Log($"Folder not found: {folder}", LogLevel.Error);
                return;
            }

            if (!int.TryParse(args[1], out int startNumber) || startNumber < 1)
            {
                this.Monitor.Log($"Invalid StartNumber: {args[1]}", LogLevel.Error);
                return;
            }

            string ext = "ogg";
            if (args.Length >= 3)
            {
                var e = (args[2] ?? "").Trim().ToLowerInvariant();
                if (e == "ogg" || e == "wav") ext = e;
                else this.Monitor.Log($"Unknown extension '{args[2]}', defaulting to {ext}.", LogLevel.Warn);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to enumerate JSON files in '{folder}': {ex.Message}", LogLevel.Error);
                return;
            }

            if (files.Length == 0)
            {
                this.Monitor.Log($"No template JSON files found in: {folder}", LogLevel.Warn);
                return;
            }

            int totalFilesUpdated = 0;
            int totalNewEntries = 0;

            foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var file = JsonConvert.DeserializeObject<VoicePackFile>(json);
                    if (file?.VoicePacks == null || file.VoicePacks.Count == 0)
                    {
                        if (this.Config.developerModeOn)
                            this.Monitor.Log($"[update_template] Skipped (no voice packs): {Path.GetFileName(path)}", LogLevel.Trace);
                        continue;
                    }

                    bool fileChanged = false;

                    foreach (var vp in file.VoicePacks)
                    {

                        if (vp == null || string.IsNullOrWhiteSpace(vp.Character) || string.IsNullOrWhiteSpace(vp.Language))
                            continue;

                        var entries = vp.Entries ??= new List<VoiceEntryTemplate>();


                        var existingDisplays = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var e in entries)
                        {
                            var disp = NormalizeDisplay(e?.DisplayPattern ?? e?.DialogueText ?? "");
                            if (!string.IsNullOrEmpty(disp))
                                existingDisplays.Add(disp);
                        }


                        var candidates = GenerateAllEntriesFor(
                            vp.Character,
                            vp.Language,
                            ext
                        ).ToList();

                        if (candidates.Count == 0)
                        {
                            if (this.Config.developerModeOn)
                                this.Monitor.Log($"[update_template] No candidates for {vp.Character} ({vp.Language}) in {Path.GetFileName(path)}.", LogLevel.Trace);
                            continue;
                        }


                        int addedForThisPack = 0;
                        int nextNum = startNumber;


                        var sessionDisplays = new HashSet<string>(StringComparer.Ordinal);


                        var assetsDir = Path.Combine(Path.GetDirectoryName(path) ?? folder, "assets", vp.Language, vp.Character);
                        Directory.CreateDirectory(assetsDir);


                        foreach (var cand in candidates
                            .OrderBy(c => NormalizeDisplay(c?.DisplayPattern ?? c?.DialogueText ?? ""))
                            .ThenBy(c => c.TranslationKey ?? "")
                            .ThenBy(c => c.PageIndex)
                            .ThenBy(c => c.GenderVariant ?? ""))
                        {
                            var disp = NormalizeDisplay(cand?.DisplayPattern ?? cand?.DialogueText ?? "");
                            if (string.IsNullOrEmpty(disp))
                                continue;


                            if (existingDisplays.Contains(disp) || sessionDisplays.Contains(disp))
                                continue;


                            string genderTail = string.IsNullOrEmpty(cand.GenderVariant) ? "" : $"_{cand.GenderVariant}";
                            string fileName = $"{nextNum}{genderTail}.{ext}";
                            cand.AudioPath = Path.Combine("assets", vp.Language, vp.Character, fileName).Replace('\\', '/');

                            entries.Add(cand);
                            existingDisplays.Add(disp);
                            sessionDisplays.Add(disp);
                            addedForThisPack++;
                            nextNum++;
                        }

                        if (addedForThisPack > 0)
                        {
                            fileChanged = true;
                            totalNewEntries += addedForThisPack;
                            this.Monitor.Log($"[update_template] {vp.Character} ({vp.Language}): +{addedForThisPack} new entries in {Path.GetFileName(path)}.", LogLevel.Info);
                        }
                        else if (this.Config.developerModeOn)
                        {
                            this.Monitor.Log($"[update_template] {vp.Character} ({vp.Language}): no new entries.", LogLevel.Trace);
                        }
                    }

                    if (fileChanged)
                    {
                      
                        var output = JsonConvert.SerializeObject(file, Formatting.Indented,
                            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                        File.WriteAllText(path, output);
                        totalFilesUpdated++;
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"[update_template] Failed processing '{path}': {ex.Message}", LogLevel.Error);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                }
            }

            this.Monitor.Log($"[update_template] Done. Files updated: {totalFilesUpdated}, new entries added: {totalNewEntries}.", LogLevel.Info);
        }


        // Build identity for “same line” detection. Keep it stable across runs.
        private static string MakeEntryIdentity(VoiceEntryTemplate e)
        {
            string tlk = e?.TranslationKey ?? "";
            string page = e?.PageIndex.ToString() ?? "0";
            string gen = e?.GenderVariant ?? "";
            // TranslationKey + PageIndex + Gender is stable; avoid comparing text that may be re-sanitized.
            return $"{tlk}|p{page}|g{gen}";
        }

     
        // using the same pipeline as create_template — but we don’t care about AudioPath here.
        private IEnumerable<VoiceEntryTemplate> GenerateAllEntriesFor(string characterName, string languageCode, string ext)
        {
            var all = new List<VoiceEntryTemplate>();
            int dummy = 1; // numbering inside candidates will be ignored

            // Core dialogue
            all.AddRange(BuildFromCharacterDialogue(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));

            if (IsMarriableCharacter(characterName))
            {
                all.AddRange(BuildFromEngagement(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
                all.AddRange(BuildFromMarriageDialogue(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            }

            all.AddRange(BuildFromRainyDialogueForCharacter(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromEvents(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromFestivals(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromGiftTastes(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromExtraDialogue(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromMovieReactions(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildFromOneSixStrings(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));
            all.AddRange(BuildSpeechBubbleEntries(characterName, languageCode, this.Helper.GameContent, ref dummy, ext));

            // We must NOT renumber PageIndex; these come from the sanitizers.
            // Also, we ignore AudioPath here; caller will assign new paths for appended items.
            return all;
        }


        private static string NormalizeDisplay(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // Trim + collapse internal whitespace; keep case and punctuation as-is
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }





    }

}