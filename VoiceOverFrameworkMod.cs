using System; // Added for StringComparer
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GenericModConfigMenu;
using HarmonyLib; // Keep for ApplyHarmonyPatches if it stays here
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Internal;


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


       



        private void ListAllNPCCharacterData(string command, string[] args)
        {
            var printed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.Monitor.Log("Listing all NPCs with known character data and dialogue...", LogLevel.Info);

            // 1. Hardcoded vanilla characters (always included)
            string[] vanillaNames = new[]
            {
        "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Dwarf", "Elliott", "Emily", "Evelyn", "George",
        "Gil", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Kent", "Krobus", "Leah", "Leo", "LeoMainland",
        "Lewis", "Linus", "Marnie", "Maru", "Mister Qi", "Pam", "Penny", "Pierre", "Robin",
        "Sam", "Sandy", "Sebastian", "Shane", "Vincent", "Willy", "Wizard", "Birdie", "Gunther",
        "Marlon", "Morris", "Henchman", "Bouncer", "Grandpa", "Governor", "Professor Snail"
    };

            foreach (string name in vanillaNames)
            {
                var assetKey = $"Characters/Dialogue/{name}";
                bool hasDialogue = false;

                try
                {
                    var parsed = this.Helper.GameContent.ParseAssetName(assetKey);
                    hasDialogue = this.Helper.GameContent.DoesAssetExist<Dictionary<string, string>>(parsed);
                }
                catch { }

                this.Monitor.Log($"Vanilla: {name}" + (hasDialogue ? "" : " (no dialogue)"), LogLevel.Info);
                printed.Add(name);
            }

            
            // 2. All currently loaded characters (modded + some vanilla)
            foreach (NPC npc in Utility.getAllCharacters())
            {
                string name = npc.Name;
                if (printed.Contains(name) || IsSharedOrSystemDialogueFile(name))
                    continue;

                var data = npc.GetData();
                string displayName = data?.DisplayName ?? name;

                var assetKey = $"Characters/Dialogue/{name}";
                bool hasDialogue = false;

                

                try
                {
                    var parsed = this.Helper.GameContent.ParseAssetName(assetKey);
                    hasDialogue = this.Helper.GameContent.DoesAssetExist<Dictionary<string, string>>(parsed);
                }
                catch { }

                string label = IsKnownVanillaVillager(name) ? "Vanilla" : "Modded";
                this.Monitor.Log($"{label}: {name} ({displayName})" + (hasDialogue ? "" : " (no dialogue)"), LogLevel.Info);
                printed.Add(name);
            }

            // 3. Mod folder fallback (scan all Characters/Dialogue/*.json)
            string modsDir = Path.Combine(Directory.GetCurrentDirectory(), "Mods");
            if (Directory.Exists(modsDir))
            {
                foreach (string modFolder in Directory.EnumerateDirectories(modsDir))
                {
                    foreach (string file in Directory.EnumerateFiles(modFolder, "*.json", SearchOption.AllDirectories))
                    {
                        if (!file.Contains(Path.Combine("Characters", "Dialogue")))
                            continue;

                        string name = Path.GetFileNameWithoutExtension(file).Split('.')[0];
                        if (printed.Contains(name) || IsSharedOrSystemDialogueFile(name))
                            continue;

                        string displayName = name;

                        var npc = Game1.getCharacterFromName(name, false);
                        if (npc?.GetData() != null)
                            displayName = npc.GetData().DisplayName;

                        this.Monitor.Log($"Modded: {name} ({displayName})", LogLevel.Info);
                        printed.Add(name);
                    }
                }
            }
        }



        private bool IsSharedOrSystemDialogueFile(string name)
        {
            return name.Equals("MarriageDialogue", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MarriageDialogue", StringComparison.OrdinalIgnoreCase)
                || name.Equals("adoption", StringComparison.OrdinalIgnoreCase)
                || name.Equals("boatTunnel", StringComparison.OrdinalIgnoreCase)
                || name.Equals("returnBoat", StringComparison.OrdinalIgnoreCase)
                || name.Equals("customFestival", StringComparison.OrdinalIgnoreCase)
                || name.Equals("eventQuestionResponses", StringComparison.OrdinalIgnoreCase);
        }




        //gets list of NCP from Vanilla and Modded used in  Create_template
        private List<string> GetAllKnownCharacterNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Hardcoded vanilla list
            string[] vanillaList = new[]
                    {
                "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Dwarf", "Elliott", "Emily", "Evelyn", "George",
                "Gil", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Kent", "Krobus", "Leah", "Leo", "LeoMainland",
                "Lewis", "Linus", "Marnie", "Maru", "Mister Qi", "Pam", "Penny", "Pierre", "Robin", "Sam", "Sandy",
                "Sebastian", "Shane", "Vincent", "Willy", "Wizard", "Birdie", "Gunther", "Marlon", "Morris",
                "Governor", "Grandpa", "MrQi", "Henchman", "Bouncer", "Professor Snail"
            };

            foreach (string name in vanillaList)
                result.Add(name);

            foreach (NPC npc in Utility.getAllCharacters())
                if (!string.IsNullOrWhiteSpace(npc?.Name))
                    result.Add(npc.Name);

            if (Game1.characterData != null)
            {
                foreach (string name in Game1.characterData.Keys)
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Add(name);
            }

            return result.OrderBy(name => name).ToList();
        }





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







        //get Event Dialogues dynamically so that modded events are also included
        private Dictionary<string, string> GetEventDialogueForCharacter(string targetCharacterName, string languageCode, IGameContentHelper gameContent)
        {
            var eventDialogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled);
            var fallbackQuoteRegex = new Regex($@"(?:textAboveHead|drawDialogue|message|showText)\s+(?:{Regex.Escape(targetCharacterName)}\w*)?\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var looseQuoteRegex = new Regex($@"{Regex.Escape(targetCharacterName)}\w*\s+.*?""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            int foundInEventsCount = 0;

            foreach (var location in Game1.locations)
            {
                if (!location.TryGetLocationEvents(out string assetName, out Dictionary<string, string> eventData))
                    continue;

                if (eventData == null || eventData.Count == 0)
                    continue;

                foreach (var eventEntry in eventData)
                {
                    string eventId = eventEntry.Key;
                    string eventScript = eventEntry.Value;
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;

                    foreach (string command in commands)
                    {
                        string trimmedCommand = command.Trim();

                        // --- Case 1: Standard speak command ---
                        if (trimmedCommand.StartsWith("speak ", StringComparison.OrdinalIgnoreCase))
                        {
                            Match match = speakCommandRegex.Match(trimmedCommand);
                            if (match.Success)
                            {
                                string speakerName = match.Groups[1].Value;
                                string rawDialogueText = match.Groups[2].Value;

                                lastSpeaker = speakerName;

                                if (IsCharacterMatch(speakerName, targetCharacterName))
                                {
                                    string sanitizedText = SanitizeDialogueText(rawDialogueText);
                                    AddEventDialogue(eventDialogue, location, eventId, sanitizedText, ref foundInEventsCount);
                                }
                            }
                        }

                        // --- Case 2: Named message/drawDialogue/showText/textAboveHead ---
                        Match fallbackMatch = fallbackQuoteRegex.Match(trimmedCommand);
                        if (fallbackMatch.Success)
                        {
                            string rawDialogueText = fallbackMatch.Groups[1].Value;
                            string sanitizedText = SanitizeDialogueText(rawDialogueText);

                            if (!string.IsNullOrWhiteSpace(sanitizedText) && IsCharacterMatch(lastSpeaker, targetCharacterName))
                            {
                                AddEventDialogue(eventDialogue, location, eventId, sanitizedText, ref foundInEventsCount, suffix: "_alt");
                            }
                        }

                        // --- Case 3: Loose fallback match (Abigail "something" / Abigail_spouse "something") ---
                        if (trimmedCommand.Contains(targetCharacterName, StringComparison.OrdinalIgnoreCase))
                        {
                            Match looseQuoteMatch = looseQuoteRegex.Match(trimmedCommand);
                            if (looseQuoteMatch.Success)
                            {
                                string rawDialogueText = looseQuoteMatch.Groups[1].Value;
                                string sanitizedText = SanitizeDialogueText(rawDialogueText);
                                AddEventDialogue(eventDialogue, location, eventId, sanitizedText, ref foundInEventsCount, suffix: "_loose");
                            }
                        }
                    }
                }
            }

            if (Config.developerModeOn)
            {
                if (foundInEventsCount > 0)
                    this.Monitor.Log($"Found {foundInEventsCount} potential event dialogue lines for '{targetCharacterName}'.", LogLevel.Debug);
                else
                    this.Monitor.Log($"No event dialogue lines found for '{targetCharacterName}'.", LogLevel.Trace);
            }

            return eventDialogue;
        }

        // Helper method to add and deduplicate dialogue
        private void AddEventDialogue(Dictionary<string, string> dict, GameLocation location, string eventId, string sanitizedText, ref int counter, string suffix = "")
        {
            if (string.IsNullOrWhiteSpace(sanitizedText))
                return;

            string baseKey = $"Event:{location.NameOrUniqueName}/{eventId}{suffix}";
            string uniqueKey = baseKey;
            int index = 1;

            while (dict.ContainsKey(uniqueKey))
                uniqueKey = $"{baseKey}_{index++}";

            dict[uniqueKey] = sanitizedText;
            counter++;
        }

        // Helper method for fuzzy speaker name matching
        private bool IsCharacterMatch(string nameToTest, string targetName)
        {
            if (string.IsNullOrWhiteSpace(nameToTest))
                return false;

            return nameToTest.StartsWith(targetName, StringComparison.OrdinalIgnoreCase);
        }








        // Existing methods (SanitizeDialogueText, GetVanillaCharacterStringKeys, etc.) below...
        // Make sure the using statements at the top of the file include System.Text.RegularExpressions


        // --- Harmony Patching ---
        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);

            //this.Monitor.Log("Applying Harmony patches...", LogLevel.Debug);

            // Apply patches defined using Harmony attributes within this assembly
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                //this.Monitor.Log("Harmony attribu-based patches applied successfully.", LogLevel.Debug);
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
                    //this.Monitor.Log("Manual patch for MuteTypingSound applied.", LogLevel.Debug);
                }
                else
                {
                    //this.Monitor.Log("Skipping manual patch for MuteTypingSound (disabled in config).", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error applying manual MuteTypingSound patch: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }


            //this.Monitor.Log("Harmony patching process completed.", LogLevel.Debug);
        }


        //Gets Festival Data
        private Dictionary<string, (string RawText, string SourceInfo)> GetFestivalDialogueForCharacter(
     string characterName,
     string languageCode,
     IGameContentHelper contentHelper)
        {
            var result = new Dictionary<string, (string RawText, string SourceInfo)>(StringComparer.OrdinalIgnoreCase);
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";

            // Dynamically get festival keys
            var activeFestivalKeys = DataLoader.Festivals_FestivalDates(Game1.content).Keys;
            var passiveFestivalKeys = DataLoader.PassiveFestivals(Game1.content).Keys;

            // Combine and deduplicate
            var allFestivalKeys = activeFestivalKeys.Concat(passiveFestivalKeys).Distinct();

            foreach (string festivalKey in allFestivalKeys)
            {
                string assetKeyString = $"Data/Festivals/{festivalKey}{langSuffix}";
                string sourceInfo = $"Festival/{festivalKey}";

                try
                {
                    var festivalData = contentHelper.Load<Dictionary<string, string>>(assetKeyString);
                    if (festivalData == null)
                        continue;

                    foreach (var kvp in festivalData)
                    {
                        string key = kvp.Key;
                        string value = kvp.Value;

                        // Match by key or embedded speaker
                        if (key.StartsWith(characterName, StringComparison.OrdinalIgnoreCase) ||
                            key.IndexOf(characterName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("speak "))
                            {
                                string sanitized = SanitizeDialogueText(value);
                                string uniqueKey = $"{sourceInfo}:{key}";
                                if (!result.ContainsKey(uniqueKey))
                                    result[uniqueKey] = (sanitized, sourceInfo);
                            }
                        }

                        foreach (Match match in Regex.Matches(value, $@"speak\s+{Regex.Escape(characterName)}\s+""([^""]+)"""))
                        {
                            string embeddedText = match.Groups[1].Value;
                            string sanitized = SanitizeDialogueText(embeddedText);
                            if (!string.IsNullOrWhiteSpace(sanitized))
                            {
                                string uniqueKey = $"{sourceInfo}:{key}:speak:{match.Index}";
                                if (!result.ContainsKey(uniqueKey))
                                    result[uniqueKey] = (sanitized, sourceInfo);
                            }
                        }

                        if (key.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                        {
                            string sanitized = SanitizeDialogueText(value);
                            string uniqueKey = $"{sourceInfo}:{key}";
                            if (!result.ContainsKey(uniqueKey))
                                result[uniqueKey] = (sanitized, sourceInfo);
                        }
                    }
                }
                catch (ContentLoadException)
                {
                    // Skip missing assets
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error reading festival data from '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                    this.Monitor.Log(ex.ToString(), LogLevel.Trace);
                }
            }

            return result;
        }











        /// <summary>
        /// Gets dialogue from Data/NPCGiftTastes.json for a character.
        /// Extracts dialogue text appearing between /.../ code blocks.
        /// </summary>
        private List<(string RawText, string SourceInfo)> GetGiftTasteDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/NPCGiftTastes{langSuffix}";
            const string sourceInfo = "NPCGiftTastes"; // Constant source info

            try
            {
                IAssetName assetName = contentHelper.ParseAssetName(assetKeyString);
                var giftTasteData = contentHelper.Load<Dictionary<string, string>>(assetName);

                if (giftTasteData != null && giftTasteData.TryGetValue(characterName, out string combinedReactions))
                {
                    if (!string.IsNullOrWhiteSpace(combinedReactions))
                    {
                        // Split the string by the '/' delimiter
                        string[] segments = combinedReactions.Split('/');
                        int dialogueCount = 0;

                        // Iterate through the segments using an index
                        for (int i = 0; i < segments.Length; i++)
                        {
                            // Keep segments at EVEN indices (0, 2, 4, ...) as these are the dialogue parts
                            if (i % 2 == 0)
                            {
                                string potentialDialogue = segments[i].Trim();
                                // Add the segment if it's not empty after trimming
                                if (!string.IsNullOrWhiteSpace(potentialDialogue))
                                {
                                    dialogueList.Add((potentialDialogue, sourceInfo));
                                    dialogueCount++;
                                }
                            }
                            // Ignore segments at ODD indices (1, 3, 5, ...) as they contain codes/IDs
                        }

                        if (this.Config.developerModeOn)
                        {
                            this.Monitor.Log($"    -> Extracted {dialogueCount} gift taste dialogue segments for '{characterName}' from {assetKeyString}.", LogLevel.Trace);
                        }
                    }
                }
            }
            catch (ContentLoadException) { /*this.Monitor.Log($"Asset '{assetKeyString}' not found.", LogLevel.Trace);*/ }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/processing '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return dialogueList;
        }

        /// <summary>
        /// Gets dialogue from Data/EngagementDialogue.json for a character.
        /// Matches keys starting with the character's name (e.g., "Abigail0").
        /// </summary>
        private List<(string RawText, string SourceInfo)> GetEngagementDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/EngagementDialogue{langSuffix}";
            const string sourceInfo = "EngagementDialogue"; // Constant source info

            try
            {
                IAssetName assetName = contentHelper.ParseAssetName(assetKeyString);
                var engagementData = contentHelper.Load<Dictionary<string, string>>(assetName);

                if (engagementData != null)
                {
                    // Find all keys that start with the character's name (case-insensitive)
                    foreach (var kvp in engagementData)
                    {
                        if (kvp.Key.StartsWith(characterName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                dialogueList.Add((kvp.Value, sourceInfo));
                                // Monitor.Log($"    -> Found Engagement line for '{characterName}' (Key: {kvp.Key}) in {assetKeyString}: \"{kvp.Value}\"", LogLevel.Trace);
                            }
                        }
                    }
                }
            }
            catch (ContentLoadException) { /*this.Monitor.Log($"Asset '{assetKeyString}' not found.", LogLevel.Trace);*/ }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/processing '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return dialogueList;
        }

        /// <summary>
        /// Gets dialogue from Data/ExtraDialogue.json for a character.
        /// Matches keys based on various patterns containing the character's name.
        /// </summary>
        private List<(string RawText, string SourceInfo)> GetExtraDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/ExtraDialogue{langSuffix}";
            const string sourceInfo = "ExtraDialogue"; // Constant source info

            // Pre-calculate patterns for matching
            string prefixPattern = $"{characterName}_";
            string infixPattern = $"_{characterName}_";
            string suffixPattern = $"_{characterName}";

            try
            {
                IAssetName assetName = contentHelper.ParseAssetName(assetKeyString);
                var extraData = contentHelper.Load<Dictionary<string, string>>(assetName);

                if (extraData != null)
                {
                    foreach (var kvp in extraData)
                    {
                        string key = kvp.Key;
                        // Check if the key relates to the character using multiple patterns (case-insensitive)
                        bool isMatch = key.Equals(characterName, StringComparison.OrdinalIgnoreCase) ||
                                       key.StartsWith(prefixPattern, StringComparison.OrdinalIgnoreCase) ||
                                       key.EndsWith(suffixPattern, StringComparison.OrdinalIgnoreCase) ||
                                       key.IndexOf(infixPattern, StringComparison.OrdinalIgnoreCase) >= 0; // IndexOf is often faster than Contains for specific substrings

                        if (isMatch)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                dialogueList.Add((kvp.Value, sourceInfo));
                                // Monitor.Log($"    -> Found ExtraDialogue line potentially for '{characterName}' (Key: {kvp.Key}) in {assetKeyString}: \"{kvp.Value}\"", LogLevel.Trace);
                            }
                        }
                    }
                }
            }
            catch (ContentLoadException) { /* this.Monitor.Log($"Asset '{assetKeyString}' not found.", LogLevel.Trace); */ }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/processing '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return dialogueList;
        }


        /// <summary>
        /// Splits dialogue text using standard delimiters like #$b#, trims results, and removes empty entries.
        /// </summary>
        /// <param name="rawText">The raw dialogue text potentially containing delimiters.</param>
        /// <returns>An enumerable collection of non-empty dialogue segments.</returns>
        private IEnumerable<string> SplitStandardDialogueSegments(string rawText)
        {
            // Return empty collection if input is null or whitespace to avoid errors later
            if (string.IsNullOrWhiteSpace(rawText))
                return Enumerable.Empty<string>(); // Requires System.Linq

            // Split by common delimiters, trim results, remove empty ones
            return Regex.Split(rawText, @"(?:##|#\$e#|#\$b#)") // Requires System.Text.RegularExpressions
                        .Select(s => s.Trim()) // Requires System.Linq
                        .Where(s => !string.IsNullOrEmpty(s)); // Requires System.Linq
        }





        // --- Event Handlers (Core/Config related) ---

        // Ran once when SMAPI is ready (good for GMCM setup)
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            //this.Monitor.Log("GameLaunched event: Setting up GMCM integration...", LogLevel.Debug);
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
                return;
            }
            this.Monitor.Log("Adding GMCM options...", LogLevel.Trace);
            gmcm.Register(ModManifest, () => Config = new ModConfig(), () => Helper.WriteConfig(Config));

            // === General Settings ===
            // ... (Section Title, Mute Typing, Master Volume as before, using i18n) ...
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => this.Helper.Translation.Get("config.section.general.name"));
            gmcm.AddBoolOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.mute-typing.name"), tooltip: () => this.Helper.Translation.Get("config.mute-typing.tooltip"), getValue: () => this.Config.turnoffdialoguetypingsound, setValue: value => this.Config.turnoffdialoguetypingsound = value);
            gmcm.AddNumberOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.master-volume.name"), tooltip: () => this.Helper.Translation.Get("config.master-volume.tooltip"), getValue: () => this.Config.MasterVolume, setValue: value => this.Config.MasterVolume = value, min: 0.0f, max: 1.0f, interval: 0.05f, formatValue: value => $"{Math.Round(value * 100)}%");

            // === Dynamic Voice Pack Selection ===
            // ... (Section Title, Paragraph, Character dropdowns as before, using i18n) ...
            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => this.Helper.Translation.Get("config.section.voice-packs.name"));
            gmcm.AddParagraph(mod: this.ModManifest, text: () => this.Helper.Translation.Get("config.voice-packs.description"));
            var charactersWithPacks = VoicePacksByCharacter.Keys.OrderBy(name => name).ToList();
            if (!charactersWithPacks.Any())
            {
                gmcm.AddParagraph(mod: this.ModManifest, text: () => this.Helper.Translation.Get("config.voice-packs.none-loaded"));
            }
            else
            {
                foreach (string characterName in charactersWithPacks)
                {
                    // ... (character dropdown logic using i18n as updated previously) ...
                    string currentCharacterName = characterName; // Local capture
                    var packsForChar = VoicePacksByCharacter[currentCharacterName];
                    var availablePackChoices = packsForChar.Select(p => p.VoicePackId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToList();
                    string noneOptionText = this.Helper.Translation.Get("config.character-voice.none-option");
                    var displayChoices = new List<string> { noneOptionText };
                    displayChoices.AddRange(availablePackChoices.Select(id => packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id));
                    gmcm.AddTextOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.character-voice.name", new { characterName = currentCharacterName }), tooltip: () => this.Helper.Translation.Get("config.character-voice.tooltip", new { characterName = currentCharacterName }), getValue: () => { SelectedVoicePacks.TryGetValue(currentCharacterName, out string selectedId); string displayName = packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? selectedId; return string.IsNullOrWhiteSpace(displayName) ? noneOptionText : displayName; }, setValue: displayValue => { string selectedId = noneOptionText; if (displayValue != noneOptionText) { selectedId = availablePackChoices.FirstOrDefault(id => (packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id).Equals(displayValue, StringComparison.OrdinalIgnoreCase)) ?? noneOptionText; } if (selectedId == noneOptionText || string.IsNullOrEmpty(selectedId)) { SelectedVoicePacks.Remove(currentCharacterName); this.Monitor.Log($"GMCM: Set {currentCharacterName} voice to None.", LogLevel.Trace); } else { SelectedVoicePacks[currentCharacterName] = selectedId; this.Monitor.Log($"GMCM: Set {currentCharacterName} voice to Pack ID: {selectedId} (Selected: '{displayValue}')", LogLevel.Trace); } }, allowedValues: displayChoices.ToArray());
                }
            }


            // === Developer Options ===
            gmcm.AddSectionTitle(
                mod: this.ModManifest,
                text: () => this.Helper.Translation.Get("config.section.developer.name") // i18n
            );

            gmcm.AddParagraph(
                mod: this.ModManifest,
                text: () => this.Helper.Translation.Get("config.dev-options.create-template-info") // i18n
            );


            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => this.Helper.Translation.Get("config.developer-mode.name"),       // i18n
                tooltip: () => this.Helper.Translation.Get("config.developer-mode.tooltip"), // i18n
                getValue: () => this.Config.developerModeOn,
                setValue: value => this.Config.developerModeOn = value
            );

            /*
            gmcm.AddKeybind(
                mod: this.ModManifest,
                name: () => this.Helper.Translation.Get("config.dev-tool-keybind.name"),       // i18n
                tooltip: () => this.Helper.Translation.Get("config.dev-tool-keybind.tooltip"), // i18n
                getValue: () => this.Config.devToolMenu, // Get value from config
                setValue: value => this.Config.devToolMenu = value  // Set value in config
            );
            */



            this.Monitor.Log("GMCM setup complete.", LogLevel.Debug);
        }


    } // End of partial class ModEntry
} // End of namespace