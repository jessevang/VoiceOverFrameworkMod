using Newtonsoft.Json;   
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using System.Linq;
using System.Text.RegularExpressions;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {

        private readonly Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new(StringComparer.OrdinalIgnoreCase);



        //helper used by loader and CheckForDialogueV2 to canonicalize DisplayPattern text
        static string CanonDisplay(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // normalize line endings & collapse whitespace; keep punctuation as-is
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }



        private void AddVoicePackToStore(VoicePack vp, ref int totalDefinitionsProcessed, ref bool foundInPack)
        {
            if (!VoicePacksByCharacter.TryGetValue(vp.Character, out var list))
                VoicePacksByCharacter[vp.Character] = list = new List<VoicePack>();

            if (!list.Any(p => p.Language.Equals(vp.Language, StringComparison.OrdinalIgnoreCase)
                            && p.VoicePackId.Equals(vp.VoicePackId, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(vp);
                totalDefinitionsProcessed++;
                foundInPack = true;
                if (Config.developerModeOn)
                    Monitor.Log($"---> Loaded definition '{vp.VoicePackName}' ({vp.VoicePackId}) for {vp.Character} [{vp.Language}] ({vp.Entries.Count} entries)", LogLevel.Debug);
            }
        }


        private static int GetFormatMajor(string? fmt)
        {
            if (string.IsNullOrWhiteSpace(fmt)) return 1;
            // Try System.Version first
            if (Version.TryParse(fmt, out var v)) return Math.Max(1, v.Major);
            // Fallback: take leading integer before first dot
            var head = fmt.Split('.')[0];
            return int.TryParse(head, out var n) && n > 0 ? n : 1;
        }



        private void LoadVoicePacks()
        {
            if (Config.developerModeOn)
                this.Monitor.Log("Scanning Content Packs for voice data definitions...", LogLevel.Debug);

            VoicePacksByCharacter.Clear(); // Clear previous data

            var ownedContentPacks = this.Helper.ContentPacks.GetOwned();
            this.Monitor.Log($"Found {ownedContentPacks.Count()} potential voice content packs.", LogLevel.Debug);

            int totalFilesLoaded = 0;
            int totalDefinitionsProcessed = 0; // Keep track of inner definitions too

            foreach (var contentPack in ownedContentPacks)
            {
                this.Monitor.Log($"Scanning Content Pack: '{contentPack.Manifest.Name}' ({contentPack.Manifest.UniqueID}) at {contentPack.DirectoryPath}", LogLevel.Trace);
                string packDir = contentPack.DirectoryPath;

                try
                {
                    if (!Directory.Exists(packDir))
                    {
                        this.Monitor.Log($"Directory not found for Content Pack '{contentPack.Manifest.Name}': {packDir}. Skipping.", LogLevel.Warn);
                        continue;
                    }

                    // Search all subfolders for JSON (skip the pack manifest itself)
                    IEnumerable<string> jsonFiles = Directory.EnumerateFiles(packDir, "*.json", SearchOption.AllDirectories);
                    bool foundAnyValidDefinitionInPack = false;

                    foreach (string filePath in jsonFiles)
                    {
                        if (Path.GetFileName(filePath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                            continue; // Skip content pack manifest

                        string relativePathForLog = Path.GetRelativePath(packDir, filePath);
                        this.Monitor.Log($"---> Found potential voice definition file: {relativePathForLog}", LogLevel.Trace);

                        try
                        {
                            string jsonContent = File.ReadAllText(filePath);

                            // STEP 1 & 2: Deserialize file and check
                            var voicePackFileData = JsonConvert.DeserializeObject<VoicePackFile>(jsonContent);
                            if (voicePackFileData?.VoicePacks == null || !voicePackFileData.VoicePacks.Any())
                            {
                                this.Monitor.Log($"---> Skipping file '{relativePathForLog}': Invalid structure or empty 'VoicePacks' list found inside.", LogLevel.Trace);
                                continue;
                            }

                            // File-level default format (may be null for older files)
                            string? fileLevelFormat = voicePackFileData.Format;

                            // STEP 3: Loop each definition inside the file
                            foreach (var manifestData in voicePackFileData.VoicePacks)
                            {
                                // STEP 4: Validate per-definition
                                if (manifestData == null)
                                {
                                    this.Monitor.Log($"---> Skipping null voice definition entry within: {relativePathForLog}", LogLevel.Warn);
                                    continue;
                                }

                                if (string.IsNullOrWhiteSpace(manifestData.VoicePackId) ||
                                    string.IsNullOrWhiteSpace(manifestData.VoicePackName) ||
                                    string.IsNullOrWhiteSpace(manifestData.Character) ||
                                    string.IsNullOrWhiteSpace(manifestData.Language) ||
                                    manifestData.Entries == null)
                                {
                                    this.Monitor.Log($"---> Skipping invalid voice definition (ID: '{manifestData.VoicePackId ?? "N/A"}') within '{relativePathForLog}': Missing required fields (VoicePackId, VoicePackName, Character, Language, Entries).", LogLevel.Warn);
                                    continue;
                                }

                                // Resolve effective format (pack->file->default) & compute major
                                var fmt = manifestData.Format ?? fileLevelFormat ?? "1.0.0";
                                var formatMajor = GetFormatMajor(fmt);

                                // Safety: if any entry has a TranslationKey, auto-upgrade to V2 behavior
                                if (manifestData.Entries.Any(e => !string.IsNullOrWhiteSpace(e.TranslationKey)))
                                    formatMajor = Math.Max(formatMajor, 2);

                                // STEP 6: Process entries into maps
                                var entriesDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);         // V1/V2 text (DisplayPattern or DialogueText)
                                var entriesByFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);        // DialogueFrom (debug/multilingual)
                                var entriesByTk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);           // V2 TranslationKey (with optional :pN)
                                var dialogueFromCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                                // NEW: V2 DisplayPattern index (stripped/canonical)
                                var entriesByDisplayPattern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                                foreach (var entry in manifestData.Entries)
                                {
                                    if (entry == null || string.IsNullOrWhiteSpace(entry.AudioPath))
                                        continue;

                                    string audioPath = PathUtilities.NormalizePath(entry.AudioPath);

                                    // --- Text-based lookup key (kept for V1 & as fallback for V2) ---
                                    string keyForLookup;
                                    if (formatMajor >= 2)
                                        keyForLookup = !string.IsNullOrWhiteSpace(entry.DisplayPattern) ? entry.DisplayPattern : entry.DialogueText;
                                    else
                                        keyForLookup = entry.DialogueText;

                                    if (!string.IsNullOrWhiteSpace(keyForLookup) && !entriesDict.ContainsKey(keyForLookup))
                                        entriesDict[keyForLookup] = audioPath;

                                    // --- DialogueFrom mapping (kept) ---
                                    if (!string.IsNullOrWhiteSpace(entry.DialogueFrom))
                                    {
                                        string baseKey = entry.DialogueFrom;
                                        if (!dialogueFromCounters.ContainsKey(baseKey))
                                            dialogueFromCounters[baseKey] = 0;

                                        int index = dialogueFromCounters[baseKey]++;
                                        string finalKey = index == 0 ? baseKey : $"{baseKey}_{index}";
                                        entriesByFrom[finalKey] = audioPath;
                                    }

                                    // --- V2: TranslationKey mapping (append :pN only for non-Event keys) ---
                                    if (formatMajor >= 2 && !string.IsNullOrWhiteSpace(entry.TranslationKey))
                                    {
                                        string tkKey = entry.TranslationKey;

                                        // IMPORTANT: Events never use :pN. Only append :pN for Characters/Dialogue or Strings.
                                        if (entry.PageIndex.HasValue &&
                                            !tkKey.StartsWith("Events/", StringComparison.OrdinalIgnoreCase))
                                        {
                                            tkKey = $"{tkKey}:p{entry.PageIndex.Value}";
                                        }

                                        if (!entriesByTk.ContainsKey(tkKey))
                                        {
                                            entriesByTk[tkKey] = audioPath;
                                        }
                                        else if (Config.developerModeOn)
                                        {
                                            Monitor.Log($"[Load] Duplicate TranslationKey '{tkKey}' in '{relativePathForLog}'. Keeping first occurrence.", LogLevel.Trace);
                                        }
                                    }

                                    // NEW: V2 DisplayPattern index (canonical form)
                                    if (formatMajor >= 2 && !string.IsNullOrWhiteSpace(entry.DisplayPattern))
                                    {
                                        var dpKey = CanonDisplay(entry.DisplayPattern);
                                        if (!entriesByDisplayPattern.ContainsKey(dpKey))
                                            entriesByDisplayPattern[dpKey] = audioPath;
                                    }
                                }

                                // If *all* maps are empty, skip; otherwise allow (supports pure-TK packs)
                                if (!entriesDict.Any() && !entriesByTk.Any() && !entriesByFrom.Any() && !entriesByDisplayPattern.Any()) // NEW include pattern map
                                {
                                    this.Monitor.Log($"---> Skipping definition '{manifestData.VoicePackName}' from '{relativePathForLog}': No valid entries found within it.", LogLevel.Debug);
                                    continue;
                                }

                                // STEP 7: Create the internal VoicePack object
                                var voicePack = new VoicePack
                                {
                                    VoicePackId = manifestData.VoicePackId,
                                    VoicePackName = manifestData.VoicePackName,
                                    Language = manifestData.Language,
                                    Character = manifestData.Character,

                                    // Keep V1 text map (DisplayPattern/DialogueText)
                                    Entries = entriesDict,

                                    // V2 TranslationKey map
                                    EntriesByTranslationKey = entriesByTk,

                                    // NEW: V2 DisplayPattern map
                                    EntriesByDisplayPattern = entriesByDisplayPattern,

                                    ContentPackId = contentPack.Manifest.UniqueID,
                                    ContentPackName = contentPack.Manifest.Name,
                                    BaseAssetPath = PathUtilities.NormalizePath(Path.GetDirectoryName(filePath)),
                                    FormatMajor = formatMajor
                                };

                                // Keep DialogueFrom map
                                voicePack.EntriesByFrom = entriesByFrom;

                                if (Config.developerModeOn)
                                {
                                    Monitor.Log(
                                        $"[Load] '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) " +
                                        $"char={voicePack.Character} lang={voicePack.Language} " +
                                        $"format='{fmt}' major={voicePack.FormatMajor} " +
                                        $"entries(Text)={voicePack.Entries.Count} entries(TK)={voicePack.EntriesByTranslationKey.Count} entries(Display)={voicePack.EntriesByDisplayPattern.Count} entries(From)={voicePack.EntriesByFrom.Count}",
                                        LogLevel.Debug
                                    );
                                }

                                // STEP 8: Add to internal storage
                                AddVoicePackToStore(voicePack, ref totalDefinitionsProcessed, ref foundAnyValidDefinitionInPack);
                            } // foreach manifestData
                        }
                        catch (JsonException jsonEx)
                        {
                            this.Monitor.Log($"---> Error parsing JSON file '{relativePathForLog}': {jsonEx.Message}", LogLevel.Error);
                        }
                        catch (Exception fileEx)
                        {
                            this.Monitor.Log($"---> Error reading/processing file '{relativePathForLog}': {fileEx.Message}", LogLevel.Error);
                        }
                    } // foreach JSON file

                    // If there were JSON files but no valid definitions within them
                    if (!foundAnyValidDefinitionInPack && ownedContentPacks.Any())
                    {
                        if (Directory.EnumerateFiles(packDir, "*.json", SearchOption.TopDirectoryOnly)
                            .Any(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                        {
                            this.Monitor.Log($"Content Pack '{contentPack.Manifest.Name}' contained JSON files, but none yielded valid voice definitions.", LogLevel.Trace);
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    this.Monitor.Log($"Directory not found for Content Pack '{contentPack.Manifest.Name}': {packDir}. Skipping.", LogLevel.Warn);
                }
                catch (Exception dirEx)
                {
                    this.Monitor.Log($"Error scanning directory '{packDir}' for Content Pack '{contentPack.Manifest.Name}': {dirEx.Message}", LogLevel.Error);
                }
            } // foreach Content Pack

            // Final summary
            this.Monitor.Log($"Finished loading. Processed {totalDefinitionsProcessed} voice definitions for {VoicePacksByCharacter.Count} unique characters from {ownedContentPacks.Count()} content packs.", LogLevel.Info);
        }





        private VoicePack GetSelectedVoicePack(string characterName)
        {
            if (Config.SelectedVoicePacks.TryGetValue(characterName, out string selectedId) &&
                VoicePacksByCharacter.TryGetValue(characterName, out var list))
            {
                return list.FirstOrDefault(p => p.VoicePackId.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private string GetVoicePackLanguageForCharacter(string characterName)
        {
            return GetSelectedVoicePack(characterName)?.Language ?? Config.DefaultLanguage;
        }


      





    }
}