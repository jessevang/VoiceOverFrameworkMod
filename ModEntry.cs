
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



        // Returns logical *pages* (each page can have 1–3 gender variants) and whether to reserve a follow-up serial for $q.
        private List<EventPage> ExtractVoicePagesFromDialogue(string rawText)
        {
            var pages = new List<EventPage>();
            if (string.IsNullOrWhiteSpace(rawText))
                return pages;

            string s = rawText;

            // --- A) If this is a $q branching block, keep only the spoken prompt text (before any '#$r' choices),
            //         and mark ReserveNextSerial so the caller emits a {CHOICE_REPLY} page after it.
            bool hasBranch = s.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasBranch)
            {
                int header = s.IndexOf("$q", StringComparison.OrdinalIgnoreCase);
                int firstHash = (header >= 0) ? s.IndexOf('#', header) : -1;
                if (firstHash >= 0)
                {
                    string afterHeader = s.Substring(firstHash + 1);
                    int nextChoice = afterHeader.IndexOf("#$r", StringComparison.Ordinal);
                    s = (nextChoice >= 0) ? afterHeader.Substring(0, nextChoice) : afterHeader;
                }
                else
                {
                    // malformed header: strip it and continue
                    s = Regex.Replace(s, @"#?\$q\s*[^#]*#", "", RegexOptions.CultureInvariant);
                }
            }

            // --- B) Drop any residual $r … choice blocks just in case
            s = Regex.Replace(
                s,
                @"#?\$r\s+\d+\s+-?\d+\s+\S+#.*?(?=(#?\$r\s+\d+\s+-?\d+\s+\S+#)|$)",
                "",
                RegexOptions.Singleline | RegexOptions.CultureInvariant
            );

            // --- C) Normalize page-breaks to '\n' (handles "#$b#", "#$b", "$b#", "$b", "##")
            s = Regex.Replace(s, @"#\s*\$b\s*#|#\s*\$b|\$b\s*#|\$b|#\s*#", "\n", RegexOptions.CultureInvariant);

            // Split into candidate pages
            var rawPages = s.Split('\n');

            foreach (var rawPage in rawPages)
            {
                string text = rawPage?.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                // --- D) Expand gender *token* form: ${male^female(^non-binary)}
                // We'll replace each token with its variant to build distinct page variants.
                List<string> expandedByToken = ExpandGenderTokens(text);

                // Now for each token-expanded variant, check the *caret* form used in event strings:
                // "malePage$textTags^femalePage"
                foreach (var tokenVariant in expandedByToken)
                {
                    // strip mood/pause tags AFTER we detect caret split, because caret can be adjacent to a $l
                    string caretSource = tokenVariant;

                    // Top-level caret split (2 or 3 parts). We consider it only if it splits the whole page,
                    // not inside a ${...} (already handled above).
                    string[] caretParts = caretSource.Split('^');
                    if (caretParts.Length >= 2 && caretParts.Length <= 3)
                    {
                        // Heuristic: treat as caret gender split when the split is "clean" (no stray quotes etc.)
                        // Clean & strip tags per-part
                        var variants = new List<string>();
                        foreach (var part in caretParts)
                        {
                            var stripped = StripTags(part);
                            if (!string.IsNullOrWhiteSpace(stripped))
                                variants.Add(stripped);
                        }
                        if (variants.Count >= 2)
                        {
                            pages.Add(new EventPage
                            {
                                Variants = variants,
                                ReserveNextSerial = hasBranch // carries from the original line
                            });
                            continue;
                        }
                    }

                    // No caret split → single-variant page
                    string single = StripTags(tokenVariant).Trim();
                    if (!string.IsNullOrEmpty(single))
                    {
                        pages.Add(new EventPage
                        {
                            Variants = new List<string> { single },
                            ReserveNextSerial = hasBranch
                        });
                    }
                }
            }

            return pages;
        }

        // Replace ${a^b(^c)} tokens with concrete variants. Used to set the gender to dialogue 
        private List<string> ExpandGenderTokens(string input)
        {
            // quickly check if any token exists
            if (input.IndexOf("${", StringComparison.Ordinal) < 0)
                return new List<string> { input };

            var tokenRegex = new Regex(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant);
            var queue = new Queue<string>();
            queue.Enqueue(input);

            var results = new List<string>();

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var m = tokenRegex.Match(cur);
                if (!m.Success)
                {
                    results.Add(cur);
                    continue;
                }

                string before = cur.Substring(0, m.Index);
                string inside = m.Groups[1].Value; // e.g., "male^female" or "m^f^nb"
                string after = cur.Substring(m.Index + m.Length);

                var options = inside.Split('^');
                foreach (var opt in options)
                {
                    queue.Enqueue(before + opt + after);
                }
            }
            

            return results;
        }

        // Remove mood/pause tags: $h,$s,$a,$l,$x[...], numeric $10, etc.
        private string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? "";

            string t = s;
            t = Regex.Replace(t, @"\$[a-zA-Z](\[[^\]]+\])?", "", RegexOptions.CultureInvariant); // $h, $l, $x[...]
            t = Regex.Replace(t, @"\$\d+", "", RegexOptions.CultureInvariant);                   // $10
            return t.Trim();
        }

        // Overload to support (out male, out female, out nonBinary)
        private bool TrySplitTopLevelGender(string raw, out string male, out string female, out string nonBinary)
        {
            male = female = nonBinary = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // --- 1) Full-line caret split: "<male version> ^ <female version>"
            // Heuristic: treat as gender split if there's a single top-level '^'
            // (good enough for event lines like "...$l^I didn't know I felt this way...")
            int caret = raw.IndexOf('^');
            if (caret > 0)
            {
                string left = raw.Substring(0, caret);
                string right = raw.Substring(caret + 1);

                male = CleanSingleLine(left);
                female = CleanSingleLine(right);
                // nonBinary remains null here (rare in vanilla for full-line caret form)
                return !string.IsNullOrEmpty(male) || !string.IsNullOrEmpty(female);
            }

            // --- 2) Token form: "${male^female}" or "${male^female^non-binary}"
            // Only treat it as a top-level split if the ENTIRE line is the token.
            if (raw.StartsWith("${") && raw.EndsWith("}"))
            {
                string inner = raw.Substring(2, raw.Length - 3);
                var parts = inner.Split('^');
                if (parts.Length == 2 || parts.Length == 3)
                {
                    male = CleanSingleLine(parts[0]);
                    female = CleanSingleLine(parts[1]);
                    if (parts.Length == 3)
                        nonBinary = CleanSingleLine(parts[2]);
                    return true;
                }
            }

            return false;
        }

        // If you already have this helper somewhere, keep yours and remove this one.
        private string CleanSingleLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return s;

            // strip mood/pause tokens like $l, $h, $s, $a, $x[...]
            s = Regex.Replace(s, @"\$[a-zA-Z](\[[^\]]+\])?", "", RegexOptions.CultureInvariant);
            // strip numeric pauses like $10
            s = Regex.Replace(s, @"\$\d+", "", RegexOptions.CultureInvariant);

            return s.Trim();
        }





        //get Event Dialogues dynamically so that modded events are also included

        private Dictionary<string, string> GetEventDialogueForCharacter(string targetCharacterName, string languageCode, IGameContentHelper gameContent)
        {
            Monitor.Log($"[EV_GEN] Scanning for event dialogue for '{targetCharacterName}'...", LogLevel.Info);

            var eventDialogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // existing regexes
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

                    if (Config.developerModeOn)
                        Monitor.Log($"[EV_GEN] ▶ Location='{location.NameOrUniqueName}' Event='{eventId}'", LogLevel.Trace);

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;

                    // EVENTS: no :pN, we emit :sN pages.
                    int speakSerialForTarget = -1;

                    int cmdIndex = -1;
                    foreach (string raw in commands)
                    {
                        cmdIndex++;
                        string command = raw.Trim();
                        if (command.Length == 0) continue;

                        // --------------- Case 1: speak <NPC> "..." ---------------
                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            if (Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] speak {speaker}: \"{Trunc(dialogueText)}\"", LogLevel.Trace);

                            if (IsCharacterMatch(speaker, targetCharacterName))
                            {
                                bool hasBranch = dialogueText.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0;

                                // Top-level gender fast-path: one page, multiple variants → SAME :sN
                                if (TrySplitTopLevelGender(dialogueText, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++; // bump ONCE for the page
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (speak) → {suffix} (m/f/nb where present)", LogLevel.Trace);
                                    }

                                    // done with this speak command
                                    continue;
                                }

                                // No gender split → split into spoken pages
                                var lines = ExtractVoiceLinesFromDialogue(dialogueText);
                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     gender=NO, $q={hasBranch}, pages={lines.Count}", LogLevel.Trace);

                                foreach (var line in lines)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";
                                    LogAdd(eventDialogue, location, eventId, suffix, line, ref foundInEventsCount);
                                }

                                if (hasBranch)
                                {
                                    // one extra page for the immediate post-choice reply
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";
                                    if (Config.developerModeOn)
                                        Monitor.Log($"[EV_GEN]     reserve reply {suffix}", LogLevel.Trace);
                                    LogAdd(eventDialogue, location, eventId, suffix, "{CHOICE_REPLY}", ref foundInEventsCount);
                                }
                            }
                            continue;
                        }

                        // --------------- Case 2: drawDialogue/message/showText "..." ---------------
                        var namedMatch = namedQuoteRegex.Match(command);
                        if (namedMatch.Success)
                        {
                            string possibleSpeaker = namedMatch.Groups[1].Value;
                            string dialogueText = namedMatch.Groups[2].Value;

                            if (!string.IsNullOrWhiteSpace(possibleSpeaker))
                                lastSpeaker = possibleSpeaker;

                            if (Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] {possibleSpeaker}*: \"{Trunc(dialogueText)}\"", LogLevel.Trace);

                            if (IsCharacterMatch(lastSpeaker, targetCharacterName))
                            {
                                // Gender fast-path first (single page with m^f[^nb])
                                if (TrySplitTopLevelGender(dialogueText, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++;
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (named) → {suffix} (m/f/nb where present)", LogLevel.Trace);

                                        continue;
                                    }
                                }

                                // Otherwise, extract pages (may include multiple pages)
                                var pages = ExtractVoicePagesFromDialogue(dialogueText);

                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     pages={pages.Count}", LogLevel.Trace);

                                foreach (var page in pages)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";

                                    // Each page can have 1..3 variants (gender) *if* your extractor returns them.
                                    // Since variants here aren't labeled, we store without explicit genderTag.
                                    for (int i = 0; i < page.Variants.Count; i++)
                                    {
                                        string v = page.Variants[i];
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       variant[{i}] -> {suffix}: \"{Trunc(v)}\"", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, suffix, v, ref foundInEventsCount);
                                    }

                                    if (page.ReserveNextSerial)
                                    {
                                        speakSerialForTarget++;
                                        string replyKey = $":s{speakSerialForTarget}";
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       reserve reply {replyKey}", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, replyKey, "{CHOICE_REPLY}", ref foundInEventsCount);
                                    }
                                }
                            }
                            continue;
                        }

                        // --------------- Case 3: Generic quoted text under lastSpeaker ---------------
                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch(lastSpeaker, targetCharacterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
                            if (genericMatches.Count > 0 && Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] generic under '{lastSpeaker}': found {genericMatches.Count} quoted blocks", LogLevel.Trace);

                            foreach (Match m in genericMatches)
                            {
                                string chunk = m.Groups[1].Value.Trim();
                                if (chunk.Length <= 3) continue;

                                // Gender fast-path if the whole chunk is a single page with ^ split
                                if (TrySplitTopLevelGender(chunk, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++;
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (generic) → {suffix} (m/f/nb where present)", LogLevel.Trace);

                                        continue;
                                    }
                                }

                                // Otherwise, page-extract within the chunk
                                var pages = ExtractVoicePagesFromDialogue(chunk);

                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     generic pages={pages.Count}", LogLevel.Trace);

                                foreach (var page in pages)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";

                                    for (int i = 0; i < page.Variants.Count; i++)
                                    {
                                        string v = page.Variants[i];
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       variant[{i}] -> {suffix}: \"{Trunc(v)}\"", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, suffix, v, ref foundInEventsCount);
                                    }

                                    if (page.ReserveNextSerial)
                                    {
                                        speakSerialForTarget++;
                                        string replyKey = $":s{speakSerialForTarget}";
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       reserve reply {replyKey}", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, replyKey, "{CHOICE_REPLY}", ref foundInEventsCount);
                                    }
                                }
                            }
                        }
                    } // foreach command
                } // foreach event
            } // foreach location

            Monitor.Log($"[EV_GEN] Done. Found {foundInEventsCount} event dialogue lines for '{targetCharacterName}'.", LogLevel.Info);
            return eventDialogue;

            // ---- local helpers used by this method ----

            void LogAdd(Dictionary<string, string> dict, GameLocation loc, string evId, string suff, string text, ref int ctr)
            {
                AddEventDialogue(dict, loc, evId, text, ref ctr, suff, genderTag: null);
                if (Config.developerModeOn)
                    Monitor.Log($"[EV_GEN]       add {loc.NameOrUniqueName}/{evId}{suff} → \"{Trunc(text)}\"", LogLevel.Trace);
            }
        }



        // ------- tiny helpers for logging GetEventDialogueForCharacter()-------

        private string Trunc(string s, int max = 140)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\n", "\\n");
            return (s.Length <= max) ? s : (s.Substring(0, max) + "…");
        }

        private void LogAdd(Dictionary<string, string> dict, GameLocation location, string eventId, string suffix, string text, ref int counter)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string baseKey = $"Event:{location.NameOrUniqueName}/{eventId}{suffix}";
            string uniqueKey = baseKey;
            int idx = 1;
            while (dict.ContainsKey(uniqueKey))
                uniqueKey = $"{baseKey}_{idx++}";

            dict[uniqueKey] = text;
            counter++;

            if (Config.developerModeOn)
                Monitor.Log($"[EV_GEN]         ADD {uniqueKey}  ←  \"{Trunc(text)}\"", LogLevel.Trace);
        }







        // Splits complex dialogue into separate lines, preserving event page breaks. Fixed issue with Events not splitting properly M and F lines
        private List<string> ExtractVoiceLinesFromDialogue(string rawText)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(rawText))
                return results;

            string s = rawText;

            // 1) If this is a $q branching block, keep only the spoken prompt text (before any '#$r' choices)
            int qIdx = s.IndexOf("$q", StringComparison.OrdinalIgnoreCase);
            if (qIdx >= 0)
            {
                int firstHash = s.IndexOf('#', qIdx);
                if (firstHash >= 0)
                {
                    string afterHeader = s.Substring(firstHash + 1); // prompt + rest
                    int nextChoice = afterHeader.IndexOf("#$r", StringComparison.Ordinal);
                    s = nextChoice >= 0 ? afterHeader.Substring(0, nextChoice) : afterHeader;
                }
                else
                {
                    // malformed $q: strip header if present
                    s = Regex.Replace(s, @"#?\$q\s*[^#]*#", "", RegexOptions.CultureInvariant);
                }
            }

            // 2) Drop any residual $r choice blocks if they remain (safety)
            s = Regex.Replace(
                s,
                @"#?\$r\s+\d+\s+-?\d+\s+\S+#.*?(?=(#?\$r\s+\d+\s+-?\d+\s+\S+#)|$)",
                "",
                RegexOptions.Singleline | RegexOptions.CultureInvariant
            );

            // 3) Normalize page-break markers to newlines
            s = Regex.Replace(
                s,
                @"#\s*\$b\s*#|#\s*\$b|\$b\s*#|\$b|#\s*#",
                "\n",
                RegexOptions.CultureInvariant
            );

            // 4) Strip mood/pause tokens and numeric pauses
            s = Regex.Replace(s, @"\$[a-zA-Z](\[[^\]]+\])?", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\$\d+", "", RegexOptions.CultureInvariant);

            // 5) Split into lines; if a line contains '^', emit both gender variants (same serial)
            foreach (var part in s.Split('\n'))
            {
                string line = part.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                int caret = line.IndexOf('^');
                if (caret >= 0)
                {
                    string male = line.Substring(0, caret).Trim();
                    string female = line.Substring(caret + 1).Trim();

                    if (!string.IsNullOrEmpty(male)) results.Add(male);    // will get :sN
                    if (!string.IsNullOrEmpty(female)) results.Add(female);  // will also get :sN (duplicate DialogueFrom => will dedupe with _1)
                }
                else
                {
                    results.Add(line);
                }
            }

            return results;
        }



        private sealed class EventPage
        {
            public List<string> Variants = new List<string>(); // one page, 1–3 variants (male/female/nb)
            public bool ReserveNextSerial;                     // true if this page is a $q prompt; we emit a follow-up {CHOICE_REPLY}
        }





        // Helper method to add and deduplicate dialogue

        private void AddEventDialogue(
            Dictionary<string, string> dict,
            GameLocation location,
            string eventId,
            string sanitizedText,
            ref int counter,
            string suffix = "",
            string genderTag = null // "male" | "female" | "nonbinary" | null
        )
        {
            if (string.IsNullOrWhiteSpace(sanitizedText))
                return;

            // Public TK stays "Event:<loc>/<id>:sN"
            string baseKey = $"Event:{location.NameOrUniqueName}/{eventId}{suffix}";

            // Internal storage key is unique per gender variant, so we don't create :sN_1
            string storageKey = genderTag is null ? baseKey : $"{baseKey}|g={genderTag}";

            dict[storageKey] = sanitizedText;
            counter++;
        }

        // Helper method for fuzzy speaker name matching
        private bool IsCharacterMatch(string nameToTest, string targetName)
        {
            if (string.IsNullOrWhiteSpace(nameToTest))
                return false;

            return nameToTest.StartsWith(targetName, StringComparison.OrdinalIgnoreCase);
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


        /*
         //Get Festival Data V1
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

         */


        // Gets dialogue from Data/NPCGiftTastes.json for a character.
        // V2: attaches synthetic segment keys:
        //   TranslationKey = "Data/NPCGiftTastes:{Character}:s{n}"
        //   SourceInfo     = "NPCGiftTastes/{Character}:s{n}"
        // We pull every EVEN segment (text) from the slash-separated value payload.
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetGiftTasteDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo, string TranslationKey)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/NPCGiftTastes{langSuffix}";
            const string sourceRoot = "NPCGiftTastes";

            try
            {
                IAssetName assetName = contentHelper.ParseAssetName(assetKeyString);
                var giftTasteData = contentHelper.Load<Dictionary<string, string>>(assetName);

                if (giftTasteData != null && giftTasteData.TryGetValue(characterName, out string combined))
                {
                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        string[] segments = combined.Split('/');
                        int outIndex = 0; // count only text segments (even indices)

                        for (int i = 0; i < segments.Length; i++)
                        {
                            if (i % 2 != 0) continue; // only even => text
                            string text = (segments[i] ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(text)) continue;

                            string segId = $"s{outIndex}";
                            string sourceInfo = $"{sourceRoot}/{characterName}:{segId}";
                            string tk = $"Data/NPCGiftTastes:{characterName}:{segId}";
                            dialogueList.Add((text, sourceInfo, tk));
                            outIndex++;
                        }

                        if (this.Config.developerModeOn)
                            this.Monitor.Log($"    -> Extracted {outIndex} gift taste dialogue segments for '{characterName}' from {assetKeyString}.", LogLevel.Trace);
                    }
                }
            }
            catch (ContentLoadException) { /* skip missing */ }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/processing '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return dialogueList;
        }



        // Gets dialogue from Data/EngagementDialogue.json for a character.
        // V2: returns (RawText, SourceInfo, TranslationKey) per matching key:
        //   TK = "Data/EngagementDialogue:{jsonKey}"
        //   SourceInfo = "EngagementDialogue/{jsonKey}"
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetEngagementDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo, string TranslationKey)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/EngagementDialogue{langSuffix}";

            try
            {
                IAssetName assetName = contentHelper.ParseAssetName(assetKeyString);
                var engagementData = contentHelper.Load<Dictionary<string, string>>(assetName);

                if (engagementData != null)
                {
                    foreach (var kvp in engagementData)
                    {
                        string jsonKey = kvp.Key ?? "";
                        string val = kvp.Value ?? "";
                        if (string.IsNullOrWhiteSpace(val))
                            continue;

                        if (!jsonKey.StartsWith(characterName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string sourceInfo = $"EngagementDialogue/{jsonKey}";
                        string tk = $"Data/EngagementDialogue:{jsonKey}";
                        dialogueList.Add((val, sourceInfo, tk));
                    }
                }
            }
            catch (ContentLoadException) { /* skip missing */ }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error loading/processing '{assetKeyString}': {ex.Message}", LogLevel.Warn);
                this.Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
            return dialogueList;
        }




        // Gets dialogue from Data/ExtraDialogue.json for a character.
        // V2: now returns (RawText, SourceInfo, TranslationKey) where TK = "Data/ExtraDialogue:{jsonKey}".
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetExtraDialogueForCharacter(string characterName, string languageCode, IGameContentHelper contentHelper)
        {
            var dialogueList = new List<(string RawText, string SourceInfo, string TranslationKey)>();
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            string assetKeyString = $"Data/ExtraDialogue{langSuffix}";

            // Pre-calc patterns for matching
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
                        string key = kvp.Key ?? "";
                        string val = kvp.Value ?? "";
                        if (string.IsNullOrWhiteSpace(val))
                            continue;

                        // Match by exact / prefix / suffix / infix (case-insensitive)
                        bool isMatch =
                            key.Equals(characterName, StringComparison.OrdinalIgnoreCase) ||
                            key.StartsWith(prefixPattern, StringComparison.OrdinalIgnoreCase) ||
                            key.EndsWith(suffixPattern, StringComparison.OrdinalIgnoreCase) ||
                            key.IndexOf(infixPattern, StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!isMatch)
                            continue;

                        string sourceInfo = $"ExtraDialogue/{key}";
                        string tk = $"Data/ExtraDialogue:{key}";
                        dialogueList.Add((val, sourceInfo, tk));
                    }
                }
            }
            catch (ContentLoadException) { /* skip missing */ }
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