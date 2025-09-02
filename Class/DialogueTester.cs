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
                    "Auto-plays all dialogue (auto-discovers base + marriage) for the NPC.",
                callback: this.TestDialogue
            );

            commands.Add(
                name: "test_dialogue_from",
                documentation:
                    "Usage: test_dialogue_from <NPCName> <startIndex> [delayMs] [filter] [includeChoices]\n" +
                    "Resume auto-play from the given Primary Index ID.",
                callback: this.TestDialogueFrom
            );

            commands.Add(
                name: "list_dialogue",
                documentation:
                    "Usage: list_dialogue <NPCName> [filter] [includeChoices]\n" +
                    "Lists all dialogue (auto-discovers base + marriage) with Primary Index IDs.",
                callback: this.ListDialogue
            );

            commands.Add(
                name: "play_dialogue",
                documentation:
                    "Usage: play_dialogue <NPCName> <index>\n" +
                    "Plays one dialogue entry by Primary Index ID from the last/cached list.",
                callback: this.PlayDialogueByIndex
            );

            commands.Add(
                name: "play_dialogue_key",
                documentation:
                    "Usage: play_dialogue_key <NPCName> <sheetLabel> <key>\n" +
                    "Plays one dialogue by sheet label + key (labels shown by list_dialogue).",
                callback: this.PlayDialogueByKey
            );
        }

        // ===================== Commands =====================

        private async void TestDialogue(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                this.Monitor.Log("You must specify an NPC name. Example: test_dialogue Abigail 1000", LogLevel.Info);
                return;
            }

            string npcName = args[0];
            int delay = (args.Length > 1 && int.TryParse(args[1], out var d)) ? Math.Max(150, d) : 1200;
            string filter = args.Length > 2 ? args[2] : null;
            bool includeChoices = args.Length > 3 && args[3].Equals("includeChoices", StringComparison.OrdinalIgnoreCase);

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

            var list = BuildList(sheets, filter, includeChoices);
            if (list.Count == 0)
            {
                this.Monitor.Log($"No entries matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }

            _lastListByNpc[npcName] = list;
            SaveListCache(npcName, list);
            AppendRunLog(npcName, $"TEST start (0..{list.Count - 1}) delay={delay} filter='{filter ?? "(none)"}' includeChoices={includeChoices}");

            this.Monitor.Log($"Auto-playing {list.Count} entries for {npcName} (delay={delay}ms).", LogLevel.Info);
            await PlayListRange(npc, list, startIndex: 0, delayMs: delay);
            this.Monitor.Log("Finished.", LogLevel.Info);
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

            int delay = (args.Length > 2 && int.TryParse(args[2], out var d)) ? Math.Max(150, d) : 1200;
            string filter = args.Length > 3 ? args[3] : null;
            bool includeChoices = args.Length > 4 && args[4].Equals("includeChoices", StringComparison.OrdinalIgnoreCase);

            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            var list = GetOrRebuildList(npcName, filter, includeChoices);
            if (list == null || list.Count == 0)
            {
                this.Monitor.Log($"No entries matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }
            if (startIndex >= list.Count)
            {
                this.Monitor.Log($"startIndex {startIndex} is out of range (0..{list.Count - 1}).", LogLevel.Warn);
                return;
            }

            AppendRunLog(npcName, $"TEST_FROM start={startIndex} (..{list.Count - 1}) delay={delay} filter='{filter ?? "(none)"}' includeChoices={includeChoices}");
            this.Monitor.Log($"Resuming at index {startIndex} of {list.Count} for {npcName} (delay={delay}ms).", LogLevel.Info);
            await PlayListRange(npc, list, startIndex, delay);
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

            var list = BuildList(sheets, filter, includeChoices);
            if (list.Count == 0)
            {
                this.Monitor.Log($"No entries matched (filter='{filter ?? "(none)"}', includeChoices={includeChoices}).", LogLevel.Info);
                return;
            }

            _lastListByNpc[npcName] = list;
            SaveListCache(npcName, list);

            this.Monitor.Log($"--- Dialogue list for {npcName} (total {list.Count}) ---", LogLevel.Info);
            foreach (var e in list)
            {
                string preview = TruncateOneLine(e.Text, 96);
                this.Monitor.Log($"{e.PrimaryId,4}: ({e.SheetLabel}:{e.Key}) {preview}", LogLevel.Info);
            }
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

            var list = GetOrLoadCachedList(npcName);
            if (list == null || list.Count == 0)
            {
                this.Monitor.Log($"No cached list for '{npcName}'. Run 'list_dialogue {npcName}' first.", LogLevel.Warn);
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

            var dlg = new Dialogue(npc, translationKey: null, dialogueText: entry.Text ?? "");
            Game1.drawDialogue(npc);

            // HUD label (safe, not part of the dialogue text)
            ShowHudTag(entry, index, list.Count);

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

            var sheets = LoadAllNpcDialogueSheets(npcName);
            if (sheets.Count == 0)
            {
                this.Monitor.Log($"No dialogue found for '{npcName}'.", LogLevel.Warn);
                return;
            }

            var target = sheets.FirstOrDefault(s => s.SheetLabel.Equals(sheetLabel, StringComparison.OrdinalIgnoreCase));
            if (target.Sheet == null || !target.Sheet.TryGetValue(key, out var text))
            {
                this.Monitor.Log($"Key '{key}' not found in sheet '{sheetLabel}'.", LogLevel.Warn);
                return;
            }

            var npc = Game1.getCharacterFromName(npcName, true);
            if (npc == null)
            {
                this.Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                return;
            }

            npc.setNewDialogue(text ?? "");
            Game1.drawDialogue(npc);

            // HUD label
            ShowHudTag(new DialogueRef(sheetLabel, key, text ?? ""), -1, -1);

            this.Monitor.Log($"Played ({sheetLabel}:{key}) for {npcName}.", LogLevel.Info);
        }

        // ===================== Helpers =====================

        private List<(string SheetLabel, Dictionary<string, string> Sheet)> LoadAllNpcDialogueSheets(string npcName)
        {
            var results = new List<(string, Dictionary<string, string>)>();

            try
            {
                var baseSheet = this.Helper.GameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/{npcName}");
                if (baseSheet != null && baseSheet.Count > 0)
                    results.Add((npcName, baseSheet));
            }
            catch { }

            try
            {
                var spouseSheet = this.Helper.GameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{npcName}");
                if (spouseSheet != null && spouseSheet.Count > 0)
                    results.Add(($"MarriageDialogue{npcName}", spouseSheet));
            }
            catch { }

            return results;
        }

        private List<DialogueRef> BuildList(
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

                    list.Add(new DialogueRef(sheetLabel, key, raw));
                }
            }

            for (int i = 0; i < list.Count; i++)
                list[i].PrimaryId = i;

            return list;
        }

        private async Task PlayListRange(NPC npc, List<DialogueRef> list, int startIndex, int delayMs)
        {
            for (int i = startIndex; i < list.Count; i++)
            {
                var entry = list[i];

                var dlg = new Dialogue(npc, translationKey: null, dialogueText: entry.Text ?? "");
                npc.setNewDialogue(dlg);
                Game1.drawDialogue(npc);


                // HUD label displays which entry is being shown (doesn't affect parsing)
                ShowHudTag(entry, i, list.Count);

                await Task.Delay(delayMs);
                Game1.exitActiveMenu();
            }
        }

        private static string TruncateOneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        private void ShowHudTag(DialogueRef entry, int index, int total)
        {
            try
            {
                string idx = (index >= 0 && total > 0) ? $" • {index + 1}/{total}" : "";
                string msg = $"({entry.SheetLabel}:{entry.Key}){idx}";
                // Simple, non-intrusive on-screen toast (safe; not part of dialogue text)
                Game1.addHUDMessage(new HUDMessage(msg));
            }
            catch { /* HUD optional */ }
        }

        // ===================== Caching & Logging =====================

        private string CacheFileFor(string npcName) =>
            Path.Combine(this.Helper.DirectoryPath, $"DialogueListCache_{SanitizeFileName(npcName)}.json");

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

        private List<DialogueRef> GetOrLoadCachedList(string npcName)
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

        private List<DialogueRef> GetOrRebuildList(string npcName, string filter, bool includeChoices)
        {
            var cached = GetOrLoadCachedList(npcName);
            if (cached != null && cached.Count > 0)
                return cached;

            var sheets = LoadAllNpcDialogueSheets(npcName);
            if (sheets.Count == 0) return null;

            var list = BuildList(sheets, filter, includeChoices);
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

        // ===================== Data type =====================

        private sealed class DialogueRef
        {
            public int PrimaryId { get; set; }
            public string SheetLabel { get; set; }
            public string Key { get; set; }
            public string Text { get; set; }

            public DialogueRef() { }
            public DialogueRef(string sheetLabel, string key, string text)
            {
                SheetLabel = sheetLabel;
                Key = key;
                Text = text;
            }
        }
    }
}
