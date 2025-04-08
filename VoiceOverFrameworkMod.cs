using System; // Added for StringComparer
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using GenericModConfigMenu;
using HarmonyLib; // Keep for ApplyHarmonyPatches if it stays here
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;


namespace VoiceOverFrameworkMod
{
    // Main entry point for the mod. Responsibilities split into partial classes:
    // - ModEntry.Loading.cs: Handles loading voice pack data.
    // - ModEntry.Dialogue.cs: Handles detecting and processing dialogue events.
    // - ModEntry.Playback.cs: Handles finding and playing audio files.
    // - ModEntry.Commands.cs: Handles console command registration and execution.
    // - ModEntry.Utilities.cs: Contains helper methods for sanitization, validation, etc.
    // - Models.cs: Contains data structure definitions (ModConfig, VoicePack, etc.).
    public partial class ModEntry : Mod
    {
        // --- Core Properties ---
        public static ModEntry Instance { get; private set; }
        public ModConfig Config { get; private set; }

        // Stores the user's selection (Character Name -> Selected VoicePackId) from config/GMCM
        private Dictionary<string, string> SelectedVoicePacks = new(StringComparer.OrdinalIgnoreCase);

        //Events List
        private readonly List<string> CommonEventFileNames = new List<string> {
            "AbigailVisits", "Farm", "Town", "Mountain", "Forest", "Beach", "Mine", "Railroad",
            "AdventureGuild", "ArchaeologyHouse", "BathHouse_Entry", "BathHouse_Pool", "BathHouse_WomensLocker", "BathHouse_MensLocker",
            "Blacksmith", "CommunityCenter", "FishShop", "HarveyRoom", "Hospital", "JoshHouse", "JojaMart",
            "LeahHouse", "ManorHouse", "Saloon", "SamHouse", "SandyHouse", "ScienceHouse", "SeedShop",
            "SebastianRoom", "Sewer", "SkullCave", "Trailer", "WizardHouse", "Woods", "ElliottHouse",
            // Ginger Island locations often have events too
            "Island_E", "Island_W", "Island_N", "Island_S", "Island_SE", "Island_FieldOffice", "IslandFarmhouse", "IslandHut",
            "IslandShrine", "IslandSouthEastCave", "IslandWestCave1", "VolcanoDungeon"
            // Add more known event file names if necessary
        };


        // --- Mod Entry Point ---
        public override void Entry(IModHelper helper)
        {
            Instance = this; // Set static instance for easy access

            // Load configuration
            this.Config = helper.ReadConfig<ModConfig>();
            // Initialize SelectedVoicePacks from loaded config, ensuring it's not null
            this.SelectedVoicePacks = this.Config?.SelectedVoicePacks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.Monitor.Log("Configuration loaded.", LogLevel.Debug);

            // Load voice pack definitions from content packs
            // This method is defined in ModEntry.Loading.cs
            LoadVoicePacks();

            // Apply Harmony patches
            // This method is defined below (or could be moved to ModEntry.Harmony.cs)
            ApplyHarmonyPatches();

            // Register event listeners
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked; // Handler in ModEntry.Dialogue.cs
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched; // Handler below (for GMCM)
            // Add other necessary event listeners (e.g., SaveLoaded if config needs reload, Content Events if needed)
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded; // Example: Reload config per save

            // Setup Console Commands
            // This method is defined in ModEntry.Commands.cs
            SetupConsoleCommands(helper.ConsoleCommands);


            Monitor.Log($"{this.ModManifest.Name} {this.ModManifest.Version} initialized.", LogLevel.Info);
        }


        /// <summary>
        /// Extracts dialogue lines spoken by a specific character from common event files for a given language.
        /// </summary>
        /// <param name="targetCharacterName">The name of the character whose dialogue to extract.</param>
        /// <param name="languageCode">The language code (e.g., "en", "es-ES").</param>
        /// <param name="gameContent">The game content helper.</param>
        /// <returns>A dictionary where the key is the sanitized dialogue text and the value identifies the source event.</returns>
        private Dictionary<string, string> GetEventDialogueForCharacter(string targetCharacterName, string languageCode, IGameContentHelper gameContent)
        {
            var eventDialogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string eventsBasePath = "Data/Events/";

            // Regex to capture: speak CharacterName "Dialogue Text"
            // - Group 1: Character Name (\w+) - Assumes single-word names for simplicity in basic events
            // - Group 2: Dialogue Text ([^"]*) - Captures everything inside the quotes
            // Updated Regex to better handle potential leading/trailing spaces around name/quotes
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled);


            this.Monitor.Log($"Searching events for '{targetCharacterName}' dialogue ({languageCode})...", LogLevel.Trace);
            int foundInEventsCount = 0;

            foreach (string eventFileNameBase in CommonEventFileNames)
            {
                string assetKey = $"{eventsBasePath}{eventFileNameBase}{langSuffix}";
                Dictionary<string, string> eventData = null;

                try
                {
                    eventData = gameContent.Load<Dictionary<string, string>>(assetKey);
                }
                catch (ContentLoadException)
                {
                    // Monitor.Log($"Event asset not found: {assetKey}", LogLevel.Trace); // Log only if debugging asset loading
                    continue; // Skip if file doesn't exist for this language
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error loading event asset '{assetKey}': {ex.Message}", LogLevel.Warn);
                    continue; // Skip on other loading errors
                }

                if (eventData == null) continue; // Skip if loaded data is null

                foreach (var eventEntry in eventData) // eventEntry.Key = Event ID, eventEntry.Value = Event Script String
                {
                    string eventId = eventEntry.Key;
                    string eventScript = eventEntry.Value;

                    if (string.IsNullOrWhiteSpace(eventScript)) continue;

                    string[] commands = eventScript.Split('/');

                    foreach (string command in commands)
                    {
                        string trimmedCommand = command.Trim();
                        if (trimmedCommand.StartsWith("speak ", StringComparison.OrdinalIgnoreCase))
                        {
                            Match match = speakCommandRegex.Match(trimmedCommand);
                            if (match.Success)
                            {
                                string speakerName = match.Groups[1].Value;
                                string dialogueText = match.Groups[2].Value;

                                // *** Check if the speaker matches the target character ***
                                if (speakerName.Equals(targetCharacterName, StringComparison.OrdinalIgnoreCase))
                                {
                                    string sanitizedText = SanitizeDialogueText(dialogueText);

                                    if (!string.IsNullOrWhiteSpace(sanitizedText))
                                    {
                                        // Use sanitized text as key to avoid duplicates across sources
                                        if (!eventDialogue.ContainsKey(sanitizedText))
                                        {
                                            // Store where it came from (File/EventID)
                                            eventDialogue[sanitizedText] = $"Event:{eventFileNameBase}/{eventId}";
                                            foundInEventsCount++;
                                            // Monitor.Log($"    Found event dialogue for {targetCharacterName}: '{sanitizedText}' (Source: {eventFileNameBase}/{eventId})", LogLevel.Trace);
                                        }
                                        // else { Monitor.Log($"    Duplicate event dialogue text found and skipped: '{sanitizedText}'", LogLevel.Trace); }
                                    }
                                }
                            }
                            // else { Monitor.Log($"    Regex failed to parse speak command: '{trimmedCommand}'", LogLevel.Trace); } // Log parsing failures if needed
                        }
                    }
                }
            } // End foreach eventFileNameBase

            if (foundInEventsCount > 0)
                this.Monitor.Log($"Found {foundInEventsCount} potential event dialogue lines for '{targetCharacterName}' ({languageCode}).", LogLevel.Debug);
            else
                Monitor.Log($"No event dialogue lines found for '{targetCharacterName}' ({languageCode}) in common event files.", LogLevel.Trace);


            return eventDialogue;
        }

        // Existing methods (SanitizeDialogueText, GetVanillaCharacterStringKeys, etc.) below...
        // Make sure the using statements at the top of the file include System.Text.RegularExpressions
    

        // --- Harmony Patching ---
        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);

            this.Monitor.Log("Applying Harmony patches...", LogLevel.Debug);

            // Apply patches defined using Harmony attributes within this assembly
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                this.Monitor.Log("Harmony attribu-based patches applied successfully.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error applying Harmony attribute patches: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }


            // Apply manual patches if needed (example shown, ensure MuteTypingSoundPatch exists)
            try
            {
                if (this.Config.turnoffdialoguetypingsound) // Check config before applying
                {
                    MuteTypingSoundPatch.ApplyPatch(harmony, this.Monitor); // Pass Monitor for logging
                    this.Monitor.Log("Manual patch for MuteTypingSound applied.", LogLevel.Debug);
                }
                else
                {
                    this.Monitor.Log("Skipping manual patch for MuteTypingSound (disabled in config).", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error applying manual MuteTypingSound patch: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }


            this.Monitor.Log("Harmony patching process completed.", LogLevel.Debug);
        }

        // --- Event Handlers (Core/Config related) ---

        // Ran once when SMAPI is ready (good for GMCM setup)
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.Monitor.Log("GameLaunched event: Setting up GMCM integration...", LogLevel.Debug);
            SetupGMCM(); // Call GMCM setup method (to be implemented)
        }

        // Ran when a save file is loaded (good for reloading config)
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.Monitor.Log("SaveLoaded event: Reloading config...", LogLevel.Debug);
            // Reload config in case it changed via GMCM while not in-game, or for per-save settings if added later
            this.Config = this.Helper.ReadConfig<ModConfig>();
            this.SelectedVoicePacks = this.Config?.SelectedVoicePacks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Maybe trigger a refresh of GMCM if needed? Usually GMCM handles live updates via its API.
        }


        // --- GMCM Setup (Placeholder) ---
        private void SetupGMCM()
        {
            var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null)
            {
                this.Monitor.Log("Generic Mod Config Menu API not found. Skipping GMCM setup.", LogLevel.Debug);
                return;
            }

            this.Monitor.Log("Registering Mod for GMCM...", LogLevel.Trace);

            // Register this mod with GMCM
            gmcm.Register(
                mod: this.ModManifest,
                reset: () => {
                    this.Config = new ModConfig(); // Reset config to defaults
                    this.SelectedVoicePacks = this.Config.SelectedVoicePacks; // Update internal dict
                },
                save: () => {
                    this.Helper.WriteConfig(this.Config); // Save current config
                                                          // Optional: Maybe reload voice packs or update something immediately after save?
                    this.Monitor.Log("Configuration saved via GMCM.", LogLevel.Debug);
                    // Example: Apply volume change immediately? (Requires more logic)
                },
                 titleScreenOnly: false // Allow config access in-game
            );

            this.Monitor.Log("Adding GMCM options...", LogLevel.Trace);

            // Add basic options
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => "General Settings");

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Mute Dialogue Typing Sound",
                tooltip: () => "If checked, disables the default 'tick' sound when dialogue appears.",
                getValue: () => this.Config.turnoffdialoguetypingsound,
                setValue: value => this.Config.turnoffdialoguetypingsound = value
            );

            gmcm.AddNumberOption(
                 mod: this.ModManifest,
                 name: () => "Master Volume",
                 tooltip: () => "Adjusts the overall volume for voice lines (multiplied by game sound volume).",
                 getValue: () => this.Config.MasterVolume,
                 setValue: value => this.Config.MasterVolume = value,
                 min: 0.0f,
                 max: 1.0f,
                 interval: 0.05f, // Volume steps
                 formatValue: value => $"{Math.Round(value * 100)}%" // Display as percentage
            );

            // Language settings (Example - simple text box for now)
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => "Language Settings");
            gmcm.AddTextOption(
                 mod: this.ModManifest,
                 name: () => "Default Language Code",
                 tooltip: () => "Enter the preferred language code (e.g., en, es-ES, zh-CN) for voices.",
                 getValue: () => this.Config.DefaultLanguage,
                 setValue: value => this.Config.DefaultLanguage = value
             );
            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Use English if Default Missing",
                tooltip: () => "If a voice line isn't found for the Default Language, try loading the English version instead.",
                getValue: () => this.Config.FallbackToDefaultIfMissing, // Assuming 'en' is the ultimate fallback
                setValue: value => this.Config.FallbackToDefaultIfMissing = value
            );


            // --- Dynamic Voice Pack Selection ---
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => "Voice Pack Selection");
            gmcm.AddParagraph(mod: this.ModManifest, text: () => "Select which voice pack to use for each character. Requires loaded voice packs.");

            // Get unique character names that HAVE loaded voice packs
            var charactersWithPacks = VoicePacksByCharacter.Keys.OrderBy(name => name).ToList();

            if (!charactersWithPacks.Any())
            {
                gmcm.AddParagraph(mod: this.ModManifest, text: () => "No voice packs loaded. Install voice content packs first.");
            }
            else
            {
                // Create a dropdown for each character
                foreach (string characterName in charactersWithPacks)
                {
                    // Get available packs for *this* character (needed for dropdown options)
                    // Filter by language? For now, list all pack IDs for the character.
                    // A better approach might group by language first if packs exist in multiple languages.
                    // Let's list unique VoicePack IDs available for this character across all loaded languages.
                    var packsForChar = VoicePacksByCharacter[characterName];
                    var availablePackChoices = packsForChar
                                               .Select(p => p.VoicePackId) // Get the IDs
                                               .Distinct(StringComparer.OrdinalIgnoreCase) // Ensure uniqueness
                                               .OrderBy(id => id) // Sort IDs
                                               .ToList();

                    // Add "None" option to disable voice for this character
                    var displayChoices = new List<string> { "None" };
                    // Add display names for the available packs (ID is used for saving)
                    // Try to get a nice name, fall back to ID if needed
                    displayChoices.AddRange(availablePackChoices.Select(id =>
                            packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id
                        ));


                    gmcm.AddTextOption(
                        mod: this.ModManifest,
                        name: () => $"{characterName} Voice", // Dropdown label
                        tooltip: () => $"Select the voice pack for {characterName}.",
                        getValue: () => {
                            // Read the currently selected ID from our config dictionary
                            SelectedVoicePacks.TryGetValue(characterName, out string selectedId);
                            // Find the corresponding display name, default to "None" if not found or null/empty
                            string displayName = packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? selectedId;
                            return string.IsNullOrWhiteSpace(displayName) ? "None" : displayName;
                        },
                        setValue: displayValue => {
                            // Find the VoicePackId corresponding to the selected display name
                            string selectedId = "None"; // Default to "None"
                            if (displayValue != "None")
                            {
                                selectedId = packsForChar.FirstOrDefault(p => (p.VoicePackName ?? p.VoicePackId).Equals(displayValue, StringComparison.OrdinalIgnoreCase))?.VoicePackId ?? displayValue; // Fallback to displayValue if name lookup fails? Risky. Better stick to known IDs.

                                // More robust: Find ID from the known available choices based on display name
                                selectedId = availablePackChoices.FirstOrDefault(id =>
                                    (packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id).Equals(displayValue, StringComparison.OrdinalIgnoreCase)
                                    ) ?? "None"; // Ensure we save a known ID or "None"
                            }

                            // Update the config dictionary
                            if (selectedId == "None")
                            {
                                SelectedVoicePacks.Remove(characterName); // Or set SelectedVoicePacks[characterName] = null/empty string if preferred
                                this.Monitor.Log($"GMCM: Set {characterName} voice to None.", LogLevel.Trace);
                            }
                            else
                            {
                                SelectedVoicePacks[characterName] = selectedId;
                                this.Monitor.Log($"GMCM: Set {characterName} voice to Pack ID: {selectedId} (Selected: '{displayValue}')", LogLevel.Trace);
                            }
                            // Config dictionary is updated, save() will write it to file later.
                        },
                        allowedValues: displayChoices.ToArray() // Provide the list of display names
                    );
                }
            }


            // Developer Options
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => "Developer Options");
            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Developer Mode",
                tooltip: () => "Enables extra logging and potentially debug features. May impact performance.",
                getValue: () => this.Config.developerModeOn,
                setValue: value => this.Config.developerModeOn = value
            );

            this.Monitor.Log("GMCM setup complete.", LogLevel.Debug);
        }


    } // End of partial class ModEntry
} // End of namespace