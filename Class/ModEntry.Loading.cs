using System; // Added for StringComparison, StringComparer, Exception
using System.Collections.Generic;
using System.IO;            // Added for Directory, File, Path, SearchOption, DirectoryNotFoundException
using System.Linq;          // Added for .Count(), .Any(), .Select(), .Equals() etc.
using Newtonsoft.Json;    // Added for JsonConvert, JsonException
using StardewModdingAPI;
using StardewModdingAPI.Utilities; // Added for PathUtilities

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // Dictionary to hold loaded voice packs, keyed by character name
        // Value is a list of packs (for different languages or multiple packs for the same char/lang)
        private readonly Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new(StringComparer.OrdinalIgnoreCase);

        private void LoadVoicePacks()
        {
            if (Config.developerModeOn)
            {
                this.Monitor.Log("Scanning Content Packs for voice data definitions...", LogLevel.Debug);
            }
            
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

                    IEnumerable<string> jsonFiles = Directory.EnumerateFiles(packDir, "*.json", SearchOption.TopDirectoryOnly);
                    bool foundAnyValidDefinitionInPack = false;

                    foreach (string filePath in jsonFiles)
                    {
                        if (Path.GetFileName(filePath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Skip content pack manifest
                        }

                        string relativePathForLog = Path.GetRelativePath(packDir, filePath);
                        this.Monitor.Log($"---> Found potential voice definition file: {relativePathForLog}", LogLevel.Trace);

                        try
                        {
                            string jsonContent = File.ReadAllText(filePath);

                            // *** STEP 1 & 2: Deserialize to VoicePackFile and Check ***
                            var voicePackFileData = JsonConvert.DeserializeObject<VoicePackFile>(jsonContent);

                            if (voicePackFileData?.VoicePacks == null || !voicePackFileData.VoicePacks.Any())
                            {
                                // Log if the file structure is wrong or the list is empty
                                this.Monitor.Log($"---> Skipping file '{relativePathForLog}': Invalid structure or empty 'VoicePacks' list found inside.", LogLevel.Trace);
                                continue;
                            }

                            // *** STEP 3: Loop through each definition IN THE LIST ***
                            foreach (var manifestData in voicePackFileData.VoicePacks)
                            {
                                // *** STEP 4: Move Validation and Processing INSIDE the loop ***

                                // 5. Validate the loaded manifest data (now operating on the object from the list)
                                if (manifestData == null) { this.Monitor.Log($"---> Skipping null voice definition entry within: {relativePathForLog}", LogLevel.Warn); continue; }
                                if (string.IsNullOrWhiteSpace(manifestData.VoicePackId) ||
                                    string.IsNullOrWhiteSpace(manifestData.VoicePackName) ||
                                    string.IsNullOrWhiteSpace(manifestData.Character) ||
                                    string.IsNullOrWhiteSpace(manifestData.Language) ||
                                    manifestData.Entries == null)
                                {
                                    // Log which definition *within* the file is invalid
                                    this.Monitor.Log($"---> Skipping invalid voice definition (ID: '{manifestData.VoicePackId ?? "N/A"}') within '{relativePathForLog}': Missing required fields (VoicePackId, VoicePackName, Character, Language, Entries).", LogLevel.Warn);
                                    continue; // Skip this specific definition, continue to next in list
                                }

                                // 6. Process entries into a dictionary
                                var entriesDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var entry in manifestData.Entries)
                                {
                                    if (entry != null && !string.IsNullOrWhiteSpace(entry.DialogueText) && !string.IsNullOrWhiteSpace(entry.AudioPath))
                                    {
                                        string dialogueKey = entry.DialogueText; // Assuming JSON text is the lookup key
                                        string relativeAudioPath = PathUtilities.NormalizePath(entry.AudioPath);

                                        if (!entriesDict.ContainsKey(dialogueKey))
                                        {
                                            entriesDict[dialogueKey] = relativeAudioPath;
                                        }
                                        else
                                        {
                                            this.Monitor.Log($"---> Duplicate dialogue text key found for definition '{manifestData.VoicePackId}' in '{relativePathForLog}': '{dialogueKey}'. Using first encountered audio path.", LogLevel.Trace);
                                        }
                                    }
                                }

                                if (!entriesDict.Any()) { this.Monitor.Log($"---> Skipping definition '{manifestData.VoicePackName}' from '{relativePathForLog}': No valid entries found within it.", LogLevel.Debug); continue; }

                                // 7. Create the internal VoicePack object
                                var voicePack = new VoicePack
                                {
                                    VoicePackId = manifestData.VoicePackId,
                                    VoicePackName = manifestData.VoicePackName,
                                    Language = manifestData.Language,
                                    Character = manifestData.Character,
                                    Entries = entriesDict,
                                    ContentPackId = contentPack.Manifest.UniqueID,
                                    ContentPackName = contentPack.Manifest.Name,
                                    BaseAssetPath = PathUtilities.NormalizePath(packDir)
                                };

                                // 8. Add to internal storage
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
                                    totalDefinitionsProcessed++; // Increment for each successfully processed definition
                                    foundAnyValidDefinitionInPack = true; // Mark that this pack contributed something
                                    if (Config.developerModeOn)
                                    {
                                        this.Monitor.Log($"---> Loaded definition '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) for {voicePack.Character} [{voicePack.Language}] ({entriesDict.Count} entries) from {relativePathForLog}", LogLevel.Debug);
                                    }
                                    
                                }
                                else
                                {
                                    if (Config.developerModeOn)
                                    {
                                        this.Monitor.Log($"---> Skipping duplicate VoicePackId '{voicePack.VoicePackId}' for {voicePack.Character} [{voicePack.Language}] found within file '{relativePathForLog}'. A pack with this ID for this character/language is already loaded.", LogLevel.Trace);
                                    }
                                }

                            } // End foreach manifestData in voicePackFileData.VoicePacks

                        } // End try block for processing a single file
                        catch (JsonException jsonEx) { this.Monitor.Log($"---> Error parsing JSON file '{relativePathForLog}': {jsonEx.Message}", LogLevel.Error); }
                        catch (Exception fileEx) { this.Monitor.Log($"---> Error reading/processing file '{relativePathForLog}': {fileEx.Message}", LogLevel.Error); }

                    } // End foreach JSON file in pack

                    // Adjusted logging for when no valid *definitions* were found in the pack
                    if (!foundAnyValidDefinitionInPack && ownedContentPacks.Any())
                    {
                        // Check if there were actually any JSON files (besides manifest.json) to process
                        if (Directory.EnumerateFiles(packDir, "*.json", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                        {
                            this.Monitor.Log($"Content Pack '{contentPack.Manifest.Name}' contained JSON files, but none yielded valid voice definitions.", LogLevel.Trace);
                        }
                    }
                } // End try block for scanning directory
                catch (DirectoryNotFoundException) { this.Monitor.Log($"Directory not found for Content Pack '{contentPack.Manifest.Name}': {packDir}. Skipping.", LogLevel.Warn); }
                catch (Exception dirEx) { this.Monitor.Log($"Error scanning directory '{packDir}' for Content Pack '{contentPack.Manifest.Name}': {dirEx.Message}", LogLevel.Error); }

            } // End foreach Content Pack

            // Update final log message to reflect definitions processed
            this.Monitor.Log($"Finished loading. Processed {totalDefinitionsProcessed} voice definitions for {VoicePacksByCharacter.Count} unique characters from {ownedContentPacks.Count()} content packs.", LogLevel.Info);
        }
    }
}