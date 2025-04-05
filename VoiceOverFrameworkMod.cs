// VoiceOverFrameworkMod.cs

// --- Required using statements ---
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content; 
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace VoiceOverFrameworkMod
{
    // --- Supporting Class Definitions ---
    // Verify these definitions EXACTLY match what's below. No extra copies elsewhere.
    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public bool FallbackToDefaultIfMissing { get; set; } = true;
        // *** Ensure this property exists and is public ***
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new();
    }

    public class VoicePackWrapper
    {
        public List<VoicePackManifest> VoicePacks { get; set; }
    }

    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; }
        public List<VoiceEntry> Entries { get; set; }
    }

    public class VoiceEntry
    {
        public string DialogueKey { get; set; }
        public string AudioPath { get; set; }
    }

    public class VoicePack
    {
        public string VoicePackId;
        public string VoicePackName;
        public string Character;
        public string Language;
        public Dictionary<string, string> Entries;
    }

    public class VoicePackWrapperTemplate
    {
        // *** Ensure this property exists and is public ***
        public List<VoicePackManifestTemplate> VoicePacks { get; set; } = new List<VoicePackManifestTemplate>();
    }

    public class VoicePackManifestTemplate
    {
        public string Format { get; set; } = "1.0.0";
        // *** Ensure these properties exist and are public ***
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; } = "en";
        public List<VoiceEntryTemplate> Entries { get; set; } = new List<VoiceEntryTemplate>();
    }

    public class VoiceEntryTemplate
    {
        // *** Ensure these properties exist and are public ***
        public string DialogueFrom { get; set; }
        public string DialogueKey { get; set; }
        public string DialogueText { get; set; }
        public string AudioPath { get; set; }
    }


    // --- Main Mod Class ---
    public partial class ModEntry : Mod
    {
        public static ModEntry Instance;

        private ModConfig Config;
        private Dictionary<string, string> SelectedVoicePacks;
        private Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new();

        private SoundEffectInstance currentVoiceInstance;
        private string lastDialogueText = null;
        private string lastSpeakerName = null;

        private readonly List<string> KnownStardewLanguages = new List<string> {
            "en", "es-ES", "zh-CN", "ja-JP", "pt-BR", "fr-FR", "ko-KR", "it-IT", "de-DE", "hu-HU", "ru-RU", "tr-TR"
        };

 
        // --- Mod Entry Point ---
        public override void Entry(IModHelper helper)
        {
            Instance = this;
            this.Monitor.Log("ModEntry.Entry() called.", LogLevel.Debug);

            Config = helper.ReadConfig<ModConfig>();
            // *** FIXED: Ensure SelectedVoicePacks is assigned even if Config itself was null (though ReadConfig usually returns a default) ***
            SelectedVoicePacks = Config?.SelectedVoicePacks ?? new Dictionary<string, string>();

            LoadVoicePacks();

            // *** ADDED: Apply Harmony Patches ***
            ApplyHarmonyPatches();

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            SetupConsoleCommands(helper.ConsoleCommands);

            Monitor.Log("Voice Over Framework initialized.", LogLevel.Info);
        }

        // --- Harmony Setup ---
        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);
            try
            {
                this.Monitor.Log("Applying Harmony patches...", LogLevel.Debug);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                this.Monitor.Log("Harmony patches applied successfully.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"ERROR applying Harmony patches: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
        }
       

        // --- Event Handlers ---
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e) { /* For GMCM */ }
        private void LoadVoicePacks()
        { /* ... as before, assuming Path.Combine was okay ... */
            VoicePacksByCharacter.Clear();
            var allContentPacks = this.Helper.ContentPacks.GetOwned();
            Monitor.Log($"Scanning {allContentPacks.Count()} content packs for voice data...", LogLevel.Debug);

            foreach (var pack in allContentPacks)
            {
                try
                {
                    var wrapper = pack.ReadJsonFile<VoicePackWrapper>("content.json");
                    if (wrapper?.VoicePacks == null) continue;
                    // Monitor.Log($"Found voice pack definitions in '{pack.Manifest.Name}'.", LogLevel.Trace); // Reduced noise
                    foreach (var metadata in wrapper.VoicePacks)
                    {
                        if (string.IsNullOrWhiteSpace(metadata.VoicePackId) || string.IsNullOrWhiteSpace(metadata.VoicePackName) || string.IsNullOrWhiteSpace(metadata.Character) || metadata.Entries == null)
                        { Monitor.Log($"Skipping invalid voice pack entry in '{pack.Manifest.Name}': Missing required fields.", LogLevel.Warn); continue; }

                        // Use PathUtilities for safety
                        var entriesDict = metadata.Entries
                                        .Where(e => !string.IsNullOrWhiteSpace(e?.DialogueKey) && !string.IsNullOrWhiteSpace(e?.AudioPath)) // Filter bad entries
                                        .ToDictionary(e => e.DialogueKey, e => PathUtilities.NormalizePath(Path.Combine(pack.DirectoryPath, e.AudioPath)));


                        var voicePack = new VoicePack
                        {
                            VoicePackId = metadata.VoicePackId,
                            VoicePackName = metadata.VoicePackName,
                            Language = metadata.Language ?? "en",
                            Character = metadata.Character,
                            Entries = entriesDict // Use sanitized dict
                        };

                        if (!VoicePacksByCharacter.TryGetValue(voicePack.Character, out var list))
                        {
                            list = new List<VoicePack>();
                            VoicePacksByCharacter[voicePack.Character] = list;
                        }

                        if (!list.Any(p => p.VoicePackId.Equals(voicePack.VoicePackId, StringComparison.OrdinalIgnoreCase) &&
                                           p.Language.Equals(voicePack.Language, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(voicePack);
                            // Monitor.Log($"Loaded voice pack '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) for {voicePack.Character} [{voicePack.Language}].", LogLevel.Trace); // Reduced noise
                        }
                        // else { Monitor.Log($"Skipping duplicate voice pack ID '{voicePack.VoicePackId}' for {voicePack.Character} [{voicePack.Language}] in '{pack.Manifest.Name}'.", LogLevel.Warn); } // Reduced noise
                    }
                }
                catch (Exception ex) { Monitor.Log($"Error loading voice pack definition from '{pack.Manifest.Name}': {ex.Message}", LogLevel.Error); Monitor.Log(ex.ToString(), LogLevel.Trace); } // Log trace
            }
            Monitor.Log($"Finished loading voice packs. Found packs for {VoicePacksByCharacter.Count} unique characters.", LogLevel.Debug);
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e) { /* ... as before ... */ }
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e) { /* ... as before ... */ }

        // --- Core Voice Playback Logic ---
        // Inside ModEntry class

        public void TryPlayVoice(string characterName, string dialogueKey)
        {
            if (Config == null || SelectedVoicePacks == null)
            {
                Monitor.LogOnce("Config or SelectedVoicePacks is null in TryPlayVoice. Cannot proceed.", LogLevel.Warn);
                return;
            }

            // Log the initial attempt with more context
            Monitor.Log($"[TryPlayVoice] Attempting voice lookup: Char='{characterName}', Key='{dialogueKey}'", LogLevel.Trace);

            if (!SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) || string.IsNullOrEmpty(selectedVoicePackId))
            {
                Monitor.Log($"[TryPlayVoice] No voice pack configured for '{characterName}' in config.json.", LogLevel.Trace);
                return; // No pack selected for this character
            }
            Monitor.Log($"[TryPlayVoice] Configured VoicePackId for '{characterName}': '{selectedVoicePackId}'", LogLevel.Trace);


            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks) || !availablePacks.Any())
            {
                Monitor.Log($"[TryPlayVoice] No loaded voice packs found matching character name '{characterName}'.", LogLevel.Trace);
                return; // No packs loaded at all for this character
            }

            string targetLanguage = Config.DefaultLanguage ?? "en"; // Assuming GetValidatedLanguageCode happens elsewhere if needed
            string fallbackLanguage = "en";

            VoicePack selectedPack = availablePacks.FirstOrDefault(p =>
                p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                p.Language.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase));

            // Try fallback language if configured and needed
            bool usedFallback = false;
            if (selectedPack == null && Config.FallbackToDefaultIfMissing && !targetLanguage.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase))
            {
                Monitor.Log($"[TryPlayVoice] Pack '{selectedVoicePackId}' not found for primary language '{targetLanguage}', trying fallback '{fallbackLanguage}'.", LogLevel.Trace);
                selectedPack = availablePacks.FirstOrDefault(p =>
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase));
                if (selectedPack != null) usedFallback = true;
            }

            if (selectedPack == null)
            {
                Monitor.Log($"[TryPlayVoice] Failed to find loaded voice pack matching ID='{selectedVoicePackId}' for character '{characterName}' (Lang='{targetLanguage}', Fallback attempted: {Config.FallbackToDefaultIfMissing && !targetLanguage.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase)}).", LogLevel.Warn);
                return; // No suitable pack found
            }
            Monitor.Log($"[TryPlayVoice] Found matching loaded pack: '{selectedPack.VoicePackName}' (ID: '{selectedPack.VoicePackId}', Lang: '{selectedPack.Language}', Used Fallback: {usedFallback})", LogLevel.Debug);


            // *** THE KEY LOOKUP ***
            if (selectedPack.Entries.TryGetValue(dialogueKey, out string audioPath))
            {
                // *** LOG SUCCESSFUL LOOKUP ***
                Monitor.Log($"[TryPlayVoice] SUCCESS: Found path for key '{dialogueKey}' in pack '{selectedPack.VoicePackName}'. Path: '{audioPath}'", LogLevel.Debug);
                PlayVoiceFromFile(audioPath); // Call the playback method
            }
            else
            {
                // *** LOG FAILED LOOKUP ***
                Monitor.Log($"[TryPlayVoice] FAILED: Dialogue key '{dialogueKey}' not found within the 'Entries' of selected pack '{selectedPack.VoicePackName}' (Lang: '{selectedPack.Language}').", LogLevel.Debug); // Changed to Debug as this might be common/expected
            }
        }



        // Inside ModEntry class

        private void PlayVoiceFromFile(string audioFilePath)
        {
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                Monitor.Log($"[PlayVoiceFromFile] Attempted to play null or empty audio file path. Aborting.", LogLevel.Warn);
                return;
            }

            // *** LOG 1: Entry and Path ***
            Monitor.Log($"[PlayVoiceFromFile] Received request to play: '{audioFilePath}'", LogLevel.Debug);

            try
            {
                // *** LOG 2: Check File Existence ***
                if (!File.Exists(audioFilePath))
                {
                    Monitor.Log($"[PlayVoiceFromFile] ERROR: File.Exists returned FALSE for path: {audioFilePath}", LogLevel.Error);
                    return; // Exit if file doesn't exist
                }
                Monitor.Log($"[PlayVoiceFromFile] File.Exists returned TRUE for path: {audioFilePath}", LogLevel.Trace);


                // Stop and dispose previous instance *cleanly*
                if (currentVoiceInstance != null)
                {
                    Monitor.Log($"[PlayVoiceFromFile] Previous instance exists. State: {currentVoiceInstance.State}. IsDisposed: {currentVoiceInstance.IsDisposed}", LogLevel.Trace);
                    if (!currentVoiceInstance.IsDisposed)
                    {
                        currentVoiceInstance.Stop(true); // Immediate stop
                        currentVoiceInstance.Dispose();
                        Monitor.Log($"[PlayVoiceFromFile] Stopped and disposed previous instance.", LogLevel.Trace);
                    }
                    currentVoiceInstance = null; // Clear reference
                }

                // Load and play the new sound
                SoundEffect sound;
                Monitor.Log($"[PlayVoiceFromFile] Attempting to create FileStream for: {audioFilePath}", LogLevel.Trace);
                using (var stream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Monitor.Log($"[PlayVoiceFromFile] FileStream created. Attempting SoundEffect.FromStream...", LogLevel.Trace);
                    sound = SoundEffect.FromStream(stream);
                    Monitor.Log($"[PlayVoiceFromFile] SoundEffect.FromStream succeeded.", LogLevel.Trace);
                } // Stream is disposed here

                Monitor.Log($"[PlayVoiceFromFile] Attempting sound.CreateInstance()...", LogLevel.Trace);
                currentVoiceInstance = sound.CreateInstance();
                Monitor.Log($"[PlayVoiceFromFile] SoundEffectInstance created.", LogLevel.Trace);

                // Optional: Log volume before playing
                // Monitor.Log($"[PlayVoiceFromFile] Game Sound Volume: {Game1.options.soundVolumeLevel}", LogLevel.Trace);
                // currentVoiceInstance.Volume = Game1.options.soundVolumeLevel; // Apply game volume

                Monitor.Log($"[PlayVoiceFromFile] Calling currentVoiceInstance.Play()...", LogLevel.Debug);
                currentVoiceInstance.Play();
                Monitor.Log($"[PlayVoiceFromFile] Play() called successfully for: {Path.GetFileName(audioFilePath)}", LogLevel.Debug);


                // Simplified cleanup for now: Let the instance handle its lifecycle mostly.
                // We primarily need to ensure we stop the *previous* one correctly.
                // Excessive manual disposal here might interfere if not perfectly timed.
                // The main risk is overlapping sounds if dialogue changes extremely fast.
                // Consider adding back StateChanged disposal if needed later for resource management.

            }
            // Catch specific exceptions first
            catch (NoAudioHardwareException) { Monitor.LogOnce("[PlayVoiceFromFile] No audio hardware detected.", LogLevel.Warn); }
            catch (FileNotFoundException fnfEx) { Monitor.Log($"[PlayVoiceFromFile] ERROR (FileNotFoundException): {audioFilePath}. Message: {fnfEx.Message}", LogLevel.Error); }
            catch (IOException ioEx) { Monitor.Log($"[PlayVoiceFromFile] ERROR (IOException): {audioFilePath}. Message: {ioEx.Message}", LogLevel.Error); Monitor.Log(ioEx.ToString(), LogLevel.Trace); }
            catch (InvalidOperationException opEx) { Monitor.Log($"[PlayVoiceFromFile] ERROR (InvalidOperationException likely during FromStream/Play): {audioFilePath}. Message: {opEx.Message}", LogLevel.Error); Monitor.Log(opEx.ToString(), LogLevel.Trace); } // Often indicates bad WAV format
            catch (Exception ex) // Catch-all for unexpected issues
            {
                Monitor.Log($"[PlayVoiceFromFile] FAILED ({ex.GetType().Name}): {audioFilePath}. Message: {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            finally // Ensure instance is cleared if exception occurred before Play() but after creation
            {
                // If an exception happened *during* play setup, currentVoiceInstance might be non-null but unusable.
                // However, clearing it here might interfere with intended playback if the exception was minor.
                // Let's rely on the start of the method to clear the previous one.
            }
        }

        // --- Console Command Setup & Implementation ---
        private void SetupConsoleCommands(ICommandHelper commands)
        {
            this.Monitor.Log("Setting up console commands...", LogLevel.Debug);
            commands.Add("voice_create_template", "...", this.GenerateTemplateCommand);
            commands.Add("voice_list_chars", "...", this.ListCharactersCommand);
            commands.Add("voice_create_combined_template", "...", this.GenerateCombinedTemplateCommand);
            this.Monitor.Log("Console commands registered.", LogLevel.Debug);
        }


        // --- Generate Combined Template Command Handler ---
        private void GenerateCombinedTemplateCommand(string command, string[] args)
        {
            this.Monitor.Log($"'{command}' command invoked.", LogLevel.Debug);

            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn);
                return;
            }
            this.Monitor.Log("Save file loaded, proceeding...", LogLevel.Trace);

            // Determine target language(s)
            string targetLanguageArg = (args.Length > 0) ? args[0] : (Config?.DefaultLanguage ?? "en");
            this.Monitor.Log($"Target language argument: '{targetLanguageArg}'", LogLevel.Trace);

            List<string> languagesToProcess = new List<string>();
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            {
                languagesToProcess.AddRange(this.KnownStardewLanguages);
                this.Monitor.Log($"Processing combined template for ALL {languagesToProcess.Count} known languages.", LogLevel.Info);
            }
            else
            {
                string validatedLang = GetValidatedLanguageCode(targetLanguageArg);
                languagesToProcess.Add(validatedLang);
                this.Monitor.Log($"Processing combined template for language: {validatedLang}", LogLevel.Info);
            }
            if (!languagesToProcess.Any())
            {
                this.Monitor.Log("No languages determined for processing. Aborting.", LogLevel.Error);
                return;
            }

            // Get list of characters to include (using IsKnownVanillaVillager filter)
            List<string> charactersToProcess = new List<string>();
            this.Monitor.Log("Gathering list of characters to include (using IsKnownVanillaVillager filter)...", LogLevel.Info);
            try
            {
                if (Game1.characterData != null && Game1.characterData.Any())
                {
                    charactersToProcess = Game1.characterData.Keys
                                            .Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name))
                                            .OrderBy(name => name)
                                            .ToList();
                    this.Monitor.Log($"Found {charactersToProcess.Count} characters to potentially include based on filter.", LogLevel.Info);
                }
                else
                {
                    this.Monitor.Log("Game1.characterData is null or empty. Cannot generate combined template.", LogLevel.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error retrieving character list: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                return;
            }

            if (!charactersToProcess.Any())
            {
                this.Monitor.Log("No characters found to process after filtering. Check IsKnownVanillaVillager.", LogLevel.Warn);
                return;
            }

            // --- Loop through LANGUAGES ---
            int totalFilesGenerated = 0;
            string baseOutputDir = PathUtilities.NormalizePath(Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates", "Combined"));
            this.Monitor.Log($"Base output directory: {baseOutputDir}", LogLevel.Trace);

            foreach (string languageCode in languagesToProcess)
            {
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                var languageWrapper = new VoicePackWrapperTemplate();
                int charactersAddedToThisFile = 0;
                var assetFoldersToCreate = new HashSet<string>();

                // --- Loop through CHARACTERS for this language ---
                foreach (string characterName in charactersToProcess)
                {
                    this.Monitor.Log($"Gathering data for '{characterName}' ({languageCode})...", LogLevel.Trace);
                    var discoveredKeyTextPairs = new Dictionary<string, string>();
                    var sourceTracking = new Dictionary<string, string>();

                    try
                    {
                        // --- Load dialogue sources ---
                        // Individual Dialogue File
                        string dialogueAssetKeyBase = $"Characters/Dialogue/{characterName}";
                        string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                        string specificDialogueAssetKey = dialogueAssetKeyBase + langSuffix;
                        try
                        {
                            var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                            if (dialogueData != null)
                            {
                                foreach (var kvp in dialogueData) { if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Dialogue"; } }
                                this.Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace);
                            }
                        }
                        catch (ContentLoadException) { this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found.", LogLevel.Trace); }
                        catch (Exception ex) { this.Monitor.Log($" Error loading '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); }

                        // Strings/Characters
                        var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent);
                        this.Monitor.Log($"Found {stringCharData.Count} entries from Strings/Characters for {characterName}.", LogLevel.Trace);
                        foreach (var kvp in stringCharData) { if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Strings/Characters"; } }

                        // --- TODO: Add Mail/Quest/Event parsing here ---

                        if (!discoveredKeyTextPairs.Any())
                        {
                            this.Monitor.Log($" No keys found for '{characterName}' in {languageCode} after checking sources. Skipping.", LogLevel.Trace);
                            continue;
                        }
                        this.Monitor.Log($"Found {discoveredKeyTextPairs.Count} total keys for '{characterName}' in {languageCode}.", LogLevel.Debug);

                        // --- Create Manifest FOR THIS CHARACTER ---
                        var characterManifest = new VoicePackManifestTemplate
                        {
                            VoicePackId = $"YourModID.Vanilla_{characterName}_{languageCode}", // Remind user to change this!
                            VoicePackName = $"{characterName} - Vanilla ({languageCode})",
                            Character = characterName,
                            Language = languageCode
                        };

                        // *** Initialize counter for this character ***
                        int entryNumber = 1;

                        foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key)) // Order by key for consistent numbering
                        {
                            string sanitizedKey = SanitizeKeyForFileName(kvp.Key);
                            if (string.IsNullOrWhiteSpace(sanitizedKey) || sanitizedKey == "invalid_key")
                            {
                                this.Monitor.Log($"Skipping entry with invalid sanitized key (Original: '{kvp.Key}') for {characterName}.", LogLevel.Warn);
                                continue;
                            }

                            // *** Format the filename WITHOUT padding (e.g., "1_Mon") ***
                            string numberedFileName = $"{entryNumber}_{sanitizedKey}.wav";

                            characterManifest.Entries.Add(new VoiceEntryTemplate
                            {
                                DialogueKey = kvp.Key,
                                DialogueText = SanitizeDialogueText(kvp.Value),
                                DialogueFrom = sourceTracking.TryGetValue(kvp.Key, out var source) ? source : "Unknown",
                                // *** Use the numbered filename in the path ***
                                AudioPath = PathUtilities.NormalizePath($"assets/{languageCode}/{characterName}/{numberedFileName}")
                            });

                            // *** Increment counter for the next entry ***
                            entryNumber++;
                        }

                        languageWrapper.VoicePacks.Add(characterManifest);
                        charactersAddedToThisFile++;
                        string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "assets", languageCode, characterName));
                        assetFoldersToCreate.Add(assetsCharacterPath);

                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Failed during data processing for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                        this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                    }
                } // --- End Character Loop ---

                // --- Save the COMBINED File for this Language ---
                this.Monitor.Log($"Finished processing characters for {languageCode}. Found dialogue for {charactersAddedToThisFile} characters.", LogLevel.Debug);
                if (charactersAddedToThisFile > 0)
                {
                    try
                    {
                        this.Monitor.Log($"Ensuring base output directory exists: {baseOutputDir}", LogLevel.Trace);
                        Directory.CreateDirectory(baseOutputDir); // Create base dir if needed

                        string jsonOutput = JsonConvert.SerializeObject(languageWrapper, Formatting.Indented);
                        string outputPath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, $"all_characters_{languageCode}_template.json"));

                        this.Monitor.Log($"Attempting to write COMBINED template JSON for {languageCode} to: {outputPath}", LogLevel.Info);
                        File.WriteAllText(outputPath, jsonOutput);

                        bool fileExists = File.Exists(outputPath);
                        if (fileExists)
                        {
                            this.Monitor.Log($"Successfully generated COMBINED template for {languageCode}.", LogLevel.Info);
                            totalFilesGenerated++;

                            // --- Create ALL asset folders for this language file ---
                            this.Monitor.Log($"Creating required asset folder structures ({assetFoldersToCreate.Count} needed)...", LogLevel.Debug);
                            int foldersCreated = 0;
                            foreach (string assetPath in assetFoldersToCreate)
                            {
                                try
                                {
                                    Directory.CreateDirectory(assetPath);
                                    foldersCreated++;
                                }
                                catch (Exception dirEx)
                                { this.Monitor.Log($" Error creating asset directory '{assetPath}': {dirEx.Message}", LogLevel.Error); }
                            }
                            this.Monitor.Log($"Created {foldersCreated} asset folder structures under '{PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "assets", languageCode))}'.", LogLevel.Info);
                        }
                        else
                        { this.Monitor.Log($"Write operation for JSON {languageCode} completed BUT File.Exists returned false! Check permissions/path: {outputPath}", LogLevel.Error); }
                    }
                    catch (UnauthorizedAccessException ex) { this.Monitor.Log($"PERMISSION ERROR saving combined template for {languageCode}: {ex.Message}. Check folder permissions for '{baseOutputDir}'.", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
                    catch (IOException ex) { this.Monitor.Log($"IO ERROR saving combined template for {languageCode}: {ex.Message}. Is the file open or path invalid?", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Failed to save combined template for {languageCode}: {ex.GetType().Name} - {ex.Message}", LogLevel.Error);
                        this.Monitor.Log(ex.ToString(), LogLevel.Error);
                    }
                }
                else
                { this.Monitor.Log($"Skipping combined file generation for {languageCode} as no characters had dialogue.", LogLevel.Warn); }

            } // --- End Language Loop ---

            this.Monitor.Log($"--- Overall Combined Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Total COMBINED files generated: {totalFilesGenerated}", LogLevel.Info);
        }


        // --- Generate Individual Templates Command Handler ---
        private void GenerateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 1)
            { this.Monitor.Log("Please provide a character name or 'all', and optionally a language code or 'all'.", LogLevel.Error); this.Monitor.Log("Usage: voice_create_template <CharacterName|all> [LanguageCode|all]", LogLevel.Info); return; }
            if (!Context.IsWorldReady)
            { this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn); return; }

            string targetCharacterArg = args[0];
            string targetLanguageArg = (args.Length > 1) ? args[1] : (Config.DefaultLanguage ?? "en");

            List<string> languagesToProcess = new List<string>();
            List<string> charactersToProcess = new List<string>();

            // Determine Languages
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            { languagesToProcess.AddRange(this.KnownStardewLanguages); this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info); }
            else
            { languagesToProcess.Add(GetValidatedLanguageCode(targetLanguageArg)); this.Monitor.Log($"Processing for language: {languagesToProcess[0]}", LogLevel.Info); }

            // Determine Characters
            if (targetCharacterArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetCharacterArg == "*")
            {
                this.Monitor.Log("Gathering list of all characters from Game1.characterData...", LogLevel.Info);
                try
                {
                    if (Game1.characterData != null && Game1.characterData.Any())
                    {
                        charactersToProcess = Game1.characterData.Keys.Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name)).OrderBy(name => name).ToList();
                        this.Monitor.Log($"Found {charactersToProcess.Count} characters to process.", LogLevel.Info);
                    }
                    else { this.Monitor.Log("Game1.characterData is null or empty. Cannot process 'all'.", LogLevel.Error); return; }
                }
                catch (Exception ex) { this.Monitor.Log($"Error retrieving character list: {ex.Message}", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); return; }
            }
            else { charactersToProcess.Add(targetCharacterArg); }

            // Loop through languages and characters
            int totalSuccessCount = 0; int totalFailCount = 0;
            string baseTemplateDir = Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates");

            foreach (string languageCode in languagesToProcess)
            {
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                int langSuccessCount = 0; int langFailCount = 0;
                foreach (string characterName in charactersToProcess)
                {
                    if (GenerateSingleTemplate(characterName, languageCode, baseTemplateDir)) { langSuccessCount++; } else { langFailCount++; }
                }
                this.Monitor.Log($"Language {languageCode} Summary - Generated: {langSuccessCount}, Failed/Skipped: {langFailCount}", LogLevel.Info);
                totalSuccessCount += langSuccessCount; totalFailCount += langFailCount;
            }

            this.Monitor.Log($"--- Overall Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Total Successfully generated: {totalSuccessCount}", LogLevel.Info);
            this.Monitor.Log($"Total Failed/Skipped: {totalFailCount}", LogLevel.Info);
        }




        // --- Generate Single Template File (Helper) ---
        private bool GenerateSingleTemplate(string characterName, string languageCode, string baseOutputDir)
        // NOTE: baseOutputDir for this method is expected to be ".../GeneratedTemplates",
        // it will create the "/Individual/<lang>" structure inside.
        {
            if (string.IsNullOrWhiteSpace(characterName)) return false;
            bool success = false;
            try
            {
                var discoveredKeyTextPairs = new Dictionary<string, string>();
                var sourceTracking = new Dictionary<string, string>(); // Optional source tracking

                // --- Load dialogue sources ---
                string dialogueAssetKeyBase = $"Characters/Dialogue/{characterName}";
                string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                string specificDialogueAssetKey = dialogueAssetKeyBase + langSuffix;
                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                    if (dialogueData != null) { foreach (var kvp in dialogueData) { if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Dialogue"; } } }
                }
                catch (ContentLoadException) { /* Ignore */ }
                catch (Exception ex) { Monitor.Log($"Error loading '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); }

                var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent);
                foreach (var kvp in stringCharData) { if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Strings/Characters"; } }
                // TODO: Add other sources if needed

                if (!discoveredKeyTextPairs.Any()) { Monitor.Log($"No keys found for '{characterName}' in '{languageCode}'. Skipping template.", LogLevel.Warn); return false; }

                // --- Create Manifest Structure ---
                var wrapper = new VoicePackWrapperTemplate();
                var manifest = new VoicePackManifestTemplate
                {
                    VoicePackId = $"Vanilla_{characterName}_{languageCode}_Template", // Keep distinct from combined if desired
                    VoicePackName = $"{characterName} - Vanilla Template ({languageCode})",
                    Character = characterName,
                    Language = languageCode
                };

                // *** Initialize counter for this character ***
                int entryNumber = 1;

                foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key)) // Order for consistent numbering
                {
                    string sanitizedKey = SanitizeKeyForFileName(kvp.Key);
                    if (string.IsNullOrWhiteSpace(sanitizedKey) || sanitizedKey == "invalid_key") { continue; } // Skip bad keys

                    // *** Format the filename WITHOUT padding ***
                    string numberedFileName = $"{entryNumber}_{sanitizedKey}.wav";

                    manifest.Entries.Add(new VoiceEntryTemplate
                    {
                        DialogueKey = kvp.Key,
                        DialogueText = SanitizeDialogueText(kvp.Value),
                        DialogueFrom = sourceTracking.TryGetValue(kvp.Key, out var source) ? source : "Unknown", // Added source here too
                                                                                                                 // *** Use the numbered filename in the path ***
                                                                                                                 // Asset path structure within this INDIVIDUAL template's context
                        AudioPath = PathUtilities.NormalizePath($"assets/{characterName}/{numberedFileName}") // Simpler path for individual pack
                    });

                    // *** Increment counter ***
                    entryNumber++;
                }

                wrapper.VoicePacks.Add(manifest);
                string jsonOutput = JsonConvert.SerializeObject(wrapper, Formatting.Indented);

                // --- Define Output Paths for INDIVIDUAL template ---
                string individualOutputDir = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "Individual")); // Specific subfolder
                string langSpecificDir = PathUtilities.NormalizePath(Path.Combine(individualOutputDir, languageCode));
                string templateJsonPath = PathUtilities.NormalizePath(Path.Combine(langSpecificDir, $"{characterName}_VoicePack_Template.json"));
                // Asset path relative to the individual template's JSON file location
                string assetsPath = PathUtilities.NormalizePath(Path.Combine(langSpecificDir, "assets", characterName));

                // --- Save JSON File & Create Folders ---
                Directory.CreateDirectory(langSpecificDir); // Ensure .../Individual/<lang>/ exists
                Directory.CreateDirectory(assetsPath); // Ensure .../Individual/<lang>/assets/<charname>/ exists
                File.WriteAllText(templateJsonPath, jsonOutput);

                Monitor.Log($"Successfully generated INDIVIDUAL template for {characterName} ({languageCode})!", LogLevel.Info);
                Monitor.Log($"Saved JSON to: {templateJsonPath}", LogLevel.Info);
                Monitor.Log($"Created asset folder structure at: {assetsPath}", LogLevel.Info);
                success = true;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to generate template for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Error);
                success = false;
            }
            return success;
        }


        // --- Get Vanilla Character Strings Helper ---
        // *** FIXED: Add default return path ***
        private Dictionary<string, string> GetVanillaCharacterStringKeys(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var keyTextPairs = new Dictionary<string, string>();
            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string assetKey = isEnglish ? "Strings/Characters" : $"Strings/Characters.{languageCode}";

            try
            {
                var characterStrings = gameContent.Load<Dictionary<string, string>>(assetKey);
                if (characterStrings != null) // Check for null after load
                {
                    string prefix = characterName + "_"; string marriagePrefix = "MarriageDialogue." + characterName + "_";
                    foreach (var kvp in characterStrings)
                    { if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || kvp.Key.StartsWith(marriagePrefix, StringComparison.OrdinalIgnoreCase)) { keyTextPairs[kvp.Key] = kvp.Value; } }
                }
                else { Monitor.Log($"'{assetKey}' loaded as null!", LogLevel.Warn); }
            }
            catch (ContentLoadException) { Monitor.Log($"Asset '{assetKey}' not found.", LogLevel.Trace); }
            catch (Exception ex) { Monitor.Log($"Error loading/parsing {assetKey}: {ex.Message}", LogLevel.Error); Monitor.Log(ex.ToString(), LogLevel.Trace); }
            return keyTextPairs; // Always return the dictionary
        }


        // --- List Characters Command Handler ---
        private void ListCharactersCommand(string command, string[] args) { /* ... as before, using IsKnownVanillaVillager ... */ }


        // --- Utility Helpers ---
        // *** FIXED: Add default return path ***
        private string SanitizeKeyForFileName(string key)
        {
            // Keep existing sanitization logic
            if (string.IsNullOrWhiteSpace(key)) return "invalid_key"; // Handle null/whitespace early
            string originalKey = key; // Keep original for logging if needed

            key = key.Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace(" ", "_").Replace(".", "_");
            key = Regex.Replace(key, @"[^\w\-]", ""); // Allow word chars, underscore, hyphen

            // Limit length
            const int MaxLength = 60;
            if (key.Length > MaxLength) key = key.Substring(0, MaxLength);

            // Ensure not empty after sanitization
            if (string.IsNullOrWhiteSpace(key))
            {
                Monitor.Log($"Sanitization resulted in empty string for key: '{originalKey}'. Using 'invalid_key'.", LogLevel.Warn);
                key = "invalid_key";
            }
            return key; // Always return a string
        }

        // *** FIXED: Add default return path ***
        private bool IsKnownVanillaVillager(string name)
        {
            // Keep existing HashSet logic
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey", "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane",
                "Caroline", "Clint", "Demetrius", "Evelyn", "George", "Gus", "Jas", "Jodi", "Kent", "Lewis", "Linus", "Marnie",
                "Pam", "Pierre", "Robin", "Vincent", "Willy", "Wizard", "Krobus", "Dwarf", "Sandy", "Leo" };
            return known.Contains(name); // Directly return the result of Contains
        }

        // *** FIXED: Add default return path ***
        private string GetValidatedLanguageCode(string requestedLang)
        {
            // Keep existing dictionary logic
            var langMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "en", "en" }, { "english", "en" }, { "es", "es-ES" }, { "spanish", "es-ES" }, { "zh", "zh-CN" }, { "chinese", "zh-CN" }, { "ja", "ja-JP" }, { "japanese", "ja-JP" }, { "pt", "pt-BR" }, { "portuguese", "pt-BR" }, { "fr", "fr-FR" }, { "french", "fr-FR" }, { "ko", "ko-KR" }, { "korean", "ko-KR" }, { "it", "it-IT" }, { "italian", "it-IT" }, { "de", "de-DE" }, { "german", "de-DE" }, { "hu", "hu-HU" }, { "hungarian", "hu-HU" }, { "ru", "ru-RU" }, { "russian", "ru-RU" }, { "tr", "tr-TR" }, { "turkish", "tr-TR" } };
            // Ensure reverse mapping exists (value -> value)
            foreach (var kvp in langMap.ToList()) { if (!langMap.ContainsKey(kvp.Value)) langMap[kvp.Value] = kvp.Value; }

            if (langMap.TryGetValue(requestedLang, out string stardewCode)) { return stardewCode; }

            this.Monitor.Log($"Language code '{requestedLang}' not explicitly mapped, attempting direct use.", LogLevel.Warn); // Changed log level/message
            // *** FIXED: Return the original request if not found, don't default to 'en' here ***
            // Let the asset loading fail later if it's truly invalid.
            return requestedLang;
        }


        // *** FIXED: Add default return path ***
        private string SanitizeDialogueText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ""; // Return empty if input is empty/whitespace

            // Keep existing regex logic
            text = Regex.Replace(text, @"\$[a-zA-Z]\b", ""); text = Regex.Replace(text, @"%[a-zA-Z]+\b", ""); text = Regex.Replace(text, @"#[^#]+#", ""); text = Regex.Replace(text, @"\^", ""); text = Regex.Replace(text, @"<", ""); text = Regex.Replace(text, @"\\", "");
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();

            return text; // Always return the processed string
        }

    } // End of ModEntry class
} // End of namespace