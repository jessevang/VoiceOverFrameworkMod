using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private readonly Dictionary<string, List<DialogueRef>> _lastListByNpc =
            new Dictionary<string, List<DialogueRef>>(StringComparer.OrdinalIgnoreCase);

        // cache for generic string sheets
        private readonly Dictionary<string, List<SheetEntry>> _lastSheetEntriesByAsset =
            new(StringComparer.OrdinalIgnoreCase);

        // cache for parsed event lines per location asset (e.g., "Town", "Beach")
        private readonly Dictionary<string, List<EventLine>> _lastEventLinesByLocation =
            new(StringComparer.OrdinalIgnoreCase);

        // A curated default set of generic string sheets to iterate when user runs: test_sheet all <delay>
        private static readonly string[] DefaultSheetAssets = new[]
        {
            "Strings/SpeechBubbles",
            "Data/ExtraDialogue",
            "Strings/StringsFromCSFiles",
            // Add more if you want them included by default
        };

        private const string InlineDialogueKey = "Strings\\Characters:VOFInline";
        // A canonical set of vanilla festivals; modded festivals still work if present.
        private static readonly string[] DefaultFestivalIds = new[]
        {
            "spring13","spring24","summer11","summer28","fall16","fall27","winter8","winter25"
        };



        // A curated list of standard event location asset names (Data/Events/<Name>)
        private static readonly string[] DefaultEventLocations = new[]
        {
            "AbandonedJojaMart","AnimalShop","ArchaeologyHouse","Backwoods","BathHouse_Pool",
            "Beach","BoatTunnel","BusStop","CommunityCenter","DesertFestival","ElliottHouse",
            "Farm","FarmHouse","FishShop","Forest","HaleyHouse","HarveyRoom","Hospital",
            "IslandFarmHouse","IslandHut","IslandNorth","IslandSouth","IslandWest","JoshHouse",
            "LeahHouse","ManorHouse","Mine","Mountain","QiNutRoom","Railroad","Saloon",
            "SamHouse","SandyHouse","ScienceHouse","SebastianRoom","SeedShop","Sewer","Sunroom",
            "Temp","Tent","Town","Trailer","Trailer_Big","WizardHouse","Woods"
        };

        // Return keyed, non-event festival dialogue for an NPC as a "sheet" (key → raw).
        // Keys are stable "festId:FullKey" (e.g., "summer11:Abigail_y2").
        private Dictionary<string, string> LoadFestivalNonEventForNpc(string npcName)
        {
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(npcName)) return results;

            // Common vanilla suffixes. (Keep small; you can expand later.)
            var suffixes = new[] { "", "_spouse", "_y2", "_spouse_y2" };

            foreach (var festId in DefaultFestivalIds)
            {
                Dictionary<string, string> dict = null;
                try
                {
                    dict = this.Helper.GameContent.Load<Dictionary<string, string>>($"Data/Festivals/{festId}");
                }
                catch { /* ignore missing */ }

                if (dict == null || dict.Count == 0)
                    continue;

                foreach (var sfx in suffixes)
                {
                    var fullKey = sfx.Length == 0 ? npcName : npcName + sfx;
                    if (!dict.TryGetValue(fullKey, out var raw) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    // Stable, unique within all festivals
                    results[$"{festId}:{fullKey}"] = raw;
                }
            }

            return results;
        }


        // ============== Command Registration ==============
        private void DialogueTester(ICommandHelper commands)
        {
            commands.Add(
                name: "test_dialogue",
                documentation:
                    "Usage: test_dialogue <NPCName> [delayMs] [filter] [includeChoices]\n" +
                    "Auto-plays ALL dialogue KEYS for the NPC (base + marriage).",
                callback: this.TestDialogue
            );

            commands.Add(
                name: "test_dialogue_from",
                documentation:
                    "Usage: test_dialogue_from <NPCName> <startIndex> [delayMs] [filter] [includeChoices]\n" +
                    "Resume auto-play from a Primary Index ID shown by list_dialogue.",
                callback: this.TestDialogueFrom
            );

            commands.Add(
                name: "list_dialogue",
                documentation:
                    "Usage: list_dialogue <NPCName> [filter] [includeChoices]\n" +
                    "List all dialogue KEYS (base + marriage) with Primary Index IDs.",
                callback: this.ListDialogue
            );

            commands.Add(
                name: "play_dialogue",
                documentation:
                    "Usage: play_dialogue <NPCName> <index>\n" +
                    "Play one entry by Primary Index ID from the last/cached list.",
                callback: this.PlayDialogueByIndex
            );

            commands.Add(
                name: "play_dialogue_key",
                documentation:
                    "Usage: play_dialogue_key <NPCName> <sheetLabel> <key>\n" +
                    "Play one entry by sheet label + key. (labels shown in list_dialogue)",
                callback: this.PlayDialogueByKey
            );

            commands.Add("vof.summary", "Print summary of unmatched V2 lines (and clear).", (c, a) => PrintV2FailureReport());

            // ===== BUBBLES: list / test / play (bubble-only) =====
            commands.Add(
                name: "list_bubbles",
                documentation:
                    "Usage: list_bubbles [filter]\n" +
                    "List keys from the vanilla 'Strings/SpeechBubbles' sheet.",
                callback: this.ListBubbles
            );

            commands.Add(
                name: "test_bubbles",
                documentation:
                    "Usage: test_bubbles <all|filter> [delayMs]\n" +
                    "• all         → play ALL 'Strings/SpeechBubbles' entries as speech bubbles only\n" +
                    "• <filter>    → plays only entries whose key OR text contains the filter\n" +
                    "Examples:\n" +
                    "  test_bubbles all 100    (play everything with 100ms gap)\n" +
                    "  test_bubbles hello 800  (only entries matching 'hello', 800ms gap)",
                callback: this.TestBubbles
            );

            commands.Add(
                name: "play_bubble_key",
                documentation:
                    "Usage: play_bubble_key <key>\n" +
                    "Play a single bubble line by key from 'Strings/SpeechBubbles' (bubble only, no dialogue window).",
                callback: this.PlayBubbleByKey
            );



            // ===== Event string testing (Data/Events/<Location>) =====
            commands.Add(
                name: "list_events",
                documentation:
                    "Usage: list_events <locationAssetName> [filter]\n" +
                    "List extracted 'speak' and bubble lines from an events sheet, e.g., Data/Events/Beach -> 'Beach'.",
                callback: this.ListEvents
            );

            commands.Add(
                name: "test_events",
                documentation:
                    "Usage: test_events <locationAssetName|all|characterName> [delayMs] [filter]\n" +
                    "• all         → test all standard event locations\n" +
                    "• Town        → test that one location\n" +
                    "• Abigail     → test *all* locations but only lines where speaker is 'Abigail'",
                callback: this.TestEvents
            );

            commands.Add(
                name: "play_event_line",
                documentation:
                    "Usage: play_event_line <locationAssetName> <index>\n" +
                    "Play one extracted event line by index (use list_events first).",
                callback: this.PlayEventLineByIndex
            );
        }




        //================Test Bubbles=======================
       

        private void ListBubbles(string cmd, string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;

            var entries = LoadSpeechBubbles(filter);
            if (entries.Count == 0)
            {
                this.Monitor.Log($"No entries found in 'Strings/SpeechBubbles' (filter='{filter ?? "(none)"}').", LogLevel.Info);
                return;
            }

            _lastSheetEntriesByAsset["Strings/SpeechBubbles"] = entries;

            this.Monitor.Log($"--- SpeechBubbles keys (total {entries.Count}) ---", LogLevel.Info);
            for (int i = 0; i < entries.Count; i++)
                this.Monitor.Log($"{i,4}: {entries[i].Key}", LogLevel.Info);
        }

        private async void TestBubbles(string cmd, string[] args)
        {
            // Parse args
            string filterArg = args.Length > 0 ? args[0] : "all";
            int delay = (args.Length > 1 && int.TryParse(args[1], out var d)) ? Math.Max(1, d) : 1000;
            string filter = filterArg.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : filterArg;

            // Load bubble lines from Strings/SpeechBubbles
            var entries = LoadSpeechBubbles(filter);
            if (entries == null || entries.Count == 0)
            {
                Monitor.Log($"[BUBBLES] No lines found in Strings/SpeechBubbles (filter='{filter ?? "(none)"}').", LogLevel.Info);
                return;
            }

            var loc = Game1.player.currentLocation;
            if (loc == null)
            {
                Monitor.Log("[BUBBLES] No current location.", LogLevel.Warn);
                return;
            }

            bool didWarp = false;
            bool spawnedTemp = false;
            NPC tempLewis = null;

            // Make sure we have visible villagers near the player.
            List<NPC> villagersHere = loc.characters?
                .OfType<NPC>()
                .Where(n => n.IsVillager && !n.IsMonster)
                .ToList() ?? new List<NPC>();

            if (villagersHere.Count == 0)
            {
                // Try warping everyone TO the player
                WarpAllVillagersToPlayer();
                didWarp = true;

                villagersHere = Game1.player.currentLocation.characters?
                    .OfType<NPC>()
                    .Where(n => n.IsVillager && !n.IsMonster)
                    .ToList() ?? new List<NPC>();
            }

            if (villagersHere.Count == 0)
            {
                // Still nobody? Spawn a temp Lewis
                tempLewis = Game1.getCharacterFromName("Lewis", true);
                if (tempLewis != null)
                {
                    tempLewis.currentLocation = loc;
                    if (!loc.characters.Contains(tempLewis))
                        loc.addCharacter(tempLewis);

                    int px = Game1.player.TilePoint.X;
                    int py = Game1.player.TilePoint.Y;
                    tempLewis.setTileLocation(new Vector2(px + 1, py));
                    villagersHere.Add(tempLewis);
                    spawnedTemp = true;
                }
            }

            if (villagersHere.Count == 0)
            {
                Monitor.Log("[BUBBLES] Couldn’t stage any NPC in the current area.", LogLevel.Warn);
                if (didWarp) RestoreWarpedVillagers();
                return;
            }
            _collectV2Failures = true;
           var _bubbleSummarySeen = new HashSet<string>(StringComparer.Ordinal); 
            try
            {
                Monitor.Log($"[BUBBLES] Playing {entries.Count} bubble(s) targeted to their NPC (delay={delay}ms, filter='{filter ?? "all"}')…", LogLevel.Info);

                foreach (var e in entries)
                {
                    // figure out who should speak this line from the key
                    var npcName = ParseNpcFromBubbleKey(e.Key);
                    if (string.IsNullOrWhiteSpace(npcName))
                    {
                        Monitor.Log($"[BUBBLES] Couldn’t parse NPC from key '{e.Key}', skipping.", LogLevel.Trace);
                        continue;
                    }

                    var speaker = GetOrStageNpcForTest(npcName);
                    if (speaker == null)
                    {
                        Monitor.Log($"[BUBBLES] Couldn’t stage NPC '{npcName}' for key '{e.Key}', skipping.", LogLevel.Trace);
                        continue;
                    }

                    // bubble only; no DialogueBox
                    EmitBubbleOnly(speaker, e.Value);

                    await Task.Delay(delay);
                }
            }
            finally
            {
                PrintV2FailureReport();
                _collectV2Failures = false;
                _bubbleSummarySeen = null;

                // put everyone back
                CleanupStagedNpcs();
            }





            Monitor.Log("[BUBBLES] Done.", LogLevel.Info);
        }

        private List<SheetEntry> LoadSpeechBubbles(string filter)
        {
            return LoadStringSheet("Strings/SpeechBubbles", filter);
        }

        private List<SheetEntry> LoadStringSheet(string assetPath, string filter)
        {
            var results = new List<SheetEntry>();
            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>(assetPath);
                if (dict == null || dict.Count == 0)
                    return results;

                IEnumerable<KeyValuePair<string, string>> q = dict;
                if (!string.IsNullOrWhiteSpace(filter))
                    q = q.Where(kv =>
                        (kv.Key?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                        (kv.Value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

                int i = 0;
                foreach (var kv in q.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    results.Add(new SheetEntry { Index = i++, Key = kv.Key, Value = kv.Value ?? "" });
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to read string sheet '{assetPath}': {ex.Message}", LogLevel.Trace);
            }
            return results;
        }

        private readonly List<(NPC Npc, GameLocation Location, Vector2 Tile)> _warpBackup = new();

        private void WarpAllVillagersToPlayer(int columns = 6, int rowSpacing = 1)
        {
            _warpBackup.Clear();

            var targetLoc = Game1.player.currentLocation;
            if (targetLoc == null)
                return;

            // collect all villagers from all locations
            var villagers = new List<NPC>();
            foreach (var loc in Game1.locations)
            {
                if (loc?.characters == null) continue;
                foreach (var c in loc.characters)
                {
                    if (c is NPC n && n.IsVillager && !n.IsMonster)
                        villagers.Add(n);
                }
            }

            if (villagers.Count == 0)
                return;

            int px = Game1.player.TilePoint.X;
            int py = Game1.player.TilePoint.Y;

            int col = 0, row = 0;

            foreach (var npc in villagers)
            {
                try
                {
                    var originalLoc = npc.currentLocation;
                    var originalTile = npc.Tile;

                    // remember where they were
                    _warpBackup.Add((npc, originalLoc, originalTile));

                    // ensure they are in the player's location list
                    if (originalLoc != targetLoc)
                    {
                        try { originalLoc?.characters?.Remove(npc); } catch { }
                        npc.currentLocation = targetLoc;
                        if (!targetLoc.characters.Contains(npc))
                            targetLoc.addCharacter(npc);
                    }

                    // place on a grid around the player
                    int tx = px + 2 + col;          // start a little to the right of player
                    int ty = py + row * rowSpacing; // rows beneath/above as needed

                    npc.setTileLocation(new Vector2(tx, ty));
                    npc.faceTowardFarmerForPeriod(1, 1, false, Game1.player);

                    col++;
                    if (col >= columns)
                    {
                        col = 0;
                        row++;
                    }
                }
                catch { /* keep going */ }
            }
        }

        private void RestoreWarpedVillagers()
        {
            // put everyone back where they came from
            foreach (var (npc, originalLoc, originalTile) in _warpBackup)
            {
                try
                {
                    var currentLoc = npc.currentLocation;
                    if (currentLoc != null && currentLoc != originalLoc)
                    {
                        try { currentLoc.characters?.Remove(npc); } catch { }
                    }

                    npc.currentLocation = originalLoc;
                    if (originalLoc != null && !originalLoc.characters.Contains(npc))
                        originalLoc.addCharacter(npc);

                    npc.setTileLocation(originalTile);
                }
                catch { /* best effort */ }
            }

            _warpBackup.Clear();
        }


        private void PlayBubbleByKey(string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                this.Monitor.Log("Usage: play_bubble_key <key>", LogLevel.Info);
                return;
            }

            string key = args[0];

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>("Strings/SpeechBubbles");
                if (dict == null || !dict.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                {
                    this.Monitor.Log($"Key '{key}' not found in 'Strings/SpeechBubbles'.", LogLevel.Warn);
                    return;
                }

                var speaker = DefaultSpeaker() ?? Game1.getCharacterFromName("Lewis", true);
                if (speaker == null)
                {
                    this.Monitor.Log("No available NPC to speak this bubble.", LogLevel.Warn);
                    return;
                }

                EmitBubbleOnly(speaker, raw);
                Game1.addHUDMessage(new HUDMessage($"(Strings/SpeechBubbles:{key})"));
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to load 'Strings/SpeechBubbles': {ex.Message}", LogLevel.Warn);
            }
        }

        private void EmitBubbleOnly(NPC speaker, string rawText, int durationMs = 1800)
        {
            try
            {
                if (speaker == null || string.IsNullOrWhiteSpace(rawText))
                    return;

                // format placeholders the same way vanilla code would
                // {0} => farmer name, {1} => farm name
                string farmerName = Utility.FilterUserName(Game1.player?.Name) ?? "";
                string farmName = Utility.FilterUserName(Game1.player?.farmName?.Value) ?? "";

                string formatted = rawText;
                try
                {
                    // string.Format will leave it unchanged if no placeholders exist
                    formatted = string.Format(rawText, farmerName, farmName);
                }
                catch
                {
                    // if malformed placeholders slip in, fall back to raw text
                    formatted = rawText;
                }

                // bubble only; do NOT open Dialogue UI
                speaker.showTextAboveHead(text: formatted, spriteTextColor: null, duration: durationMs);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"EmitBubbleOnly failed: {ex}", LogLevel.Trace);
            }
        }







        // ===================== Commands =====================

        private async void TestDialogue(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                this.Monitor.Log("Usage: test_dialogue <NPCName|all> [delayMs] [filter] [includeChoices]", LogLevel.Info);
                return;
            }

            string target = args[0];
            int delay = (args.Length > 1 && int.TryParse(args[1], out var d)) ? Math.Max(1, d) : 1200;
            string filter = args.Length > 2 ? args[2] : null;
            bool includeChoices = args.Length > 3 && args[3].Equals("includeChoices", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                _collectV2Failures = true;
                try
                {
                    await TestDialogueAll(delay, filter, includeChoices);
                }
                finally
                {
                    _collectV2Failures = false;
                    PrintV2FailureReport();
                }
                return;
            }

            string npcName = target;
            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            var sheets = LoadAllNpcDialogueSheets(npcName);
            if (sheets.Count == 0)
            {
                this.Monitor.Log($"No dialogue found for '{npcName}'.", LogLevel.Warn);
                return;
            }

            var list = BuildKeyList(sheets, filter, includeChoices);
            if (list.Count == 0)
            {
                this.Monitor.Log($"No keys matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }

            _lastListByNpc[npcName] = list;
            SaveListCache(npcName, list);

            AppendRunLog(npcName, $"TEST keys start (0..{list.Count - 1}) delay={delay} filter='{filter ?? "(none)"}' includeChoices={includeChoices}");
            this.Monitor.Log($"Auto-playing {list.Count} KEYS for {npcName} (delay={delay}ms).", LogLevel.Info);

            _collectV2Failures = true;
            try
            {
                await PlayKeyListRange(npc, list, startIndex: 0, delayMs: delay);
            }
            finally
            {
                PrintV2FailureReport();
                _collectV2Failures = false;
            }

            this.Monitor.Log("Finished.", LogLevel.Info);
        }


     


        // Run all NPCs that have dialogue sheets (base or marriage), one after another.
        private async Task TestDialogueAll(int delay, string filter, bool includeChoices)
        {
            _v2Fails.Clear();

            var names = GetAllNpcNames();
            if (names == null || names.Count == 0)
            {
                this.Monitor.Log("No NPC names found (Data/NPCDispositions).", LogLevel.Warn);
                return;
            }

            int npcRan = 0, totalKeys = 0;

            this.Monitor.Log($"[ALL] Starting dialogue test for {names.Count} NPC entries… (delay={delay}ms, filter='{filter ?? "(none)"}', includeChoices={includeChoices})", LogLevel.Info);

            foreach (var npcName in names)
            {
                var sheets = LoadAllNpcDialogueSheets(npcName);
                if (sheets.Count == 0)
                    continue;

                var list = BuildKeyList(sheets, filter, includeChoices);
                if (list.Count == 0)
                    continue;

                var npc = Game1.getCharacterFromName(npcName, true);
                if (npc == null)
                {
                    this.Monitor.Log($"[ALL] Skipping '{npcName}': NPC instance not found.", LogLevel.Trace);
                    continue;
                }

                _lastListByNpc[npcName] = list;
                SaveListCache(npcName, list);

                AppendRunLog(npcName, $"ALL_RUN keys start (0..{list.Count - 1}) delay={delay} filter='{filter ?? "(none)"}' includeChoices={includeChoices}");
                this.Monitor.Log($"[ALL] {npcName}: {list.Count} keys…", LogLevel.Info);

                await PlayKeyListRange(npc, list, startIndex: 0, delayMs: delay);
                Game1.exitActiveMenu();

                npcRan++;
                totalKeys += list.Count;
            }

            this.Monitor.Log($"[ALL] Completed. NPCs run: {npcRan}, total keys: {totalKeys}.", LogLevel.Info);
        }

        private List<string> GetAllNpcNames()
        {
            try
            {
                var dispo = this.Helper.GameContent.Load<Dictionary<string, string>>("Data/NPCDispositions");
                if (dispo != null && dispo.Count > 0)
                {
                    return dispo.Keys
                                .Where(k => !string.IsNullOrWhiteSpace(k))
                                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                }
                else
                {
                    this.Monitor.Log("[ALL] Data/NPCDispositions returned empty; falling back to vanilla list.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"[ALL] Could not load Data/NPCDispositions (modded list). Falling back to vanilla. Details: {ex.Message}", LogLevel.Trace);
            }

            var vanilla = new[]
            {
                "Abigail","Alex","Caroline","Clint","Demetrius","Dwarf","Elliott","Emily","Evelyn","George",
                "Gus","Haley","Harvey","Jas","Jodi","Kent","Krobus","Leah","Leo","Lewis","Linus","Marnie",
                "Maru","Pam","Penny","Pierre","Robin","Sam","Sandy","Sebastian","Shane","Vincent","Willy","Wizard"
            };

            return vanilla.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async void TestDialogueFrom(string cmd, string[] args)
        {
            if (args.Length < 2)
            {
                this.Monitor.Log("Usage: test_dialogue_from <NPCName> <startIndex> [delayMs] [filter] [includeChoices]", LogLevel.Info);
                return;
            }

            string npcName = args[0];
            if (!int.TryParse(args[1], out int startIndex) || startIndex < 0)
            {
                this.Monitor.Log("startIndex must be a non-negative integer.", LogLevel.Warn);
                return;
            }

            int delay = (args.Length > 2 && int.TryParse(args[2], out var d)) ? Math.Max(1, d) : 1200;
            string filter = args.Length > 3 ? args[3] : null;
            bool includeChoices = args.Length > 4 && args[4].Equals("includeChoices", StringComparison.OrdinalIgnoreCase);

            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            var list = GetOrRebuildKeyList(npcName, filter, includeChoices);
            if (list == null || list.Count == 0)
            {
                this.Monitor.Log($"No keys matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }
            if (startIndex >= list.Count)
            {
                this.Monitor.Log($"startIndex {startIndex} is out of range (0..{list.Count - 1}).", LogLevel.Warn);
                return;
            }

            AppendRunLog(npcName, $"TEST_FROM keys start={startIndex} (..{list.Count - 1}) delay={delay} filter='{filter ?? "(none)"}' includeChoices={includeChoices}");
            this.Monitor.Log($"Resuming at index {startIndex} of {list.Count} KEYS for {npcName} (delay={delay}ms).", LogLevel.Info);

            _collectV2Failures = true;
            try
            {
                await PlayKeyListRange(npc, list, startIndex, delay);
            }
            finally
            {
                _collectV2Failures = false;
            }

            this.Monitor.Log("Finished.", LogLevel.Info);
        }

        private void ListDialogue(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                this.Monitor.Log("Usage: list_dialogue <NPCName> [filter] [includeChoices]", LogLevel.Info);
                return;
            }

            string npcName = args[0];
            string filter = args.Length > 1 ? args[1] : null;
            bool includeChoices = args.Length > 2 && args[2].Equals("includeChoices", StringComparison.OrdinalIgnoreCase);

            var sheets = LoadAllNpcDialogueSheets(npcName);
            if (sheets.Count == 0)
            {
                this.Monitor.Log($"No dialogue found for '{npcName}'.", LogLevel.Warn);
                return;
            }

            var list = BuildKeyList(sheets, filter, includeChoices);
            if (list.Count == 0)
            {
                this.Monitor.Log($"No keys matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }

            _lastListByNpc[npcName] = list;
            SaveListCache(npcName, list);

            this.Monitor.Log($"--- Dialogue KEY list for {npcName} (total {list.Count}) ---", LogLevel.Info);
            foreach (var e in list)
                this.Monitor.Log($"{e.PrimaryId,4}: ({e.SheetLabel}:{e.Key})", LogLevel.Info);

            this.Monitor.Log($"Use: play_dialogue {npcName} <index>   or   test_dialogue_from {npcName} <startIndex>", LogLevel.Info);
        }

        

        private void PlayDialogueByIndex(string cmd, string[] args)
        {
            if (args.Length < 2)
            {
                this.Monitor.Log("Usage: play_dialogue <NPCName> <index>", LogLevel.Info);
                return;
            }

            string npcName = args[0];
            if (!int.TryParse(args[1], out int index) || index < 0)
            {
                this.Monitor.Log("Index must be a non-negative integer.", LogLevel.Warn);
                return;
            }

            var list = GetOrLoadCachedKeyList(npcName);
            if (list == null || list.Count == 0)
            {
                this.Monitor.Log($"No cached key list for '{npcName}'. Run 'list_dialogue {npcName}' first.", LogLevel.Warn);
                return;
            }

            if (index >= list.Count)
            {
                this.Monitor.Log($"Invalid index. Must be 0..{list.Count - 1}.", LogLevel.Warn);
                return;
            }

            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            var entry = list[index];
            PlayByKey(npc, entry.SheetLabel, entry.Key);
            ShowHudKey(entry, index, list.Count);
            this.Monitor.Log($"Played index {index}: ({entry.SheetLabel}:{entry.Key})", LogLevel.Info);
        }

        private void PlayDialogueByKey(string cmd, string[] args)
        {
            if (args.Length < 3)
            {
                this.Monitor.Log("Usage: play_dialogue_key <NPCName> <sheetLabel> <key>", LogLevel.Info);
                return;
            }

            string npcName = args[0];
            string sheetLabel = args[1];
            string key = args[2];

            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            PlayByKey(npc, sheetLabel, key);
            ShowHudKey(new DialogueRef(sheetLabel, key), -1, -1);
            this.Monitor.Log($"Played ({sheetLabel}:{key}) for {npcName}.", LogLevel.Info);
        }

        // ===================== Key-based playback =====================

        private void PlayByKey(NPC npc, string sheetLabel, string key)
        {
            try
            {
                if (string.Equals(sheetLabel, "EngagementDialogue", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = this.Helper.GameContent.Load<Dictionary<string, string>>("Data/EngagementDialogue");
                    if (dict != null && dict.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    {
                        EmitPortraitDialogue(npc, raw);
                        return;
                    }
                    Game1.addHUDMessage(new HUDMessage($"Missing key: (EngagementDialogue:{key})", 3));
                    this.Monitor.Log($"Missing EngagementDialogue key '{key}'.", LogLevel.Warn);
                    return;
                }

                if (string.Equals(sheetLabel, "ExtraDialogue", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = this.Helper.GameContent.Load<Dictionary<string, string>>("Data/ExtraDialogue");
                    if (dict != null && dict.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    {
                        EmitPortraitDialogue(npc, raw);
                        return;
                    }
                    Game1.addHUDMessage(new HUDMessage($"Missing key: (ExtraDialogue:{key})", 3));
                    this.Monitor.Log($"Missing ExtraDialogue key '{key}'.", LogLevel.Warn);
                    return;
                }

                if (string.Equals(sheetLabel, "FestivalNonEvent", StringComparison.OrdinalIgnoreCase))
                {
                    // Key format is "festId:FullKey" (e.g., "summer11:Abigail_y2")
                    string raw = TryGetFestivalNonEventRaw(key);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        EmitPortraitDialogue(npc, raw);
                        return;
                    }
                    Game1.addHUDMessage(new HUDMessage($"Missing key: (FestivalNonEvent:{key})", 3));
                    this.Monitor.Log($"Missing FestivalNonEvent key '{key}'.", LogLevel.Warn);
                    return;
                }


                // default: Characters/Dialogue/<sheetLabel>:<key>
                string keyPath = $"Characters\\Dialogue\\{sheetLabel}:{key}";
                var dlg = new Dialogue(npc, keyPath);
                npc.setNewDialogue(dlg);
                Game1.drawDialogue(npc);
            }
            catch (Exception ex)
            {
                Game1.addHUDMessage(new HUDMessage($"Missing key: ({sheetLabel}:{key})", 3));
                this.Monitor.Log($"Missing or invalid key '{sheetLabel}:{key}': {ex}", LogLevel.Warn);
            }
        }

        private string TryGetFestivalNonEventRaw(string compoundKey)
        {
            // compoundKey: "festId:FullKey"
            if (string.IsNullOrWhiteSpace(compoundKey)) return null;
            var idx = compoundKey.IndexOf(':');
            if (idx <= 0 || idx >= compoundKey.Length - 1) return null;

            string festId = compoundKey.Substring(0, idx);
            string innerKey = compoundKey.Substring(idx + 1);

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>($"Data/Festivals/{festId}");
                if (dict != null && dict.TryGetValue(innerKey, out var raw))
                    return raw;
            }
            catch { }
            return null;
        }

        private List<EventLine> ParseFestivalSheet(string festId, string filter)
        {
            var results = new List<EventLine>();
            if (string.IsNullOrWhiteSpace(festId)) return results;

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>($"Data/Festivals/{festId}");
                if (dict == null || dict.Count == 0) return results;


                var rxSpeak = new Regex(@"\bspeak\s+([A-Za-z_]+)\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
                var rxBubble = new Regex(@"\b(?:showTextAboveHead|textAboveHead)\s+([A-Za-z_]+)\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);


                foreach (var kv in dict)
                {
                    var script = kv.Value ?? "";

                    foreach (Match m in rxSpeak.Matches(script))
                    {
                        var name = m.Groups[1].Value;
                        var text = m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(filter) &&
                            text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        results.Add(new EventLine { SpeakerName = name, Text = text, IsBubble = false });
                    }

                    foreach (Match m in rxBubble.Matches(script))
                    {
                        var name = m.Groups[1].Value;
                        var text = m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(filter) &&
                            text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        results.Add(new EventLine { SpeakerName = name, Text = text, IsBubble = true });
                    }
                }

                int i = 0;
                foreach (var r in results) r.Index = i++;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to parse Data/Festivals/{festId}: {ex.Message}", LogLevel.Trace);
            }
            try
            {
                int speakCount = results.Count(r => !r.IsBubble);
                int bubbleCount = results.Count(r => r.IsBubble);
                this.Monitor.Log($"[FestivalParse] {festId}: speak={speakCount}, bubble={bubbleCount} (after filter='{filter ?? "none"}')", LogLevel.Trace);
            }
            catch { }
            return results;
        }


        private async Task PlayKeyListRange(NPC npc, List<DialogueRef> list, int startIndex, int delayMs)
        {
            for (int i = startIndex; i < list.Count; i++)
            {
                var e = list[i];
                PlayByKey(npc, e.SheetLabel, e.Key);
                ShowHudKey(e, i, list.Count);

                await Task.Delay(delayMs);
                Game1.exitActiveMenu();
            }
        }

        private void ShowHudKey(DialogueRef entry, int index, int total)
        {
            try
            {
                string idx = (index >= 0 && total > 0) ? $" • {index + 1}/{total}" : "";
                string msg = $"({entry.SheetLabel}:{entry.Key}){idx}";
                Game1.addHUDMessage(new HUDMessage(msg));
            }
            catch { }
        }

        // ===================== Build the KEY list =====================

        private List<(string SheetLabel, Dictionary<string, string> Sheet)> LoadAllNpcDialogueSheets(string npcName)
        {
            var results = new List<(string, Dictionary<string, string>)>();

            // Base
            try
            {
                var baseSheet = this.Helper.GameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/{npcName}");
                if (baseSheet != null && baseSheet.Count > 0)
                    results.Add((npcName, baseSheet));
            }
            catch { }

            // Rainy (localized)
            try
            {
                var rainyOne = LoadRainyForNpc(npcName);
                if (rainyOne != null)
                    results.Add(("rainy", rainyOne));
            }
            catch { }

            // Marriage
            try
            {
                var spouseSheet = this.Helper.GameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{npcName}");
                if (spouseSheet != null && spouseSheet.Count > 0)
                    results.Add(($"MarriageDialogue{npcName}", spouseSheet));
            }
            catch { }

            // Engagement
            try
            {
                var engaged = LoadEngagementForNpc(npcName);
                if (engaged != null)
                    results.Add(("EngagementDialogue", engaged));
            }
            catch { }

            // ExtraDialogue (NEW)
            try
            {
                var extra = LoadExtraDialogueForNpc(npcName);
                if (extra != null)
                    results.Add(("ExtraDialogue", extra));

              
            }
            catch { }

            // Festival non-event (keyed) dialogue as a synthetic sheet
            try
            {
                var festNonEvent = LoadFestivalNonEventForNpc(npcName);
                if (festNonEvent != null && festNonEvent.Count > 0)
                    results.Add(("FestivalNonEvent", festNonEvent));
            }
            catch { }


            return results;
        }




        private List<DialogueRef> BuildKeyList(
            List<(string SheetLabel, Dictionary<string, string> Sheet)> sheets,
            string filter,
            bool includeChoices)
        {
            bool PassesFilter(string key) =>
                string.IsNullOrEmpty(filter) ||
                key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var list = new List<DialogueRef>(sheets.Sum(s => s.Sheet?.Count ?? 0));

            foreach (var (sheetLabel, sheet) in sheets)
            {
                if (sheet == null) continue;

                foreach (var kvp in sheet.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var key = kvp.Key;
                    if (!PassesFilter(key)) continue;

                    string raw = kvp.Value ?? "";

                    if (!includeChoices && (raw.Contains("$q", StringComparison.Ordinal) || raw.Contains("$r", StringComparison.Ordinal)))
                        continue;

                    list.Add(new DialogueRef(sheetLabel, key));
                }
            }

            for (int i = 0; i < list.Count; i++)
                list[i].PrimaryId = i;

            return list;
        }

        // ===================== Cache & resume =====================

        private string CacheFileFor(string npcName) =>
            Path.Combine(this.Helper.DirectoryPath, $"DialogueKeyList_{SanitizeFileName(npcName)}.json");

        private string RunLogFileFor(string npcName) =>
            Path.Combine(this.Helper.DirectoryPath, $"DialogueTester_{SanitizeFileName(npcName)}.log");

        private void SaveListCache(string npcName, List<DialogueRef> list)
        {
            try
            {
                File.WriteAllText(CacheFileFor(npcName), JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to write cache for '{npcName}': {ex}", LogLevel.Trace);
            }
        }

        private List<DialogueRef> GetOrLoadCachedKeyList(string npcName)
        {
            if (_lastListByNpc.TryGetValue(npcName, out var list) && list != null && list.Count > 0)
                return list;

            var path = CacheFileFor(npcName);
            if (!File.Exists(path)) return null;

            try
            {
                var loaded = JsonConvert.DeserializeObject<List<DialogueRef>>(File.ReadAllText(path));
                if (loaded != null && loaded.Count > 0)
                {
                    for (int i = 0; i < loaded.Count; i++)
                        loaded[i].PrimaryId = i;

                    _lastListByNpc[npcName] = loaded;
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to read cache for '{npcName}': {ex}", LogLevel.Trace);
            }
            return null;
        }

        private List<DialogueRef> GetOrRebuildKeyList(string npcName, string filter, bool includeChoices)
        {
            var cached = GetOrLoadCachedKeyList(npcName);
            if (cached != null && cached.Count > 0)
                return cached;

            var sheets = LoadAllNpcDialogueSheets(npcName);
            if (sheets.Count == 0) return null;

            var list = BuildKeyList(sheets, filter, includeChoices);
            if (list.Count > 0)
            {
                _lastListByNpc[npcName] = list;
                SaveListCache(npcName, list);
            }
            return list;
        }

        private void AppendRunLog(string npcName, string line)
        {
            try
            {
                var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.AppendAllText(RunLogFileFor(npcName), $"[{ts}] {line}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to write run log: {ex}", LogLevel.Trace);
            }
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        // ===================== Data =====================

        private sealed class DialogueRef
        {
            public int PrimaryId { get; set; }
            public string SheetLabel { get; set; }
            public string Key { get; set; }

            public DialogueRef() { }
            public DialogueRef(string sheetLabel, string key)
            {
                SheetLabel = sheetLabel;
                Key = key;
            }
        }

        private sealed class V2Fail
        {
            public string Speaker;
            public string Dialogue;
            public List<string> Removables;
            public string Stripped;
            public string Key;
            public bool Matched;
            public bool MissingAudio;
            public bool FuzzyAttempted;
            public double FuzzyBestScore;
            public string FuzzyBestKey;
            public bool FuzzyChosen;
            public string AudioPath;
        }

        private readonly List<V2Fail> _v2Fails = new();
        private bool _collectV2Failures = false;

        private void V2AddFailure(
            string speaker,
            string dialogue,
            IEnumerable<string> removables,
            string stripped,
            string key,
            bool matched,
            bool missingAudio,
            bool fuzzyAttempted = false,
            double fuzzyBestScore = 0.0,
            string fuzzyBestKey = null,
            bool fuzzyChosen = false,
            string audioPath = null
        )
        {
            _v2Fails.Add(new V2Fail
            {
                Speaker = speaker ?? "(unknown)",
                Dialogue = dialogue ?? "",
                Removables = removables?.ToList() ?? new List<string>(),
                Stripped = stripped ?? "",
                Key = key ?? "",
                Matched = matched,
                MissingAudio = missingAudio,
                FuzzyAttempted = fuzzyAttempted,
                FuzzyBestScore = fuzzyBestScore,
                FuzzyBestKey = fuzzyBestKey,
                FuzzyChosen = fuzzyChosen,
                AudioPath = audioPath ?? ""
            });
        }

        /// <summary>Print and clear a compact summary of unmatched/missing-audio lines, with fuzzy details.</summary>
        private void PrintV2FailureReport()
        {
            if (_v2Fails.Count == 0)
            {
                Monitor.Log("V2 summary: no unmatched/missing-audio lines.", LogLevel.Info);
                return;
            }

            int unmatched = _v2Fails.Count(f => !f.Matched);
            int missingAudio = _v2Fails.Count(f => f.Matched && f.MissingAudio);
            int fuzzyChosen = _v2Fails.Count(f => f.FuzzyChosen && f.Matched && !f.MissingAudio);

            Monitor.Log($"V2 summary: total={_v2Fails.Count}, unmatched={unmatched}, fuzzy-chosen={fuzzyChosen}, missing-audio={missingAudio}", LogLevel.Info);

            var unmatchedList = _v2Fails.Where(f => !f.Matched).ToList();
            if (unmatchedList.Count > 0)
            {
                Monitor.Log("--- UNMATCHED (after fuzzy) ---", LogLevel.Warn);
                int i = 1;
                foreach (var f in unmatchedList)
                {
                    Monitor.Log($"[{i++}] Speaker   : {f.Speaker}", LogLevel.Info);
                    Monitor.Log($"      Dialogue  : \"{f.Dialogue}\"", LogLevel.Info);
                    Monitor.Log($"      Removables: {(f.Removables.Count > 0 ? string.Join(", ", f.Removables) : "(none)")}", LogLevel.Info);
                    Monitor.Log($"      Stripped  : \"{f.Stripped}\"", LogLevel.Info);
                    Monitor.Log($"      DisplayKey: \"{f.Key}\"  {(f.FuzzyAttempted ? $"(fuzzy tried: best={(f.FuzzyBestScore * 100):0.0}% (needed ≥ 90.0%))" : "(fuzzy not attempted)")} ", LogLevel.Info);
                }
            }

            var fuzzyHitList = _v2Fails.Where(f => f.FuzzyChosen && f.Matched && !f.MissingAudio).ToList();
            if (fuzzyHitList.Count > 0)
            {
                Monitor.Log("--- FUZZY MATCHED ---", LogLevel.Info);
                int i = 1;
                foreach (var f in fuzzyHitList)
                {
                    Monitor.Log($"[{i++}] Speaker   : {f.Speaker}", LogLevel.Info);
                    Monitor.Log($"      Dialogue  : \"{f.Dialogue}\"", LogLevel.Info);
                    Monitor.Log($"      Stripped  : \"{f.Stripped}\"", LogLevel.Info);
                    Monitor.Log($"      Fuzzy     : score={(f.FuzzyBestScore * 100):0.1}% (needed ≥ 90.0%) → CHOSEN", LogLevel.Info);
                    Monitor.Log($"      ChosenKey : \"{f.FuzzyBestKey}\"", LogLevel.Info);
                    if (!string.IsNullOrWhiteSpace(f.AudioPath))
                        Monitor.Log($"      AudioPath : {f.AudioPath}", LogLevel.Info);
                }
            }

            // comment this section out to hide Missing Audio testing unmatch dialogues
            var missingList = _v2Fails.Where(f => f.Matched && f.MissingAudio).ToList();
            if (missingList.Count > 0)
            {
                Monitor.Log("--- MISSING AUDIO (pattern matched, file missing) ---", LogLevel.Warn);
                int i = 1;
                foreach (var f in missingList)
                {
                    Monitor.Log($"[{i++}] Speaker   : {f.Speaker}", LogLevel.Info);
                    Monitor.Log($"      Dialogue  : \"{f.Dialogue}\"", LogLevel.Info);
                    Monitor.Log($"      Removables: {(f.Removables.Count > 0 ? string.Join(", ", f.Removables) : "(none)")}", LogLevel.Info);
                    Monitor.Log($"      Stripped  : \"{f.Stripped}\"", LogLevel.Info);
                    Monitor.Log($"      DisplayKey: \"{f.Key}\"", LogLevel.Info);
                    if (!string.IsNullOrWhiteSpace(f.AudioPath))
                        Monitor.Log($"      AudioPath : {f.AudioPath}", LogLevel.Info);
                }
            }
            

            _v2Fails.Clear();
        }
            




        // ===================== EVENTS: list / play / test =====================
        private static bool IsFestivalId(string token) => DefaultFestivalIds.Any(f => f.Equals(token, StringComparison.OrdinalIgnoreCase));

        private void ListEvents(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                this.Monitor.Log("Usage: list_events <locationAssetName|festivalId> [filter]   e.g., list_events Town  |  list_events summer11", LogLevel.Info);
                return;
            }

            string target = args[0];
            string filter = args.Length > 1 ? args[1] : null;

            List<EventLine> lines;
            string cacheKey;

            if (IsFestivalId(target))
            {
                lines = ParseFestivalSheet(target, filter);
                cacheKey = $"fest:{target}";
            }
            else
            {
                lines = ParseEventLocation(target, filter);
                cacheKey = target;
            }

            if (lines.Count == 0)
            {
                this.Monitor.Log($"No lines found in {(IsFestivalId(target) ? $"Data/Festivals/{target}" : $"Data/Events/{target}")} (filter='{filter ?? "(none)"}').", LogLevel.Info);
                return;
            }

            _lastEventLinesByLocation[cacheKey] = lines;

            this.Monitor.Log($"--- {(IsFestivalId(target) ? $"Festival '{target}'" : $"Events '{target}'")} lines (total {lines.Count}) ---", LogLevel.Info);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                string mode = l.IsBubble ? "bubble" : "speak";
                this.Monitor.Log($"{i,4}: [{mode}] {l.SpeakerName}: \"{Trunc(l.Text, 70)}\"", LogLevel.Info);
            }
        }

        // Normalize & alias speaker tokens from event scripts.
        private static readonly Dictionary<string, string> SpeakerAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["mrqi"] = "qi",
                ["qi"] = "qi",
                ["abby"] = "abigail",
                ["elliot"] = "elliott",
                // add more if you find them in scripts:
                // ["krobus??"] = "krobus",
            };

        // keep only letters for loose matching (e.g., "Mr_Qi" -> "mrqi")
        private static string NormalizeSpeakerToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var chars = s.Where(char.IsLetter).ToArray();
            var t = new string(chars);
            if (t.Length == 0) return "";
            // apply aliases first
            if (SpeakerAliases.TryGetValue(t, out var aliased))
                return aliased.ToLowerInvariant();
            return t.ToLowerInvariant();
        }

        private static bool NamesMatch(string a, string b)
        {
            return NormalizeSpeakerToken(a) == NormalizeSpeakerToken(b);
        }

        private async void TestEvents(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                this.Monitor.Log("Usage: test_events <locationAssetName|festivalId|all|all_festivals|characterName> [delayMs] [filter]", LogLevel.Info);
                return;
            }

            string target = args[0];
            int delay = (args.Length > 1 && int.TryParse(args[1], out var d)) ? Math.Max(1, d) : 1200;
            string filter = args.Length > 2 ? args[2] : null;

            _collectV2Failures = true;
            try
            {
                if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    int total = 0;
                    foreach (var loc in DefaultEventLocations)
                    {
                        var lines = ParseEventLocation(loc, filter);
                        if (lines.Count == 0) continue;
                        _lastEventLinesByLocation[loc] = lines;
                        this.Monitor.Log($"[EVENT] {loc}: {lines.Count} lines…", LogLevel.Info);

                        foreach (var line in lines)
                        {
                            var npc = Game1.getCharacterFromName(line.SpeakerName);
                            if (npc == null) continue;
                            if (line.IsBubble) EmitBubbleWithVOF(npc, line.Text); else EmitPortraitDialogue(npc, line.Text);
                            await Task.Delay(delay); Game1.exitActiveMenu(); total++;
                        }
                    }
                    this.Monitor.Log($"[EVENT] Completed. Total lines: {total}.", LogLevel.Info);
                    return;
                }

                if (target.Equals("all_festivals", StringComparison.OrdinalIgnoreCase))
                {
                    int total = 0;
                    foreach (var fest in DefaultFestivalIds)
                    {
                        var lines = ParseFestivalSheet(fest, filter);

                        // Always log, even when zero
                        this.Monitor.Log($"[EVENT] {fest}: {lines.Count} lines…", LogLevel.Info);

                        if (lines.Count == 0) continue;

                        var cacheKey = $"fest:{fest}";
                        _lastEventLinesByLocation[cacheKey] = lines;

                        foreach (var line in lines)
                        {
                            var npc = Game1.getCharacterFromName(line.SpeakerName);
                            if (npc == null) continue;
                            if (line.IsBubble) EmitBubbleWithVOF(npc, line.Text); else EmitPortraitDialogue(npc, line.Text);
                            await Task.Delay(delay); Game1.exitActiveMenu(); total++;
                        }
                    }
                    this.Monitor.Log($"[EVENT] Completed all festivals. Total lines: {total}.", LogLevel.Info);
                    return;
                }


                if (IsFestivalId(target))
                {
                    var lines = ParseFestivalSheet(target, filter);
                    if (lines.Count == 0)
                    {
                        this.Monitor.Log($"No lines found in Data/Festivals/{target} (filter='{filter ?? "(none)"}').", LogLevel.Info);
                        return;
                    }

                    var cacheKey = $"fest:{target}";
                    _lastEventLinesByLocation[cacheKey] = lines;
                    this.Monitor.Log($"[EVENT] {target}: {lines.Count} lines…", LogLevel.Info);

                    foreach (var line in lines)
                    {
                        var npc = Game1.getCharacterFromName(line.SpeakerName);
                        if (npc == null) continue;
                        if (line.IsBubble) EmitBubbleWithVOF(npc, line.Text); else EmitPortraitDialogue(npc, line.Text);
                        await Task.Delay(delay); Game1.exitActiveMenu();
                    }
                    return;
                }

                // Otherwise: treat target as a SPEAKER filter across BOTH locations and festivals
                {
                    string speaker = target;
                    int total = 0;

                    // Standard locations
                    foreach (var loc in DefaultEventLocations)
                    {
                        var lines = ParseEventLocation(loc, filter)
                            .Where(l => NamesMatch(l.SpeakerName, speaker)).ToList(); ;
                        if (lines.Count == 0) continue;

                        _lastEventLinesByLocation[$"{loc}:speaker:{speaker}"] = lines;
                        this.Monitor.Log($"[EVENT] {loc} (speaker={speaker}): {lines.Count} lines…", LogLevel.Info);

                        var npc = Game1.getCharacterFromName(speaker, true) ?? DefaultSpeaker();
                        foreach (var line in lines)
                        {
                            if (npc == null) break;
                            if (line.IsBubble) EmitBubbleWithVOF(npc, line.Text); else EmitPortraitDialogue(npc, line.Text);
                            await Task.Delay(delay); Game1.exitActiveMenu(); total++;
                        }
                    }

                    // Festivals
                    // Festivals
                    foreach (var fest in DefaultFestivalIds)
                    {
                        var allLines = ParseFestivalSheet(fest, filter);
                        var lines = allLines.Where(l => NamesMatch(l.SpeakerName, speaker)).ToList();

                        // Always log, even when zero
                        this.Monitor.Log($"[EVENT] {fest} (speaker={speaker}): {lines.Count} lines…", LogLevel.Info);

                        if (lines.Count == 0)
                            continue;

                        _lastEventLinesByLocation[$"fest:{fest}:speaker:{speaker}"] = lines;

                        var npc = Game1.getCharacterFromName(speaker, true) ?? DefaultSpeaker();
                        foreach (var line in lines)
                        {
                            if (npc == null) break;
                            if (line.IsBubble) EmitBubbleWithVOF(npc, line.Text); else EmitPortraitDialogue(npc, line.Text);
                            await Task.Delay(delay); Game1.exitActiveMenu(); total++;
                        }
                    }

                    if (total == 0)
                        this.Monitor.Log($"[EVENT] No lines found for speaker '{speaker}' across locations + festivals.", LogLevel.Info);
                    else
                        this.Monitor.Log($"[EVENT] Completed for speaker '{speaker}'. Total lines: {total}.", LogLevel.Info);
                }
            }
            finally
            {
                PrintV2FailureReport();
                _collectV2Failures = false;
            }
        }



        private void PlayEventLineByIndex(string cmd, string[] args)
        {
            if (args.Length < 2)
            {
                this.Monitor.Log("Usage: play_event_line <locationAssetName> <index>", LogLevel.Info);
                return;
            }

            string loc = args[0];
            if (!int.TryParse(args[1], out int index) || index < 0)
            {
                this.Monitor.Log("Index must be a non-negative integer.", LogLevel.Warn);
                return;
            }

            if (!_lastEventLinesByLocation.TryGetValue(loc, out var lines) || lines == null || lines.Count == 0)
            {
                this.Monitor.Log($"No cached event lines for '{loc}'. Run 'list_events {loc}' first.", LogLevel.Warn);
                return;
            }

            if (index >= lines.Count)
            {
                this.Monitor.Log($"Invalid index. Must be 0..{lines.Count - 1}.", LogLevel.Warn);
                return;
            }

            var line = lines[index];
            var npc = ResolveNpcForSpeaker(line.SpeakerName);

            if (npc != null)
            {
                if (line.IsBubble)
                    EmitBubbleWithVOF(npc, line.Text);
                else
                    EmitPortraitDialogue(npc, line.Text);
            }
            else
            {
         
                Game1.drawObjectDialogue(line.Text);
            }

            this.Monitor.Log($"Played [{(line.IsBubble ? "bubble" : "speak")}] {line.SpeakerName}: \"{Trunc(line.Text, 70)}\"", LogLevel.Info);
        }

        private List<EventLine> ParseEventLocation(string locationAssetName, string filter)
        {
            var results = new List<EventLine>();

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>($"Data/Events/{locationAssetName}");
                if (dict == null || dict.Count == 0) return results;

                // Extract speak "<NPC>" "text"
                var rxSpeak = new Regex(@"\bspeak\s+([A-Za-z_]+)\s+""([^""]*)""", RegexOptions.IgnoreCase);
                // Extract showTextAboveHead/textAboveHead "<NPC>" "text"
                var rxBubble = new Regex(@"\b(?:showTextAboveHead|textAboveHead)\s+([A-Za-z_]+)\s+""([^""]*)""", RegexOptions.IgnoreCase);

                foreach (var kv in dict)
                {
                    string script = kv.Value ?? "";
                    foreach (Match m in rxSpeak.Matches(script))
                    {
                        var name = m.Groups[1].Value;
                        var text = m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(filter) &&
                            text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        results.Add(new EventLine { SpeakerName = name, Text = text, IsBubble = false });
                    }
                    foreach (Match m in rxBubble.Matches(script))
                    {
                        var name = m.Groups[1].Value;
                        var text = m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(filter) &&
                            text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        results.Add(new EventLine { SpeakerName = name, Text = text, IsBubble = true });
                    }
                }

                // stable order
                int i = 0;
                foreach (var r in results)
                    r.Index = i++;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Failed to parse Data/Events/{locationAssetName}: {ex.Message}", LogLevel.Trace);
            }

            return results;
        }

        // ===================== Low-level emitters =====================

        /// <summary>Show a portrait dialogue window that routes through the normal Dialogue UI → VOF pipeline.</summary>
        // Show a portrait dialogue window from raw text (literal), routed through the normal Dialogue UI.
        private void EmitPortraitDialogue(NPC speaker, string rawText)
        {
            try
            {
                if (speaker == null || string.IsNullOrWhiteSpace(rawText))
                    return;

                // strip numeric format placeholders like "{2}" and "{2}s" that can leak into visible text
                static string Clean(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return s;
                    s = Regex.Replace(s, @"\{(\d+)\}s\b", ""); // drop {n}s
                    s = Regex.Replace(s, @"\{(\d+)\}", ""); // drop {n}
                    s = Regex.Replace(s, @"\s{2,}", " ");      // tidy spaces
                    return s.Trim();
                }

                string text = Clean(rawText);

                // 1.6 signature: (NPC speaker, string translationKey, string dialogueText)
                var dlg = new Dialogue(speaker, InlineDialogueKey, text);
                speaker.setNewDialogue(dlg);
                Game1.drawDialogue(speaker);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"EmitPortraitDialogue failed: {ex}", LogLevel.Trace);
            }
        }

        // Extracts the NPC's (display) name from a SpeechBubbles key like
        //  "SeedShop_Pierre_NotSummer" → "Pierre"
        private string ParseNpcFromBubbleKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            // Keys are generally <Location>_<NPC>_<rest>
            var parts = key.Split('_');
            if (parts.Length < 2)
                return null;

            var npcToken = parts[1];

            // Normalize a few special cases if needed
            switch (npcToken)
            {
                case "MrQi":
                    return "Qi";
                default:
                    return npcToken;
            }
        }
        private sealed class _TempNpcRecord
        {
            public NPC Npc;
            public bool WasAdded;                 // we added to characters list
            public GameLocation OriginalLoc;
            public Microsoft.Xna.Framework.Vector2 OriginalTile;
        }

        private readonly List<_TempNpcRecord> _tempNpcs = new();

        private NPC GetOrStageNpcForTest(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName))
                return null;

            var targetLoc = Game1.player?.currentLocation;
            if (targetLoc == null)
                return null;

            // Try get a concrete NPC instance (this can create it if not loaded)
            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
                return null;

            // If already present & in our location, just position near player
            if (npc.currentLocation == targetLoc && targetLoc.characters?.Contains(npc) == true)
                return PlaceNpcNearPlayer(npc);

            // Otherwise, remember where it was (best effort), move into our location, and record cleanup
            var rec = new _TempNpcRecord
            {
                Npc = npc,
                OriginalLoc = npc.currentLocation,
                OriginalTile = npc.Tile
            };

            try { rec.WasAdded = !targetLoc.characters.Contains(npc); } catch { rec.WasAdded = true; }

            try
            {
                // detach from old location if needed
                if (npc.currentLocation != null && npc.currentLocation != targetLoc)
                    npc.currentLocation.characters?.Remove(npc);
            }
            catch { /* ignore */ }

            npc.currentLocation = targetLoc;
            try { if (!targetLoc.characters.Contains(npc)) targetLoc.addCharacter(npc); } catch { }

            _tempNpcs.Add(rec);
            return PlaceNpcNearPlayer(npc);
        }

        private NPC PlaceNpcNearPlayer(NPC npc)
        {
            if (npc == null) return null;
            var px = Game1.player.TilePoint.X;
            var py = Game1.player.TilePoint.Y;
            npc.setTileLocation(new Microsoft.Xna.Framework.Vector2(px + 1, py));
            try { npc.faceTowardFarmerForPeriod(1, 1, false, Game1.player); } catch { }
            return npc;
        }

        private void CleanupStagedNpcs()
        {
            foreach (var rec in _tempNpcs)
            {
                try
                {
                    var npc = rec.Npc;
                    var cur = npc?.currentLocation;

                    // remove from current location if we added it
                    if (cur != null && rec.WasAdded)
                        cur.characters?.Remove(npc);

                    // put back
                    npc.currentLocation = rec.OriginalLoc;
                    if (rec.OriginalLoc != null && !rec.OriginalLoc.characters.Contains(npc))
                        rec.OriginalLoc.addCharacter(npc);

                    npc.setTileLocation(rec.OriginalTile);
                }
                catch { /* best effort */ }
            }
            _tempNpcs.Clear();
        }

        private void EmitBubbleWithVOF(NPC speaker, string rawText)
        {
            try
            {
                if (speaker == null || string.IsNullOrWhiteSpace(rawText))
                    return;

                // strip numeric format placeholders like "{2}" and "{2}s" that can leak into visible text
                static string Clean(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return s;
                    s = Regex.Replace(s, @"\{(\d+)\}s\b", ""); // drop {n}s
                    s = Regex.Replace(s, @"\{(\d+)\}", ""); // drop {n}
                    s = Regex.Replace(s, @"\s{2,}", " ");      // tidy spaces
                    return s.Trim();
                }

                string text = Clean(rawText);

                // bubble text (no UI yet)
                speaker.showTextAboveHead(text);

                // Mirror into the Dialogue UI so your voice matching runs the same as NPC dialogue.
                var dlg = new Dialogue(speaker, InlineDialogueKey, text);
                speaker.setNewDialogue(dlg);
                Game1.drawDialogue(speaker);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"EmitBubbleWithVOF failed: {ex}", LogLevel.Trace);
            }
        }



        private NPC ResolveNpcForSpeaker(string speakerName)
        {
            if (string.IsNullOrWhiteSpace(speakerName)) return null;
            // normalize + alias
            string norm = NormalizeSpeakerToken(speakerName);
            if (string.IsNullOrEmpty(norm)) return null;

            // Try the exact canonical name (first-letter upper for vanilla)
            string canonical = char.ToUpper(norm[0]) + norm.Substring(1);
            try
            {
                var npc = Game1.getCharacterFromName(canonical, true);
                if (npc != null) return npc;

                // Fallbacks: some NPC internal names are title-cased already
                return Game1.getCharacterFromName(speakerName, true);
            }
            catch { return null; }
        }




        private void PlayEventLineSafely(dynamic entry)
        {

            string speakerName = entry.Speaker as string;
            string translationKey = entry.TranslationKey as string ?? "Strings\\Characters:FallbackDialogueForError";
            string text = entry.RawText as string ?? "";

            try
            {
                var npc = ResolveNpcForSpeaker(speakerName);

                if (npc != null)
                {
                    // Correct constructor order: (NPC speaker, string translationKey, string dialogueText)
                    var dlg = new Dialogue(npc, translationKey, text);
                    npc.setNewDialogue(dlg);
                    Game1.drawDialogue(npc);
                }
                else
                {
                    // No instantiated NPC? Still show the text so audio + vof summary flow continues.
                    Game1.drawObjectDialogue(text);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"[EVENT TEST] Failed to play line for speaker '{speakerName}' (key: {translationKey}). {ex}", LogLevel.Warn);
              
                try { Game1.drawObjectDialogue(text); } catch {  }
            }
        }


        private NPC DefaultSpeaker()
        {
            // Prefer Lewis as a safe, always-present NPC; fall back to any loaded NPC if needed.
            var lewis = Game1.getCharacterFromName("Lewis", true);
            if (lewis != null) return lewis;

            foreach (var loc in Game1.locations)
            {
                var any = loc?.characters?.FirstOrDefault();
                if (any != null) return any;
            }
            return null;
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s;
            return s.Substring(0, Math.Max(0, n - 1)) + "…";
        }

        // ===================== DTOs for sheets/events =====================

        private sealed class SheetEntry
        {
            public int Index { get; set; }
            public string Key { get; set; }
            public string Value { get; set; }
        }

        private sealed class EventLine
        {
            public int Index { get; set; }
            public string SpeakerName { get; set; }
            public string Text { get; set; }
            public bool IsBubble { get; set; }
        }
    }
}
