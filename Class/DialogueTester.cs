using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private readonly Dictionary<string, List<DialogueRef>> _lastListByNpc =
            new Dictionary<string, List<DialogueRef>>(StringComparer.OrdinalIgnoreCase);

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
            // fresh summary per “all” run
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
                    continue; // no dialogue sheet for this NPC

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
            // Build an explicit translation key path (works for base AND marriage):
            //   Characters\Dialogue\Abigail:Mon
            //   Characters\Dialogue\MarriageDialogueAbigail:Rainy
            string keyPath = $"Characters\\Dialogue\\{sheetLabel}:{key}";

            try
            {
                var dlg = new Dialogue(npc, keyPath);   // 1.6: constructor that takes a translation key path
                npc.setNewDialogue(dlg);
                Game1.drawDialogue(npc);
            }
            catch (Exception ex)
            {
                // If a specific key is missing, show a HUD note and log the failure but continue.
                Game1.addHUDMessage(new HUDMessage($"Missing key: ({sheetLabel}:{key})", 3));
                this.Monitor.Log($"Missing or invalid key '{sheetLabel}:{key}': {ex}", LogLevel.Warn);
            }
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
                    results.Add((npcName, baseSheet)); // label "Abigail"
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

                    // Optional: skip choice/response trees unless explicitly included
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
            public int PrimaryId { get; set; }          // stable index for this list
            public string SheetLabel { get; set; }      // e.g., "Abigail" or "MarriageDialogueAbigail"
            public string Key { get; set; }             // the dialogue key (Mon, Rainy, Introduction, ...)

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

            // UNMATCHED (after fuzzy)
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

            // FUZZY MATCHED (played)
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

            // MISSING AUDIO
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






    }
}
