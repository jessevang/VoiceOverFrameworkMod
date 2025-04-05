// VoiceOverFrameworkMod.cs

// --- Required using statements ---
using System;
using System.Collections.Generic;
using System.IO; // Needed for Path, File, Directory
using System.Linq; // Needed for Linq operations like OrderBy, Any, FirstOrDefault
using System.Text.RegularExpressions; // Needed for SanitizeKeyForFileName
using System.Xml;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json; // *** ADDED: For JSON serialization/deserialization ***
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using VoiceOverFrameworkMod.Menus; // Make sure this namespace matches your Menu folder

namespace VoiceOverFrameworkMod
{
    // --- Supporting Class Definitions ---
    // Structure loaded from config.json
    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public bool FallbackToDefaultIfMissing { get; set; } = true;
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new(); // Maps Character Name -> VoicePackId
    }

    // Structure read from voice pack content.json
    public class VoicePackWrapper
    {
        public List<VoicePackManifest> VoicePacks { get; set; }
    }
    
    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; } // Added for display/GMCM
        public string Character { get; set; }
        public string Language { get; set; }
        public List<VoiceEntry> Entries { get; set; }
    }

    public class VoiceEntry
    {
        public string DialogueKey { get; set; }
        public string AudioPath { get; set; }
    }

    // Internal representation of a loaded voice pack
    public class VoicePack
    {
        public string VoicePackId;
        public string VoicePackName; // Added for display/GMCM
        public string Character;
        public string Language;
        public Dictionary<string, string> Entries; // Maps DialogueKey -> Full Audio File Path
    }

    // Structure for the generated template JSON file
    public class VoicePackWrapperTemplate
    {
        public List<VoicePackManifestTemplate> VoicePacks { get; set; } = new List<VoicePackManifestTemplate>();
    }

    public class VoicePackManifestTemplate
    {
        public string Format { get; set; } = "1.0.0"; // Or your current format version
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; } = "en";
        public List<VoiceEntryTemplate> Entries { get; set; } = new List<VoiceEntryTemplate>();
    }

    public class VoiceEntryTemplate
    {
        public string DialogueKey { get; set; }
        public string AudioPath { get; set; } // Placeholder path
    }


    // --- Main Mod Class ---
    public partial class ModEntry : Mod // Using partial if you split files later, otherwise just 'class ModEntry'
    {
        public static ModEntry Instance;

        private ModConfig Config; // Uses ModConfig class defined above
        private Dictionary<string, string> SelectedVoicePacks; // Loaded from Config
        private Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new(); // Uses VoicePack class defined above

        private string lastDialogueText = null; // For UpdateTicked method (consider replacing with Harmony)
        private string lastSpeakerName = null; // For UpdateTicked method (consider replacing with Harmony)

        private SoundEffectInstance currentVoiceInstance;

        // --- Mod Entry Point ---
        public override void Entry(IModHelper helper)
        {
            Instance = this;
            // I18n.Init(helper.Translation); // Initialize translations - uncomment if needed & I18n class exists

            // --- Config Loading ---
            Config = helper.ReadConfig<ModConfig>(); // Reads into ModConfig type
            SelectedVoicePacks = Config.SelectedVoicePacks;

            // --- Voice Pack Loading ---
            LoadVoicePacks(); // Loads using VoicePackWrapper/Manifest/Entry and populates VoicePack

            // --- Event Listeners ---
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            // --- Console Commands ---
            SetupConsoleCommands(helper.ConsoleCommands);

            Monitor.Log("Voice Over Framework initialized.", LogLevel.Info);
        }

        // --- Event Handlers ---

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // Setup GMCM registration here
        }

        private void LoadVoicePacks()
        {
            VoicePacksByCharacter.Clear();
            var allContentPacks = this.Helper.ContentPacks.GetOwned();
            Monitor.Log($"Scanning {allContentPacks.Count()} content packs for voice data...", LogLevel.Debug);

            foreach (var pack in allContentPacks)
            {
                try
                {
                    // Reads into VoicePackWrapper/Manifest/Entry classes
                    var wrapper = pack.ReadJsonFile<VoicePackWrapper>("content.json");
                    if (wrapper?.VoicePacks == null)
                        continue;

                    Monitor.Log($"Found voice pack definitions in '{pack.Manifest.Name}'.", LogLevel.Trace);
                    foreach (var metadata in wrapper.VoicePacks)
                    {
                        if (string.IsNullOrWhiteSpace(metadata.VoicePackId) ||
                            string.IsNullOrWhiteSpace(metadata.VoicePackName) ||
                            string.IsNullOrWhiteSpace(metadata.Character) ||
                            metadata.Entries == null)
                        {
                            Monitor.Log($"Skipping invalid voice pack entry in '{pack.Manifest.Name}': Missing required fields.", LogLevel.Warn);
                            continue;
                        }

                        // Creates internal VoicePack object
                        var voicePack = new VoicePack
                        {
                            VoicePackId = metadata.VoicePackId,
                            VoicePackName = metadata.VoicePackName,
                            Language = metadata.Language ?? "en",
                            Character = metadata.Character,
                            Entries = metadata.Entries.ToDictionary(
                                e => e.DialogueKey,
                                e => Path.Combine(pack.DirectoryPath, e.AudioPath)
                            )
                        };

                        if (!VoicePacksByCharacter.ContainsKey(voicePack.Character))
                        {
                            VoicePacksByCharacter[voicePack.Character] = new List<VoicePack>();
                        }

                        if (!VoicePacksByCharacter[voicePack.Character].Any(p => p.VoicePackId == voicePack.VoicePackId && p.Language == voicePack.Language))
                        {
                            VoicePacksByCharacter[voicePack.Character].Add(voicePack);
                            Monitor.Log($"Loaded voice pack '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) for {voicePack.Character}.", LogLevel.Trace);
                        }
                        else
                        {
                            Monitor.Log($"Skipping duplicate voice pack ID '{voicePack.VoicePackId}' for {voicePack.Character} in '{pack.Manifest.Name}'.", LogLevel.Warn);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error loading voice pack definition from '{pack.Manifest.Name}': {ex.Message}", LogLevel.Error);
                }
            }
            Monitor.Log($"Finished loading voice packs. Found packs for {VoicePacksByCharacter.Count} unique characters.", LogLevel.Debug);
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // As before, recommend replacing with Harmony
            if (!Context.IsWorldReady || Game1.currentSpeaker == null || !Game1.dialogueUp)
            {
                if (lastDialogueText != null || lastSpeakerName != null)
                {
                    lastDialogueText = null;
                    lastSpeakerName = null;
                }
                return;
            }

            NPC speaker = Game1.currentSpeaker;
            Dialogue currentDialogue = speaker?.CurrentDialogue?.FirstOrDefault();
            string currentText = currentDialogue?.getCurrentDialogue()?.Trim();

            if (string.IsNullOrEmpty(currentText) || speaker.Name == null)
                return;

            if (currentText == lastDialogueText && speaker.Name == lastSpeakerName)
                return;

            lastDialogueText = currentText;
            lastSpeakerName = speaker.Name;

            Monitor.Log($"[UpdateTick] Dialogue detected for {speaker.Name}. Text: '{currentText}'. Cannot play voice without KEY.", LogLevel.Trace);
            // *** Cannot call TryPlayVoice without the Dialogue Key ***
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (e.Button == SButton.F12) // Your test menu key
            {
                if (!VoicePacksByCharacter.Any())
                {
                    Monitor.Log("Cannot open voice test menu - no voice packs loaded.", LogLevel.Warn);
                    Game1.drawObjectDialogue("No voice packs loaded.");
                    return;
                }
                // Ensure VoiceTestMenu takes the correct dictionary type
                Game1.activeClickableMenu = new VoiceTestMenu(VoicePacksByCharacter);
            }
        }

        // --- Core Voice Playback Logic ---
        public void TryPlayVoice(string characterName, string dialogueKey)
        {
            // Uses Config (ModConfig) and VoicePacksByCharacter (Dictionary<string, List<VoicePack>>)
            Monitor.Log($"[Voice] Attempting voice: Char='{characterName}', Key='{dialogueKey}'", LogLevel.Trace);

            if (!Config.SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) || string.IsNullOrEmpty(selectedVoicePackId))
            {
                return;
            }

            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks))
            {
                return;
            }

            string language = Config.DefaultLanguage;
            var selectedPack = availablePacks.FirstOrDefault(p => p.VoicePackId == selectedVoicePackId && p.Language == language);

            if (selectedPack == null && Config.FallbackToDefaultIfMissing && language != "en")
            {
                selectedPack = availablePacks.FirstOrDefault(p => p.VoicePackId == selectedVoicePackId && p.Language == "en");
            }

            if (selectedPack == null)
            {
                Monitor.Log($"Selected voice pack ID='{selectedVoicePackId}' (Lang='{language}') not found among loaded packs for {characterName}.", LogLevel.Warn);
                return;
            }

            if (selectedPack.Entries.TryGetValue(dialogueKey, out string audioPath))
            {
                Monitor.Log($"Found audio for key '{dialogueKey}' in pack '{selectedPack.VoicePackName}': {audioPath}", LogLevel.Trace);
                PlayVoiceFromFile(audioPath);
            }
            // else { Monitor.Log($"No audio entry for key '{dialogueKey}' in pack '{selectedPack.VoicePackName}'", LogLevel.Trace); } // Optional: Log misses
        }

        private void PlayVoiceFromFile(string audioFilePath)
        {
            try
            {
                currentVoiceInstance?.Stop();
                currentVoiceInstance?.Dispose();
                currentVoiceInstance = null;

                if (!File.Exists(audioFilePath))
                {
                    Monitor.Log($"Audio file not found: {audioFilePath}", LogLevel.Warn);
                    return;
                }

                using (var stream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read))
                {
                    SoundEffect sound = SoundEffect.FromStream(stream);
                    currentVoiceInstance = sound.CreateInstance();
                    // Apply volume settings if needed
                    // float volume = Math.Clamp(Game1.options.soundVolumeLevel * Game1.options.masterVolumeLevel, 0f, 1f);
                    // currentVoiceInstance.Volume = volume;
                    currentVoiceInstance.Play();
                    Monitor.Log($"Playing: {Path.GetFileName(audioFilePath)}", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to play audio '{audioFilePath}': {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
                currentVoiceInstance = null;
            }
        }

        // --- Console Command Setup & Implementation ---
        private void SetupConsoleCommands(ICommandHelper commands)
        {
            commands.Add("voice_create_template",
                         "Generates template content.json(s) for vanilla character(s).\n\nUsage:\n  voice_create_template <CharacterName>\n  voice_create_template all\n\nExamples:\n  voice_create_template Harvey\n  voice_create_template all", // Update description
                         this.GenerateTemplateCommand);

            commands.Add("voice_list_chars",
                         "Lists known characters found in Game1.characterData.",
                         this.ListCharactersCommand);
        }


        // --- In GenerateTemplateCommand ---
        private void GenerateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 1)
            {
                this.Monitor.Log("Please provide a character name or 'all'.", LogLevel.Error);
                this.Monitor.Log("Usage: voice_create_template <CharacterName|all>", LogLevel.Info);
                return;
            }

            if (!Context.IsWorldReady) // Still need this check
            {
                this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn);
                return;
            }


            string target = args[0];
            List<string> charactersToProcess = new List<string>();

            if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target == "*")
            {
                this.Monitor.Log("Gathering list of all characters from Game1.characterData...", LogLevel.Info);
                try
                {
                    if (Game1.characterData != null && Game1.characterData.Any())
                    {
                        charactersToProcess = Game1.characterData.Keys
                                                // Optional: Add filtering here to exclude non-villagers if desired
                                                .Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name)) // Example filtering
                                                .OrderBy(name => name)
                                                .ToList();
                        this.Monitor.Log($"Found {charactersToProcess.Count} characters to process.", LogLevel.Info);
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
                    return;
                }
            }
            else
            {
                // Process single character
                charactersToProcess.Add(target);
            }

            // --- Loop through characters and generate template for each ---
            int successCount = 0;
            int failCount = 0;
            foreach (string characterName in charactersToProcess)
            {
                this.Monitor.Log($"--- Processing template for '{characterName}' ---", LogLevel.Info);
                if (GenerateSingleTemplate(characterName)) // Extract generation logic into a new method
                {
                    successCount++;
                }
                else
                {
                    failCount++;
                }
            }

            this.Monitor.Log($"--- Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Successfully generated: {successCount}", LogLevel.Info);
            this.Monitor.Log($"Failed/Skipped: {failCount}", LogLevel.Info);
        }


        // *** NEW METHOD: Extract the single template generation logic ***
        private bool GenerateSingleTemplate(string characterName)
        {
            // Input validation (basic)
            if (string.IsNullOrWhiteSpace(characterName)) return false;
            // Skip known non-dialogue characters if desired
            // if (characterName == "???" || characterName == "Bear"...) return false;


            // Use try-catch specifically for this single character's generation
            try
            {
                var discoveredKeys = new HashSet<string>();

                // --- Load individual Dialogue File (1.6 Primary Method) ---
                string dialogueAssetKey = $"Characters/Dialogue/{characterName}";
                try
                {
                    this.Monitor.Log($"Attempting to load: '{dialogueAssetKey}'...", LogLevel.Trace);
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(dialogueAssetKey);
                    if (dialogueData != null)
                    {
                        discoveredKeys.UnionWith(dialogueData.Keys);
                        this.Monitor.Log($"Loaded {dialogueData.Count} keys from '{dialogueAssetKey}'.", LogLevel.Debug);
                    }
                    else
                    {
                        this.Monitor.Log($"'{dialogueAssetKey}' loaded null.", LogLevel.Trace);
                    }
                }
                catch (Microsoft.Xna.Framework.Content.ContentLoadException)
                {
                    this.Monitor.Log($"Asset '{dialogueAssetKey}' not found.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error loading '{dialogueAssetKey}': {ex.Message}", LogLevel.Warn);
                }


                // --- Check Strings/Characters for additional keys ---
                this.Monitor.Log("Parsing Strings/Characters for additional keys...", LogLevel.Trace);
                discoveredKeys.UnionWith(GetVanillaCharacterStringKeys(characterName, this.Helper.GameContent));


                // --- TODO: Add parsing for Events, Festivals, etc. ---


                this.Monitor.Log($"Found {discoveredKeys.Count} potential keys total for '{characterName}'.", LogLevel.Debug);

                if (!discoveredKeys.Any())
                {
                    this.Monitor.Log($"No keys found for '{characterName}'. Skipping template generation.", LogLevel.Warn);
                    return false; // Indicate failure/skip
                }

                // --- Create and Save Template ---
                var wrapper = new VoicePackWrapperTemplate();
                var manifest = new VoicePackManifestTemplate
                {
                    VoicePackId = $"Vanilla_{characterName}_Template",
                    VoicePackName = $"{characterName} - Vanilla Template",
                    Character = characterName,
                    Language = Config.DefaultLanguage ?? "en"
                };

                foreach (string key in discoveredKeys.OrderBy(k => k))
                {
                    manifest.Entries.Add(new VoiceEntryTemplate
                    {
                        DialogueKey = key,
                        AudioPath = $"assets/{manifest.Language}/{characterName}/{SanitizeKeyForFileName(key)}.wav"
                    });
                }
                wrapper.VoicePacks.Add(manifest);

                string jsonOutput = JsonConvert.SerializeObject(wrapper, Newtonsoft.Json.Formatting.Indented);

                string templateDir = Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates");
                Directory.CreateDirectory(templateDir);
                string outputPath = Path.Combine(templateDir, $"{characterName}_VoicePack_Template.json");

                File.WriteAllText(outputPath, jsonOutput);

                this.Monitor.Log($"Successfully generated template: {outputPath}", LogLevel.Info);
                return true; // Indicate success

            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to generate template for {characterName}: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Error);
                return false; // Indicate failure
            }
        }

        // Keep GetVanillaCharacterStringKeys as it still finds relevant keys
        private HashSet<string> GetVanillaCharacterStringKeys(string characterName, IGameContentHelper gameContent)
        {
            var keys = new HashSet<string>();
            string assetKey = "Strings/Characters";
            try
            {
                var characterStrings = gameContent.Load<Dictionary<string, string>>(assetKey);
                // ... (rest of the function remains the same, searching prefixes) ...
                // Log how many it found specifically from here for debugging
                // this.Monitor.Log($"Found {keys.Count} keys matching prefixes in '{assetKey}' for {characterName}.", LogLevel.Trace);

            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/parsing {assetKey}: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return keys;
        }


        private void ListCharactersCommand(string command, string[] args)
        {
            // *** ADD THIS CHECK ***
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn);
                return; // Exit if no save is loaded
            }

            this.Monitor.Log("Listing characters found in Game1.characterData...", LogLevel.Info);
            try
            {
                // Now this check is less likely to fail, but good for safety
                if (Game1.characterData == null)
                {
                    this.Monitor.Log("Game1.characterData is still null even after save load. This is unexpected.", LogLevel.Error);
                    return;
                }

                // Get the keys (internal character names like "Abigail", "Harvey")
                var characterKeys = Game1.characterData.Keys
                                        .OrderBy(name => name) // Sort by internal name
                                        .ToList();

                // ... (rest of the command logic) ...
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"An error occurred while listing characters from Game1.characterData: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
        }

        // --- Utility Helpers ---
        private string SanitizeKeyForFileName(string key)
        {
            // Uses Regex
            key = key.Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace(" ", "_").Replace(".", "_");
            key = Regex.Replace(key, @"[^\w\-]", "");

            const int MaxLength = 60;
            if (key.Length > MaxLength) key = key.Substring(0, MaxLength);
            if (string.IsNullOrWhiteSpace(key)) key = "invalid_key";
            return key;
        }

        private bool IsKnownVanillaVillager(string name)
        {
            // Uses HashSet
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey", "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane",
                "Caroline", "Clint", "Demetrius", "Evelyn", "George", "Gus", "Jas", "Jodi", "Kent", "Lewis", "Linus", "Marnie",
                "Pam", "Pierre", "Robin", "Vincent", "Willy", "Wizard", "Krobus", "Dwarf", "Sandy", "Leo", "Gunther", "Marlon", "Morris", "Gil"
             };
            return known.Contains(name);
        }

    } // End of ModEntry class

} // End of namespace