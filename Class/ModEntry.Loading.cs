using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // Dictionary to hold loaded voice packs, keyed by character name
        // Value is a list of packs (for different languages or multiple packs for the same char/lang)
        private readonly Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new(StringComparer.OrdinalIgnoreCase);

        private void LoadVoicePacks()
        {
            this.Monitor.Log("Scanning Content Packs for voice data definitions...", LogLevel.Debug);
            VoicePacksByCharacter.Clear(); // Clear previous data

            // 1. Get all loaded Content Packs owned by this framework mod
            var ownedContentPacks = this.Helper.ContentPacks.GetOwned();
            this.Monitor.Log($"Found {ownedContentPacks.Count()} potential voice content packs.", LogLevel.Debug);

            int totalFilesLoaded = 0;

            // 2. Iterate through each discovered Content Pack
            foreach (var contentPack in ownedContentPacks)
            {
                this.Monitor.Log($"Scanning Content Pack: '{contentPack.Manifest.Name}' ({contentPack.Manifest.UniqueID}) at {contentPack.DirectoryPath}", LogLevel.Trace);

                // The base directory for this specific content pack's files
                string packDir = contentPack.DirectoryPath;

                // 3. Scan *inside* the content pack's directory for individual JSON files
                //    (Assuming they are directly in the root, adjust if they are in a subfolder)
                try
                {
                    // Ensure directory exists before enumerating
                    if (!Directory.Exists(packDir))
                    {
                        this.Monitor.Log($"Directory not found for Content Pack '{contentPack.Manifest.Name}': {packDir}. Skipping.", LogLevel.Warn);
                        continue;
                    }

                    IEnumerable<string> jsonFiles = Directory.EnumerateFiles(packDir, "*.json", SearchOption.TopDirectoryOnly); // Only scan root for now

                    bool foundAnyForThisPack = false;
                    foreach (string filePath in jsonFiles)
                    {
                        // IMPORTANT: Skip the manifest.json itself!
                        if (Path.GetFileName(filePath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string relativePathForLog = Path.GetRelativePath(packDir, filePath);
                        this.Monitor.Log($"---> Found potential voice definition file: {relativePathForLog}", LogLevel.Trace);

                        try
                        {
                            // 4. Read and Deserialize each JSON file
                            string jsonContent = File.ReadAllText(filePath);
                            var manifestData = JsonConvert.DeserializeObject<VoicePackManifestTemplate>(jsonContent);

                            // 5. Validate the loaded manifest data
                            if (manifestData == null) { this.Monitor.Log($"---> Failed to deserialize JSON in: {relativePathForLog}", LogLevel.Warn); continue; }
                            if (string.IsNullOrWhiteSpace(manifestData.VoicePackId) ||
                                string.IsNullOrWhiteSpace(manifestData.VoicePackName) ||
                                string.IsNullOrWhiteSpace(manifestData.Character) ||
                                string.IsNullOrWhiteSpace(manifestData.Language) ||
                                manifestData.Entries == null)
                            {
                                this.Monitor.Log($"---> Skipping invalid voice manifest in '{relativePathForLog}': Missing required fields (VoicePackId, VoicePackName, Character, Language, Entries).", LogLevel.Warn);
                                continue;
                            }

                            // 6. Process entries into a dictionary (Dialogue Text -> Relative Audio Path)
                            var entriesDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var entry in manifestData.Entries)
                            {
                                if (entry != null && !string.IsNullOrWhiteSpace(entry.DialogueText) && !string.IsNullOrWhiteSpace(entry.AudioPath))
                                {
                                    // *** CRITICAL: Use the DialogueText from the entry directly as the key ***
                                    // The TryPlayVoice needs to match this exact text after its own sanitization.
                                    // Ensure SanitizeDialogueText used in TryPlayVoice matches *exactly* how keys might need to be if they differ from JSON text.
                                    // If JSON text is already sanitized/cleaned, this is fine. If not, the lookup key needs cleaning too.
                                    // Assuming for now that the JSON DialogueText is the lookup key.
                                    string dialogueKey = entry.DialogueText; // Or SanitizeDialogueText(entry.DialogueText) if JSON text needs cleaning for lookup
                                    string relativeAudioPath = PathUtilities.NormalizePath(entry.AudioPath);

                                    if (!entriesDict.ContainsKey(dialogueKey))
                                    {
                                        entriesDict[dialogueKey] = relativeAudioPath;
                                    }
                                    else
                                    {
                                        this.Monitor.Log($"---> Duplicate dialogue text key found in '{relativePathForLog}': '{dialogueKey}'. Using first encountered audio path.", LogLevel.Trace);
                                    }
                                }
                            }

                            if (!entriesDict.Any()) { this.Monitor.Log($"---> Skipping '{manifestData.VoicePackName}' from '{relativePathForLog}': No valid entries.", LogLevel.Debug); continue; }

                            // 7. Create the internal VoicePack object, including Content Pack info
                            var voicePack = new VoicePack
                            {
                                VoicePackId = manifestData.VoicePackId, // The ID from the JSON file itself
                                VoicePackName = manifestData.VoicePackName,
                                Language = manifestData.Language,
                                Character = manifestData.Character,
                                Entries = entriesDict,
                                ContentPackId = contentPack.Manifest.UniqueID, // ID of the container pack
                                ContentPackName = contentPack.Manifest.Name,   // Name of the container pack
                                BaseAssetPath = PathUtilities.NormalizePath(packDir) // Base path for resolving relative audio paths
                            };

                            // 8. Add to internal storage (VoicePacksByCharacter)
                            if (!VoicePacksByCharacter.TryGetValue(voicePack.Character, out var list))
                            {
                                list = new List<VoicePack>();
                                VoicePacksByCharacter[voicePack.Character] = list;
                            }

                            // Check for duplicates based on VoicePackId WITHIN Character+Language
                            if (!list.Any(p => p.Language.Equals(voicePack.Language, StringComparison.OrdinalIgnoreCase) &&
                                                 p.VoicePackId.Equals(voicePack.VoicePackId, StringComparison.OrdinalIgnoreCase)))
                            {
                                list.Add(voicePack);
                                totalFilesLoaded++;
                                foundAnyForThisPack = true;
                                this.Monitor.Log($"---> Loaded '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) for {voicePack.Character} [{voicePack.Language}] ({entriesDict.Count} entries) from {relativePathForLog}", LogLevel.Debug);
                            }
                            else
                            {
                                this.Monitor.Log($"---> Skipping duplicate VoicePackId '{voicePack.VoicePackId}' for {voicePack.Character} [{voicePack.Language}] found in file '{relativePathForLog}'. A pack with this ID for this character/language is already loaded.", LogLevel.Trace);
                            }
                        }
                        catch (JsonException jsonEx) { this.Monitor.Log($"---> Error parsing JSON file '{relativePathForLog}': {jsonEx.Message}", LogLevel.Error); }
                        catch (Exception fileEx) { this.Monitor.Log($"---> Error reading/processing file '{relativePathForLog}': {fileEx.Message}", LogLevel.Error); }

                    } // End foreach JSON file in pack

                    if (!foundAnyForThisPack && ownedContentPacks.Any()) // Only log if we expected files
                    {
                        // Don't log this if jsonFiles was empty to begin with
                        if (jsonFiles.Any(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                        {
                            this.Monitor.Log($"Content Pack '{contentPack.Manifest.Name}' did not contain any valid voice definition JSON files (excluding manifest.json).", LogLevel.Trace);
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

            } // End foreach Content Pack

            this.Monitor.Log($"Finished loading. Loaded {totalFilesLoaded} voice definition files for {VoicePacksByCharacter.Count} unique characters from {ownedContentPacks.Count()} content packs.", LogLevel.Info);
        }
    }
}