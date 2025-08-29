
using System.Reflection;
using System.Text.RegularExpressions;
using GenericModConfigMenu;
using HarmonyLib; 
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;


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
        internal static ModEntry Instance { get; private set; }
        public ModConfig Config { get; private set; }

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






        public override void Entry(IModHelper helper)
        {
            this.Multilingual = new MultilingualDictionary(this, this.Monitor, this.Helper.DirectoryPath);
            Instance = this; 
            this.Config = helper.ReadConfig<ModConfig>();
            this.SelectedVoicePacks = this.Config?.SelectedVoicePacks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Config.developerModeOn)
            {
                this.Monitor.Log("Configuration loaded.", LogLevel.Debug);
            }
            

            LoadVoicePacks();

            ApplyHarmonyPatches();

        
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked; 
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched; 

            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded; 

            SetupConsoleCommands(helper.ConsoleCommands);


            Monitor.Log($"{this.ModManifest.Name} {this.ModManifest.Version} initialized.", LogLevel.Info);
        }







        //get Event Dialogues dynamically so that modded events are also included
        private Dictionary<string, string> GetEventDialogueForCharacter(string targetCharacterName, string languageCode, IGameContentHelper gameContent)
        {
            Monitor.Log($"[VoiceFramework] Scanning for event dialogue for '{targetCharacterName}'...", LogLevel.Info);

            var eventDialogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled);
            var namedQuoteRegex = new Regex($@"(?:textAboveHead|drawDialogue|message|showText)\s+(\w*)\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var genericQuoteRegex = new Regex(@"""([^""]{4,})""", RegexOptions.Compiled);

            int foundInEventsCount = 0;

            foreach (var location in Game1.locations)
            {
                if (!location.TryGetLocationEvents(out string assetName, out Dictionary<string, string> eventData))
                    continue;

                foreach (var (eventId, eventScript) in eventData)
                {
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;

                    foreach (string rawCommand in commands)
                    {
                        string command = rawCommand.Trim();

                        // --- Case 1: speak command ---
                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            if (IsCharacterMatch(speaker, targetCharacterName))
                            {
                                var lines = ExtractVoiceLinesFromDialogue(dialogueText);
                                foreach (var line in lines)
                                    AddEventDialogue(eventDialogue, location, eventId, line, ref foundInEventsCount);
                            }
                            continue;
                        }

                        // --- Case 2: drawDialogue/message/etc ---
                        var namedMatch = namedQuoteRegex.Match(command);
                        if (namedMatch.Success)
                        {
                            string possibleSpeaker = namedMatch.Groups[1].Value;
                            string dialogueText = namedMatch.Groups[2].Value;

                            if (!string.IsNullOrWhiteSpace(possibleSpeaker))
                                lastSpeaker = possibleSpeaker;

                            if (IsCharacterMatch(lastSpeaker, targetCharacterName))
                            {
                                var lines = ExtractVoiceLinesFromDialogue(dialogueText);
                                foreach (var line in lines)
                                    AddEventDialogue(eventDialogue, location, eventId, line, ref foundInEventsCount, suffix: "_alt");
                            }
                            continue;
                        }

                        // --- Case 3: Generic quoted text (contextual speaker match) ---
                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch(lastSpeaker, targetCharacterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
                            foreach (Match match in genericMatches)
                            {
                                string line = match.Groups[1].Value.Trim();
                                if (line.Length > 3 && !line.StartsWith("..."))
                                {
                                    var splitLines = ExtractVoiceLinesFromDialogue(line);
                                    foreach (var l in splitLines)
                                        AddEventDialogue(eventDialogue, location, eventId, l, ref foundInEventsCount, suffix: "_implied");
                                }
                            }
                        }
                    }
                }
            }

            Monitor.Log($"[VoiceFramework] Found {foundInEventsCount} event dialogue lines for '{targetCharacterName}'.", LogLevel.Info);
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




        // Splits complex dialogue into separate lines, removing tags like $h, $b, $4, etc. (used in get event)
        private List<string> ExtractVoiceLinesFromDialogue(string rawText)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(rawText))
                return results;


            string sanitized = Regex.Replace(rawText, @"\$[a-zA-Z0-9]+", "").Trim();

    
            string[] parts = sanitized.Split(new[] { "#$b#", "#$b", "$b#", "$b" }, StringSplitOptions.None);
            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    results.Add(trimmed);
            }

            return results;
        }




        // Harmony Patching to remove that typing sound
        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);



            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
               
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error applying Harmony attribute patches: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }

            
          
       
            try
            {
                if (this.Config.turnoffdialoguetypingsound) 
                {
                    MuteTypingSoundPatch.ApplyPatch(harmony, this.Monitor);
                   
                }
                else
                {
                   
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error applying manual MuteTypingSound patch: {ex.Message}", LogLevel.Error);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }


          
        }


       
        //Get Festival Data
        private Dictionary<string, (string RawText, string SourceInfo)> GetFestivalDialogueForCharacter(string characterName,string languageCode,IGameContentHelper contentHelper)
        {
            var result = new Dictionary<string, (string RawText, string SourceInfo)>(StringComparer.OrdinalIgnoreCase);
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";

            
            var activeFestivalKeys = DataLoader.Festivals_FestivalDates(Game1.content).Keys;
            var passiveFestivalKeys = DataLoader.PassiveFestivals(Game1.content).Keys;

            
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



        // Gets dialogue from Data/NPCGiftTastes.json for a character.
        private List<(string RawText, string SourceInfo)> GetGiftTasteDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/NPCGiftTastes{langSuffix}";
            const string sourceInfo = "NPCGiftTastes"; 

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

                       
                        for (int i = 0; i < segments.Length; i++)
                        {
                           
                            if (i % 2 == 0)
                            {
                                string potentialDialogue = segments[i].Trim();
                              
                                if (!string.IsNullOrWhiteSpace(potentialDialogue))
                                {
                                    dialogueList.Add((potentialDialogue, sourceInfo));
                                    dialogueCount++;
                                }
                            }
                          
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

        
        // Gets dialogue from Data/EngagementDialogue.json for a character.
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
                 
                    foreach (var kvp in engagementData)
                    {
                        if (kvp.Key.StartsWith(characterName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                dialogueList.Add((kvp.Value, sourceInfo));
                              
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


        // Gets dialogue from Data/ExtraDialogue.json for a character.
        private List<(string RawText, string SourceInfo)> GetExtraDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/ExtraDialogue{langSuffix}";
            const string sourceInfo = "ExtraDialogue";

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
                                       key.IndexOf(infixPattern, StringComparison.OrdinalIgnoreCase) >= 0; 

                        if (isMatch)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                dialogueList.Add((kvp.Value, sourceInfo));
                               
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



        // Splits dialogue text using standard delimiters like #$b#, trims results, and removes empty entries.
        private IEnumerable<string> SplitStandardDialogueSegments(string rawText)
        {
 
            if (string.IsNullOrWhiteSpace(rawText))
                return Enumerable.Empty<string>(); 


            return Regex.Split(rawText, @"(?:##|#\$e#|#\$b#)") 
                        .Select(s => s.Trim()) 
                        .Where(s => !string.IsNullOrEmpty(s)); 
        }





        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {

            SetupGMCM();



        }


        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.Monitor.Log("SaveLoaded event: Reloading config...", LogLevel.Debug);

            this.Config = this.Helper.ReadConfig<ModConfig>();
            this.SelectedVoicePacks = this.Config?.SelectedVoicePacks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var character in VoicePacksByCharacter.Keys)
            {
                this.Multilingual.LoadAllForCharacter(character);
            }

        }



        private void SetupGMCM()
        {
            var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null)
            {
                return;
            }
            this.Monitor.Log("Adding GMCM options...", LogLevel.Trace);
            gmcm.Register(ModManifest, () => Config = new ModConfig(), () => Helper.WriteConfig(Config));

            gmcm.AddSectionTitle(mod: this.ModManifest, text: () => this.Helper.Translation.Get("config.section.general.name"));
            gmcm.AddBoolOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.mute-typing.name"), tooltip: () => this.Helper.Translation.Get("config.mute-typing.tooltip"), getValue: () => this.Config.turnoffdialoguetypingsound, setValue: value => this.Config.turnoffdialoguetypingsound = value);
            gmcm.AddNumberOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.master-volume.name"), tooltip: () => this.Helper.Translation.Get("config.master-volume.tooltip"), getValue: () => this.Config.MasterVolume, setValue: value => this.Config.MasterVolume = value, min: 0.0f, max: 1.0f, interval: 0.05f, formatValue: value => $"{Math.Round(value * 100)}%");


            gmcm.AddNumberOption(
                   mod: this.ModManifest,
                   name: () => "Audio Start Speed",
                   tooltip: () => "Adjusting this will make the Audio play sooner or later when dialogue starts to appear. Too low and 2nd dialogue gets play partially on first dialogue box. Too high and there is a noticeable delay before voice plays when dialogue appears ",
                   getValue: () => this.Config.TextStabilizeTicks,
                   setValue: v => this.Config.TextStabilizeTicks = Math.Clamp(v, 0, 60),
                   min: 0,
                   max: 60,
                   interval: 1
            );

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => this.Helper.Translation.Get("config.auto-fix-dialoguefrom.name"),
                tooltip: () => this.Helper.Translation.Get("config.auto-fix-dialoguefrom.tooltip"),
                getValue: () => this.Config.AutoFixDialogueFromOnLoad,
                setValue: value => this.Config.AutoFixDialogueFromOnLoad = value
            );


            // Collect distinct content pack IDs
            var availablePackSources = VoicePacksByCharacter
                .SelectMany(kvp => kvp.Value.Select(vp => vp.ContentPackId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id)
                .ToList();

            // Add blank option for "do nothing"
            availablePackSources.Insert(0, "<None>");

            gmcm.AddTextOption(
                mod: this.ModManifest,
                name: () => this.Helper.Translation.Get("config.mass-assign-voicepack.name"),
                tooltip: () => this.Helper.Translation.Get("config.mass-assign-voicepack.tooltip"),
                getValue: () => "<None>",
                setValue: selectedModId =>
                {
                    if (string.IsNullOrWhiteSpace(selectedModId) || selectedModId == "<None>")
                    {
                        Monitor.Log("[GMCM] Mass assignment skipped (user selected '<None>').", LogLevel.Debug);
                        return;
                    }

                    var newAssignments = new Dictionary<string, string>();
                    foreach (var (character, packs) in VoicePacksByCharacter)
                    {
                        var pack = packs.FirstOrDefault(p =>
                            p.ContentPackId.Equals(selectedModId, StringComparison.OrdinalIgnoreCase));

                        if (pack != null)
                            newAssignments[character] = pack.VoicePackId;
                    }

                    if (newAssignments.Count == 0)
                    {
                        Monitor.Log($"[GMCM] No voice packs found in '{selectedModId}' to assign.", LogLevel.Warn);
                        return;
                    }


                    var updated = new Dictionary<string, string>(Config.SelectedVoicePacks);
                    foreach (var kvp in newAssignments)
                        updated[kvp.Key] = kvp.Value;

                    Config.SelectedVoicePacks = updated;
                    Helper.WriteConfig(Config);

                    Monitor.Log($"[GMCM] Mass assigned {newAssignments.Count} voice packs from '{selectedModId}' to matching characters.", LogLevel.Info);
                    Monitor.Log("[GMCM] You must restart the game to see updated selections reflected in this menu.", LogLevel.Info);
                },
                allowedValues: availablePackSources.ToArray()
            );






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
            
                    string currentCharacterName = characterName; 
                    var packsForChar = VoicePacksByCharacter[currentCharacterName];
                    var availablePackChoices = packsForChar.Select(p => p.VoicePackId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToList();
                    string noneOptionText = this.Helper.Translation.Get("config.character-voice.none-option");
                    var displayChoices = new List<string> { noneOptionText };
                    displayChoices.AddRange(availablePackChoices.Select(id => packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id));
                    gmcm.AddTextOption(mod: this.ModManifest, name: () => this.Helper.Translation.Get("config.character-voice.name", new { characterName = currentCharacterName }), tooltip: () => this.Helper.Translation.Get("config.character-voice.tooltip", new { characterName = currentCharacterName }), getValue: () => { SelectedVoicePacks.TryGetValue(currentCharacterName, out string selectedId); string displayName = packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? selectedId; return string.IsNullOrWhiteSpace(displayName) ? noneOptionText : displayName; }, setValue: displayValue => { string selectedId = noneOptionText; if (displayValue != noneOptionText) { selectedId = availablePackChoices.FirstOrDefault(id => (packsForChar.FirstOrDefault(p => p.VoicePackId.Equals(id, StringComparison.OrdinalIgnoreCase))?.VoicePackName ?? id).Equals(displayValue, StringComparison.OrdinalIgnoreCase)) ?? noneOptionText; } if (selectedId == noneOptionText || string.IsNullOrEmpty(selectedId)) { SelectedVoicePacks.Remove(currentCharacterName); this.Monitor.Log($"GMCM: Set {currentCharacterName} voice to None.", LogLevel.Trace); } else { SelectedVoicePacks[currentCharacterName] = selectedId; this.Monitor.Log($"GMCM: Set {currentCharacterName} voice to Pack ID: {selectedId} (Selected: '{displayValue}')", LogLevel.Trace); } }, allowedValues: displayChoices.ToArray());
                }
            }



            gmcm.AddSectionTitle(
                mod: this.ModManifest,
                text: () => this.Helper.Translation.Get("config.section.developer.name") 
            );

            gmcm.AddParagraph(
                mod: this.ModManifest,
                text: () => this.Helper.Translation.Get("config.dev-options.create-template-info") 
            );


            gmcm.AddBoolOption(
                mod: this.ModManifest,
                name: () => this.Helper.Translation.Get("config.developer-mode.name"),       
                tooltip: () => this.Helper.Translation.Get("config.developer-mode.tooltip"), 
                getValue: () => this.Config.developerModeOn,
                setValue: value => this.Config.developerModeOn = value
            );






            if (Config.developerModeOn)
            {
                this.Monitor.Log("GMCM setup complete.", LogLevel.Debug);
            }
                
        }


    } 
} 