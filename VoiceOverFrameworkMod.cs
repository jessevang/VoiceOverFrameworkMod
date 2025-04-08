// VoiceOverFrameworkMod.cs

// --- Required using statements ---
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
        public bool turnoffdialoguetypingsound = true;
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
        public string DialogueText { get; set; }
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
        public string DialogueText { get; set; }
        public string AudioPath { get; set; }
    }


    // --- Main Mod Class ---
    public partial class ModEntry : Mod
    {
        public static ModEntry Instance { get; private set; }
        private bool wasDialogueUpLastTick = false;
        public ModConfig Config { get; private set; }
        private Dictionary<string, string> SelectedVoicePacks;
        private Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new();

        private SoundEffectInstance currentVoiceInstance;
        private string lastDialogueText = null;
        private string lastSpeakerName = null;


        //Different Languages
        private readonly List<string> KnownStardewLanguages = new List<string> {
            "en", "es-ES", "zh-CN", "ja-JP", "pt-BR", "fr-FR", "ko-KR", "it-IT", "de-DE", "hu-HU", "ru-RU", "tr-TR"
        };

        internal NPC CurrentDialogueSpeaker = null;
        internal string CurrentDialogueOriginalKey = null;
        internal int CurrentDialogueTotalPages = 1; // Assume 1 page unless set otherwise
        internal int CurrentDialoguePage { get; set; } = 0; // Current page index (0-based)
        internal bool IsMultiPageDialogueActive { get; set; } = false;

        // --- Mod Entry Point ---
        public override void Entry(IModHelper helper)
        {
            
            this.Monitor.Log("ModEntry.Entry() called.", LogLevel.Debug);
            Instance = this;
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

        // --- Method to Reset Dialogue State ---
        public void ResetDialogueState()
        {
            // Optional: Log when state is reset for debugging
            // this.Monitor?.Log("[ResetDialogueState] Clearing dialogue context.", LogLevel.Trace);

            this.CurrentDialogueSpeaker = null;
            this.CurrentDialogueOriginalKey = null;
            this.CurrentDialogueTotalPages = 1;
            this.CurrentDialoguePage = 0;
            this.IsMultiPageDialogueActive = false;
        }

        // Make sure ApplyHarmonyPatches is called in your Entry method
        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);

                this.Monitor.Log("Applying Harmony patches...", LogLevel.Debug);
                // This will patch all classes/methods tagged with Harmony attributes in your assembly
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                this.Monitor.Log("Harmony patches applied successfully.", LogLevel.Debug);


            MuteTypingSoundPatch.ApplyPatch(harmony);

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
                                        .Where(e => !string.IsNullOrWhiteSpace(e?.DialogueText) && !string.IsNullOrWhiteSpace(e?.AudioPath)) // Filter bad entries
                                        .ToDictionary(e => e.DialogueText, e => PathUtilities.NormalizePath(Path.Combine(pack.DirectoryPath, e.AudioPath)));


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

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e) 
        {


            if (!e.IsMultipleOf(1))
                return;

            CheckForDialogue();


            // --- Logic to detect dialogue closure using Game1.dialogueUp ---
            try
            {
                bool isDialogueUpNow = Game1.dialogueUp;

                // Check if the dialogue was up last tick but isn't up now
                if (this.wasDialogueUpLastTick && !isDialogueUpNow)
                {
                    this.Monitor?.Log("[UpdateTicked] Detected Game1.dialogueUp is now false. Resetting framework state.", LogLevel.Debug);
                    this.ResetDialogueState(); // Call your existing reset method

                }

                // Update the tracker for the next tick
                this.wasDialogueUpLastTick = isDialogueUpNow;
            }
            catch (Exception ex)
            {
                this.Monitor?.Log($"Error in OnUpdateTicked: {ex.Message}", LogLevel.Error);
            }



        }

        private void CheckForDialogue()
        {
            if (Game1.activeClickableMenu is DialogueBox dialogueBox)
            {
                string current = dialogueBox.getCurrentString();
                if (!string.IsNullOrWhiteSpace(current) && current != lastDialogueText)
                {
                    lastDialogueText = current;

                    NPC speaker = Game1.currentSpeaker;
                    if (speaker != null)
                    {
                        Monitor.Log($"[Voice] {speaker.Name} says: {current}", LogLevel.Debug);
                        TryToPlayVoice(speaker.Name, SanitizeDialogueText(current));
                    }
                }
            }
            else
            {
                lastDialogueText = null; // Reset when no dialogue
            }
        }



        private void OnButtonPressed(object sender, ButtonPressedEventArgs e) { /* ... as before ... */ }

        // --- Core Voice Playback Logic ---
        // Inside ModEntry class

        public void TryToPlayVoice(string characterName, string dialogueText)
        {
            if (Config == null || SelectedVoicePacks == null)
            {
                Monitor.LogOnce("Config or SelectedVoicePacks is null in TryPlayVoice. Cannot proceed.", LogLevel.Warn);
                return;
            }

            // Log the initial attempt with more context
            Monitor.Log($"[TryPlayVoice] Attempting voice lookup: Char='{characterName}', Text='{dialogueText}'", LogLevel.Trace);

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
            if (selectedPack.Entries.TryGetValue(dialogueText, out string audioPath))
            {
                Monitor.Log($"[TryPlayVoice] SUCCESS: Found path: '{audioPath}'", LogLevel.Debug);
                // *** LOG SUCCESSFUL LOOKUP ***
                Monitor.Log($"[TryPlayVoice] SUCCESS: Found path for dialogue text '{dialogueText}' in pack '{selectedPack.VoicePackName}'. Path: '{audioPath}'", LogLevel.Debug);
                PlayVoiceFromFile(audioPath); // Call the playback method
            }
            else
            {
                // *** LOG FAILED LOOKUP ***
                Monitor.Log($"[TryPlayVoice] FAILED: Dialogue text '{dialogueText}' not found within the 'Entries' of selected pack '{selectedPack.VoicePackName}' (Lang: '{selectedPack.Language}').", LogLevel.Debug); // Changed to Debug as this might be common/expected
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
                languagesToProcess.AddRange(this.KnownStardewLanguages); // Assumes KnownStardewLanguages exists
                this.Monitor.Log($"Processing combined template for ALL {languagesToProcess.Count} known languages.", LogLevel.Info);
            }
            else
            {
                string validatedLang = GetValidatedLanguageCode(targetLanguageArg); // Assumes GetValidatedLanguageCode exists
                languagesToProcess.Add(validatedLang);
                this.Monitor.Log($"Processing combined template for language: {validatedLang}", LogLevel.Info);
            }
            if (!languagesToProcess.Any())
            {
                this.Monitor.Log("No valid languages determined for processing. Aborting.", LogLevel.Error);
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
                                            .Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name)) // Assumes IsKnownVanillaVillager exists
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
            string baseOutputDir = PathUtilities.NormalizePath(Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates", "Combined")); // Assumes PathUtilities exists
            this.Monitor.Log($"Base output directory: {baseOutputDir}", LogLevel.Trace);

            foreach (string languageCode in languagesToProcess)
            {
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                var languageWrapper = new VoicePackWrapperTemplate(); // Assumes VoicePackWrapperTemplate class exists
                int charactersAddedToThisFile = 0;
                var assetFoldersToCreate = new HashSet<string>(); // Collect asset folder paths to create later

                // --- Loop through CHARACTERS for this language ---
                foreach (string characterName in charactersToProcess)
                {
                    this.Monitor.Log($"Gathering data for '{characterName}' ({languageCode})...", LogLevel.Trace);
                    var discoveredKeyTextPairs = new Dictionary<string, string>();
                    var sourceTracking = new Dictionary<string, string>(); // Track where each KEY came from

                    try // Try block for processing a single character
                    {
                        // --- Load dialogue sources ---
                        string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";

                        // 1. Individual Dialogue File (Primary source)
                        string specificDialogueAssetKey = $"Characters/Dialogue/{characterName}{langSuffix}";
                        try
                        {
                            var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                            if (dialogueData != null)
                            {
                                foreach (var kvp in dialogueData)
                                {
                                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                                    {
                                        if (!discoveredKeyTextPairs.ContainsKey(kvp.Key))
                                        {
                                            discoveredKeyTextPairs[kvp.Key] = kvp.Value;
                                            sourceTracking[kvp.Key] = "Dialogue";
                                        }
                                    }
                                }
                                this.Monitor.Log($"Loaded {dialogueData.Count} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace);
                            }
                        }
                        catch (ContentLoadException) { this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found or invalid.", LogLevel.Trace); }
                        catch (Exception ex) { this.Monitor.Log($" Error loading '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); }

                        // 2. Strings/Characters (Secondary source)
                        var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent); // Assumes helper exists
                        this.Monitor.Log($"Found {stringCharData.Count} potential entries from Strings/Characters for {characterName}.", LogLevel.Trace);
                        foreach (var kvp in stringCharData)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                if (!discoveredKeyTextPairs.ContainsKey(kvp.Key))
                                {
                                    discoveredKeyTextPairs[kvp.Key] = kvp.Value;
                                    sourceTracking[kvp.Key] = "Strings/Characters";
                                }
                            }
                        }

                        // --- TODO: Add Mail/Quest/Event parsing here ---

                        if (!discoveredKeyTextPairs.Any())
                        {
                            this.Monitor.Log($" No keys found for '{characterName}' in {languageCode}. Skipping character.", LogLevel.Trace);
                            continue; // Skip to the next character
                        }
                        this.Monitor.Log($"Found {discoveredKeyTextPairs.Count} total unique keys for '{characterName}' in {languageCode}.", LogLevel.Debug);

                        // --- Create Manifest FOR THIS CHARACTER ---
                        var characterManifest = new VoicePackManifestTemplate // Assumes class exists
                        {
                            VoicePackId = $"YourModID.Vanilla_{characterName}_{languageCode}", // Change YourModID!
                            VoicePackName = $"{characterName} - Vanilla ({languageCode})",
                            Character = characterName,
                            Language = languageCode
                        };

                        int entryNumber = 1; // Initialize counter for this character's audio files

                        // --- Process and Split Dialogue Lines ---
                        this.Monitor.Log($"Processing keys for {characterName} ({languageCode})...", LogLevel.Trace);
                        foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key)) // Order for consistency
                        {
                            string originalKey = kvp.Key;
                            string originalValue = kvp.Value;
                            string source = sourceTracking.TryGetValue(originalKey, out var src) ? src : "Unknown";

                            // --- DEBUG LOGGING: Before Split ---
                            this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Original Value: \"{originalValue}\"", LogLevel.Trace);

                            // *** SPLITTING LOGIC ***
                            string[] splitSegments = originalValue.Split(new[] { "#$b#" }, StringSplitOptions.RemoveEmptyEntries);

                            // --- DEBUG LOGGING: After Split ---
                            this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Split Count: {splitSegments.Length}", LogLevel.Trace);
                            if (splitSegments.Length == 0 && originalValue.Contains("#$b#"))
                            {
                                // Log if #$b# was present but resulted in zero non-empty segments
                                this.Monitor.Log($"[DEBUG] Key: '{originalKey}' contained #$b# but resulted in 0 non-empty segments.", LogLevel.Warn);
                            }

                            // Process each resulting segment
                            if (splitSegments.Length > 0) // Only loop if there are segments
                            {
                                for (int i = 0; i < splitSegments.Length; i++) // Use index for clearer logging
                                {
                                    string part = splitSegments[i];

                                    // --- DEBUG LOGGING: Inside Part Loop ---
                                    this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Processing Part {i + 1}/{splitSegments.Length}: \"{part}\"", LogLevel.Trace);

                                    string cleanedPart = SanitizeDialogueText(part); // Assumes SanitizeDialogueText exists

                                    if (string.IsNullOrWhiteSpace(cleanedPart))
                                    {
                                        this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Part {i + 1} was empty after cleaning. Skipping.", LogLevel.Trace);
                                        continue; // Skip this specific empty part
                                    }

                                    string numberedFileName = $"{entryNumber}.wav";
                                    string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace("\\", "/");

                                    // Add a VoiceEntryTemplate for THIS segment
                                    var newEntry = new VoiceEntryTemplate // Assumes class exists
                                    {
                                        DialogueText = cleanedPart,
                                        DialogueFrom = source,
                                        AudioPath = relativeAudioPath
                                    };
                                    characterManifest.Entries.Add(newEntry);

                                    // --- DEBUG LOGGING: Entry Added ---
                                    this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Added Entry {entryNumber}: Path='{newEntry.AudioPath}', Text='{newEntry.DialogueText}'", LogLevel.Trace);

                                    // Increment the counter for the NEXT audio file
                                    entryNumber++;
                                } // --- End loop through split parts ---
                            }
                            else if (!string.IsNullOrWhiteSpace(originalValue)) // Handle case where split results in zero segments but original wasn't empty
                            {
                                this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Original value was not empty but resulted in 0 split segments. No entries added for this key.", LogLevel.Trace);
                            }
                            else // Handle case where original value was empty/whitespace
                            {
                                this.Monitor.Log($"[DEBUG] Key: '{originalKey}', Original value was empty or whitespace. No entries added.", LogLevel.Trace);
                            }

                        } // --- End loop through discovered keys (kvp) for this character ---

                        // Add character manifest to wrapper if it has entries
                        if (characterManifest.Entries.Any())
                        {
                            languageWrapper.VoicePacks.Add(characterManifest);
                            charactersAddedToThisFile++;
                            string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "assets", languageCode, characterName));
                            assetFoldersToCreate.Add(assetsCharacterPath);
                            this.Monitor.Log($"Added manifest for {characterName} ({languageCode}) with {characterManifest.Entries.Count} entries.", LogLevel.Trace);
                        }
                        else
                        {
                            this.Monitor.Log($"No valid dialogue entries generated for '{characterName}' ({languageCode}). Not adding manifest.", LogLevel.Debug);
                        }

                    } // End try block for processing a single character
                    catch (Exception ex)
                    {
                        // Catch errors during processing for a specific character and log
                        this.Monitor.Log($"Failed during data processing for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                        this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                        // Continue to the next character even if one fails
                    }
                } // --- End Character Loop ---

                // --- Save the COMBINED File for this Language ---
                this.Monitor.Log($"Finished processing characters for {languageCode}. Adding manifests for {charactersAddedToThisFile} characters to the combined file.", LogLevel.Debug);
                if (charactersAddedToThisFile > 0 && languageWrapper.VoicePacks.Any())
                {
                    try // Try block for saving the combined file
                    {
                        this.Monitor.Log($"Ensuring base output directory exists: {baseOutputDir}", LogLevel.Trace);
                        Directory.CreateDirectory(baseOutputDir); // Create base output dir if needed

                        // Serialize the entire wrapper
                        string jsonOutput = JsonConvert.SerializeObject(languageWrapper, Formatting.Indented, new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore // Cleaner JSON
                        });
                        string outputFilename = $"all_characters_{languageCode}_template.json";
                        string outputPath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, outputFilename));

                        this.Monitor.Log($"Attempting to write COMBINED template JSON for {languageCode} to: {outputPath}", LogLevel.Info);
                        File.WriteAllText(outputPath, jsonOutput);

                        // Verify file exists
                        bool fileExists = File.Exists(outputPath);
                        if (fileExists)
                        {
                            this.Monitor.Log($"Successfully generated COMBINED template for {languageCode}.", LogLevel.Info);
                            totalFilesGenerated++;

                            // --- Create ALL necessary asset folders ---
                            this.Monitor.Log($"Creating required asset folder structures ({assetFoldersToCreate.Count} needed)...", LogLevel.Debug);
                            int foldersCreated = 0;
                            string languageAssetBasePath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "assets", languageCode));
                            foreach (string assetPath in assetFoldersToCreate) // Iterate collected paths
                            {
                                try
                                {
                                    Directory.CreateDirectory(assetPath);
                                    foldersCreated++;
                                }
                                catch (Exception dirEx)
                                { this.Monitor.Log($" Error creating asset directory '{assetPath}': {dirEx.Message}", LogLevel.Error); }
                            }
                            this.Monitor.Log($"Created {foldersCreated} asset folder structures under '{languageAssetBasePath}'.", LogLevel.Info);
                        }
                        else
                        {
                            this.Monitor.Log($"Write operation for JSON {languageCode} completed BUT File.Exists returned false! Check permissions/path: {outputPath}", LogLevel.Error);
                        }
                    } // End try block for saving
                      // Specific exception handling for file operations
                    catch (UnauthorizedAccessException ex) { this.Monitor.Log($"PERMISSION ERROR saving combined template for {languageCode}: {ex.Message}. Check folder permissions for '{baseOutputDir}'.", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
                    catch (IOException ex) { this.Monitor.Log($"IO ERROR saving combined template for {languageCode}: {ex.Message}. Is the file open or path invalid?", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
                    catch (JsonException ex) { this.Monitor.Log($"JSON SERIALIZATION ERROR for {languageCode}: {ex.Message}", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
                    catch (Exception ex) // Catch-all for unexpected errors during save
                    {
                        this.Monitor.Log($"Failed to save combined template for {languageCode}: {ex.GetType().Name} - {ex.Message}", LogLevel.Error);
                        this.Monitor.Log(ex.ToString(), LogLevel.Error);
                    }
                }
                else // Case where no characters had processable dialogue for this language
                {
                    this.Monitor.Log($"Skipping combined file generation for {languageCode} as no characters had processable dialogue entries.", LogLevel.Warn);
                }

            } // --- End Language Loop ---

            // --- Final Summary ---
            this.Monitor.Log($"--- Overall Combined Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Total COMBINED files generated: {totalFilesGenerated}", LogLevel.Info);

        } // --- End GenerateCombinedTemplateCommand ---

        // --- Generate Individual Templates Command Handler ---
        private void GenerateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 1)
            {
                this.Monitor.Log("Please provide a character name or 'all', and optionally a language code or 'all'.", LogLevel.Error);
                this.Monitor.Log("Usage: voice_create_template <CharacterName|all> [LanguageCode|all]", LogLevel.Info);
                return;
            }
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn);
                return;
            }

            string targetCharacterArg = args[0];
            // Use configured default language or 'en' if not specified or config is null
            string targetLanguageArg = (args.Length > 1) ? args[1] : (Config?.DefaultLanguage ?? "en");

            List<string> languagesToProcess = new List<string>();
            List<string> charactersToProcess = new List<string>();

            // Determine Languages
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            {
                languagesToProcess.AddRange(this.KnownStardewLanguages); // Assumes KnownStardewLanguages exists
                this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info);
            }
            else
            {
                languagesToProcess.Add(GetValidatedLanguageCode(targetLanguageArg)); // Assumes GetValidatedLanguageCode exists
                this.Monitor.Log($"Processing for language: {languagesToProcess[0]}", LogLevel.Info);
            }
            // Add check if languagesToProcess is empty after validation (e.g., if GetValidatedLanguageCode could return null/empty)
            if (!languagesToProcess.Any() || languagesToProcess.Any(string.IsNullOrWhiteSpace))
            {
                this.Monitor.Log("No valid languages specified or determined.", LogLevel.Error);
                return;
            }


            // Determine Characters
            if (targetCharacterArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetCharacterArg == "*")
            {
                this.Monitor.Log("Gathering list of known vanilla villagers...", LogLevel.Info); // Changed log msg slightly
                try
                {
                    if (Game1.characterData != null && Game1.characterData.Any())
                    {
                        // Use the IsKnownVanillaVillager filter here (assumes method exists)
                        charactersToProcess = Game1.characterData.Keys
                                                .Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name))
                                                .OrderBy(name => name)
                                                .ToList();
                        this.Monitor.Log($"Found {charactersToProcess.Count} known characters to process.", LogLevel.Info);
                    }
                    else
                    {
                        this.Monitor.Log("Game1.characterData is null or empty. Cannot process 'all'.", LogLevel.Error);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error retrieving character list: {ex.Message}", LogLevel.Error);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                    return;
                }
            }
            else
            {
                // Allow processing even if not in the IsKnownVanillaVillager list when specified directly
                charactersToProcess.Add(targetCharacterArg);
                this.Monitor.Log($"Processing for specified character: {targetCharacterArg}", LogLevel.Info);
            }
            // Add check if charactersToProcess is empty
            if (!charactersToProcess.Any() || charactersToProcess.Any(string.IsNullOrWhiteSpace))
            {
                this.Monitor.Log("No characters specified or found to process.", LogLevel.Error);
                return;
            }


            // --- Execution Loop ---
            int totalSuccessCount = 0;
            int totalFailCount = 0;
            // Base directory for *all* generated templates
            string baseTemplateDir = PathUtilities.NormalizePath(Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates"));
            // Subdirectory specifically for individual templates (where GenerateSingleTemplate will save)
            string individualTemplateDir = PathUtilities.NormalizePath(Path.Combine(baseTemplateDir, "Individual"));

            this.Monitor.Log($"Output directory for individual templates: {individualTemplateDir}", LogLevel.Debug);

            foreach (string languageCode in languagesToProcess)
            {
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                int langSuccessCount = 0;
                int langFailCount = 0;
                foreach (string characterName in charactersToProcess)
                {
                    // --- Call the function that DOES the real work (including splitting) ---
                    // Pass the specific subdirectory for individual templates
                    // GenerateSingleTemplate MUST contain the '##' splitting logic
                    if (GenerateSingleTemplate(characterName, languageCode, individualTemplateDir)) // Assumes GenerateSingleTemplate exists and returns bool
                    {
                        langSuccessCount++;
                    }
                    else
                    {
                        langFailCount++;
                    }
                    // --- End of call to the worker function ---
                }
                // Log language summary with appropriate level based on failures
                this.Monitor.Log($"Language {languageCode} Summary - Generated: {langSuccessCount}, Failed/Skipped: {langFailCount}", langFailCount > 0 ? LogLevel.Warn : LogLevel.Info);
                totalSuccessCount += langSuccessCount;
                totalFailCount += langFailCount;
            }

            // --- Final Summary ---
            this.Monitor.Log($"--- Overall Individual Template Generation Complete ---", LogLevel.Info); // Specify "Individual"
            this.Monitor.Log($"Total Successfully generated: {totalSuccessCount}", LogLevel.Info);
            // Only log failures if there were any
            if (totalFailCount > 0)
                this.Monitor.Log($"Total Failed/Skipped: {totalFailCount}", LogLevel.Warn);
        }

        // --- Generate Single Template File (Helper) ---
        private bool GenerateSingleTemplate(string characterName, string languageCode, string baseOutputDir)
        {
            this.Monitor.Log($"Generating template for '{characterName}' ({languageCode}).", LogLevel.Trace); // Normal log level

            var discoveredKeyTextPairs = new Dictionary<string, string>();
            var sourceTracking = new Dictionary<string, string>();

            try
            {
                // --- Load Dialogue Sources ---
                string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
                string specificDialogueAssetKey = $"Characters/Dialogue/{characterName}{langSuffix}";
                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(specificDialogueAssetKey);
                    if (dialogueData != null) { foreach (var kvp in dialogueData) if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Dialogue"; } }
                    this.Monitor.Log($"Loaded {dialogueData?.Count ?? 0} entries from '{specificDialogueAssetKey}'.", LogLevel.Trace);
                }
                catch (ContentLoadException) { this.Monitor.Log($"Asset '{specificDialogueAssetKey}' not found.", LogLevel.Trace); }
                catch (Exception ex) { this.Monitor.Log($" Error loading '{specificDialogueAssetKey}': {ex.Message}", LogLevel.Warn); }
                var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent); // Assumes helper exists
                this.Monitor.Log($"Found {stringCharData.Count} potential entries from Strings/Characters.", LogLevel.Trace);
                foreach (var kvp in stringCharData) if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !discoveredKeyTextPairs.ContainsKey(kvp.Key)) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; sourceTracking[kvp.Key] = "Strings/Characters"; }

                if (!discoveredKeyTextPairs.Any()) { this.Monitor.Log("No keys found. Skipping.", LogLevel.Debug); return false; }
                this.Monitor.Log($"Processing {discoveredKeyTextPairs.Count} unique keys for '{characterName}' ({languageCode}).", LogLevel.Trace);

                // --- Create Manifest ---
                var characterManifest = new VoicePackManifestTemplate { /* ... Set properties ... */ };
                characterManifest.VoicePackId = $"YourModID.Vanilla_{characterName}_{languageCode}_Template"; // Change YourModID!
                characterManifest.VoicePackName = $"{characterName} - Vanilla Template ({languageCode})";
                characterManifest.Character = characterName;
                characterManifest.Language = languageCode;
                // Optional: Add Format field if used
                // characterManifest.Format = "1.0.0";

                int entryNumber = 1;

                // --- Loop, SPLIT, create entries ---
                foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key))
                {
                    string originalKey = kvp.Key;
                    string originalValue = kvp.Value;
                    string source = sourceTracking.TryGetValue(originalKey, out var src) ? src : "Unknown";

                    this.Monitor.Log($"Processing Key: '{originalKey}', Value: \"{originalValue}\"", LogLevel.Trace);

                    // *** CORRECTED SPLITTING LOGIC: Use the actual game separator ***
                    string[] splitSegments = originalValue.Split(new[] { "#$b#" }, StringSplitOptions.RemoveEmptyEntries); // <--- CHANGE IS HERE

                    this.Monitor.Log($" -> Split into {splitSegments.Length} segment(s) using '#$b#'.", LogLevel.Trace);


                    for (int i = 0; i < splitSegments.Length; i++)
                    {
                        string part = splitSegments[i];
                        this.Monitor.Log($"   -> Raw Part {i + 1}: \"{part}\"", LogLevel.Trace);

                        // *** Restore SanitizeDialogueText (assuming it mainly trims/cleans codes) ***
                        // If issues persist, this function is the next suspect.
                        string cleanedPart = SanitizeDialogueText(part);
                        this.Monitor.Log($"   -> Part {i + 1} after Sanitize: \"{cleanedPart}\"", LogLevel.Trace);


                        if (string.IsNullOrWhiteSpace(cleanedPart))
                        {
                            this.Monitor.Log($"      -> Skipping empty part {i + 1}.", LogLevel.Trace);
                            continue;
                        }

                        string numberedFileName = $"{entryNumber}.wav";
                        string relativeAudioPath = Path.Combine("assets", languageCode, characterName, numberedFileName).Replace("\\", "/");

                        // Add entry for THIS part
                        var newEntry = new VoiceEntryTemplate { DialogueText = cleanedPart, DialogueFrom = source, AudioPath = relativeAudioPath };
                        characterManifest.Entries.Add(newEntry);
                        this.Monitor.Log($"      -> Added Entry {entryNumber}. Text: \"{newEntry.DialogueText}\"", LogLevel.Trace);

                        entryNumber++;
                    }
                } // --- End loop through keys ---

                if (!characterManifest.Entries.Any()) { this.Monitor.Log($"No entries generated for {characterName}.", LogLevel.Debug); return false; }

                // --- Save the INDIVIDUAL Manifest File ---
                string languageSubDir = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, languageCode));
                Directory.CreateDirectory(languageSubDir);
                string sanitizedCharName = (SanitizeKeyForFileName(characterName) ?? characterName.Replace(" ", "_")); // Assumes helper exists
                string filename = $"{sanitizedCharName}_{languageCode}_template.json";
                string outputPath = PathUtilities.NormalizePath(Path.Combine(languageSubDir, filename));

                this.Monitor.Log($"Attempting to save JSON to: {outputPath}", LogLevel.Debug);
                string jsonOutput = JsonConvert.SerializeObject(characterManifest, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.WriteAllText(outputPath, jsonOutput);

                if (!File.Exists(outputPath)) { this.Monitor.Log($"Failed to verify save: {outputPath}", LogLevel.Error); return false; }
                this.Monitor.Log($"Success! Saved JSON to: {outputPath}", LogLevel.Info);


                // --- Create Asset Folder ---
                string assetsCharacterPath = PathUtilities.NormalizePath(Path.Combine(baseOutputDir, "assets", languageCode, characterName));
                Directory.CreateDirectory(assetsCharacterPath);
                this.Monitor.Log($"Created asset folder at: {assetsCharacterPath}", LogLevel.Debug);

                return true; // Success
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"ERROR in GenerateSingleTemplate for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                this.Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Trace);
                return false; // Failure
            }
        }



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
        private void ListCharactersCommand(string command, string[] args) { 
        
        
        
        }


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