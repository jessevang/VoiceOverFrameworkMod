// VoiceOverFrameworkMod.cs

// --- Required using statements ---
using System;
using System.Collections.Generic;
using System.IO; // Needed for Path, File, Directory
using System.Linq; // Needed for Linq operations like OrderBy, Any, FirstOrDefault
using System.Text.RegularExpressions; // Needed for SanitizeKeyForFileName
// using System.Xml; // Removed as unused
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json; // For JSON serialization/deserialization
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using VoiceOverFrameworkMod.Menus; // Make sure this namespace matches your Menu folder

namespace VoiceOverFrameworkMod
{
    // --- Supporting Class Definitions ---
    // Structure loaded from config.json
    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public bool FallbackToDefaultIfMissing { get; set; } = true;
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new(); // Maps Character Name -> VoicePackId
    }

    // Structure read from voice pack content.json
    public class VoicePackWrapper
    {
        public List<VoicePackManifest> VoicePacks { get; set; }
    }

    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; }
        public List<VoiceEntry> Entries { get; set; }
    }

    public class VoiceEntry
    {
        public string DialogueKey { get; set; }
        public string AudioPath { get; set; }
    }

    // Internal representation of a loaded voice pack
    public class VoicePack
    {
        public string VoicePackId;
        public string VoicePackName;
        public string Character;
        public string Language;
        public Dictionary<string, string> Entries; // Maps DialogueKey -> Full Audio File Path
    }

    // Structure for the generated template JSON file
    public class VoicePackWrapperTemplate
    {
        public List<VoicePackManifestTemplate> VoicePacks { get; set; } = new List<VoicePackManifestTemplate>();
    }

    public class VoicePackManifestTemplate
    {
        public string Format { get; set; } = "1.0.0";
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; } = "en";
        public List<VoiceEntryTemplate> Entries { get; set; } = new List<VoiceEntryTemplate>();
    }

    // UPDATED VoiceEntryTemplate to include Dialogue Text
    public class VoiceEntryTemplate
    {
        public string DialogueKey { get; set; }
        public string DialogueText { get; set; } // The actual text for reference
        public string AudioPath { get; set; } // Placeholder path
    }


    // --- Main Mod Class ---
    public partial class ModEntry : Mod // Using partial if you split files later, otherwise just 'class ModEntry'
    {
        public static ModEntry Instance;

        private ModConfig Config;
        private Dictionary<string, string> SelectedVoicePacks;
        private Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new();

        private SoundEffectInstance currentVoiceInstance;

        // *** ADD THESE TWO LINES BACK ***
        private string lastDialogueText = null; // For UpdateTicked method (consider replacing with Harmony)
        private string lastSpeakerName = null; // For UpdateTicked method (consider replacing with Harmony)
        // *******************************

        // List of known Stardew language codes
        private readonly List<string> KnownStardewLanguages = new List<string> {
            "en", "es-ES", "zh-CN", "ja-JP", "pt-BR", "fr-FR", "ko-KR", "it-IT", "de-DE", "hu-HU", "ru-RU", "tr-TR"
        };

        // --- Mod Entry Point ---
        public override void Entry(IModHelper helper)
        {
            Instance = this;

            Config = helper.ReadConfig<ModConfig>();
            SelectedVoicePacks = Config.SelectedVoicePacks;

            LoadVoicePacks();

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            SetupConsoleCommands(helper.ConsoleCommands);

            Monitor.Log("Voice Over Framework initialized.", LogLevel.Info);
        }

        // --- Event Handlers ---
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e) { /* For GMCM */ }

        private void LoadVoicePacks()
        {
            VoicePacksByCharacter.Clear();
            var allContentPacks = this.Helper.ContentPacks.GetOwned();
            Monitor.Log($"Scanning {allContentPacks.Count()} content packs for voice data...", LogLevel.Debug);

            foreach (var pack in allContentPacks)
            {
                try
                {
                    var wrapper = pack.ReadJsonFile<VoicePackWrapper>("content.json");
                    if (wrapper?.VoicePacks == null) continue;
                    Monitor.Log($"Found voice pack definitions in '{pack.Manifest.Name}'.", LogLevel.Trace);
                    foreach (var metadata in wrapper.VoicePacks)
                    {
                        if (string.IsNullOrWhiteSpace(metadata.VoicePackId) || string.IsNullOrWhiteSpace(metadata.VoicePackName) || string.IsNullOrWhiteSpace(metadata.Character) || metadata.Entries == null)
                        { Monitor.Log($"Skipping invalid voice pack entry in '{pack.Manifest.Name}': Missing required fields.", LogLevel.Warn); continue; }

                        var voicePack = new VoicePack
                        {
                            VoicePackId = metadata.VoicePackId,
                            VoicePackName = metadata.VoicePackName,
                            Language = metadata.Language ?? "en",
                            Character = metadata.Character,
                            Entries = metadata.Entries.ToDictionary(e => e.DialogueKey, e => Path.Combine(pack.DirectoryPath, e.AudioPath))
                        };

                        if (!VoicePacksByCharacter.ContainsKey(voicePack.Character)) { VoicePacksByCharacter[voicePack.Character] = new List<VoicePack>(); }
                        if (!VoicePacksByCharacter[voicePack.Character].Any(p => p.VoicePackId == voicePack.VoicePackId && p.Language == voicePack.Language))
                        {
                            VoicePacksByCharacter[voicePack.Character].Add(voicePack);
                            Monitor.Log($"Loaded voice pack '{voicePack.VoicePackName}' ({voicePack.VoicePackId}) for {voicePack.Character} [{voicePack.Language}].", LogLevel.Trace);
                        }
                        else { Monitor.Log($"Skipping duplicate voice pack ID '{voicePack.VoicePackId}' for {voicePack.Character} [{voicePack.Language}] in '{pack.Manifest.Name}'.", LogLevel.Warn); }
                    }
                }
                catch (Exception ex) { Monitor.Log($"Error loading voice pack definition from '{pack.Manifest.Name}': {ex.Message}", LogLevel.Error); }
            }
            Monitor.Log($"Finished loading voice packs. Found packs for {VoicePacksByCharacter.Count} unique characters.", LogLevel.Debug);
        }

        // UpdateTicked still needs Harmony replacement for reliable key capture
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentSpeaker == null || !Game1.dialogueUp)
            { if (lastDialogueText != null || lastSpeakerName != null) { lastDialogueText = null; lastSpeakerName = null; } return; }
            NPC speaker = Game1.currentSpeaker; Dialogue currentDialogue = speaker?.CurrentDialogue?.FirstOrDefault(); string currentText = currentDialogue?.getCurrentDialogue()?.Trim();
            if (string.IsNullOrEmpty(currentText) || speaker.Name == null) return;
            if (currentText == lastDialogueText && speaker.Name == lastSpeakerName) return;
            lastDialogueText = currentText; lastSpeakerName = speaker.Name;
            // Cannot accurately play voice without the original DialogueKey
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            if (e.Button == SButton.F12) // Test menu key
            {
                if (!VoicePacksByCharacter.Any()) { Monitor.Log("Cannot open voice test menu - no voice packs loaded.", LogLevel.Warn); Game1.drawObjectDialogue("No voice packs loaded."); return; }
                Game1.activeClickableMenu = new VoiceTestMenu(VoicePacksByCharacter);
            }
        }

        // --- Core Voice Playback Logic ---
        public void TryPlayVoice(string characterName, string dialogueKey)
        {
            Monitor.Log($"[Voice] Attempting voice: Char='{characterName}', Key='{dialogueKey}'", LogLevel.Trace);
            if (!Config.SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) || string.IsNullOrEmpty(selectedVoicePackId)) return;
            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks)) return;
            string language = Config.DefaultLanguage;
            var selectedPack = availablePacks.FirstOrDefault(p => p.VoicePackId == selectedVoicePackId && p.Language == language);
            if (selectedPack == null && Config.FallbackToDefaultIfMissing && language != "en") { selectedPack = availablePacks.FirstOrDefault(p => p.VoicePackId == selectedVoicePackId && p.Language == "en"); }
            if (selectedPack == null) { Monitor.Log($"Selected voice pack ID='{selectedVoicePackId}' (Lang='{language}') not found among loaded packs for {characterName}.", LogLevel.Warn); return; }
            if (selectedPack.Entries.TryGetValue(dialogueKey, out string audioPath)) { Monitor.Log($"Found audio for key '{dialogueKey}' in pack '{selectedPack.VoicePackName}': {audioPath}", LogLevel.Trace); PlayVoiceFromFile(audioPath); }
        }

        private void PlayVoiceFromFile(string audioFilePath)
        {
            try
            {
                currentVoiceInstance?.Stop(); currentVoiceInstance?.Dispose(); currentVoiceInstance = null;
                if (!File.Exists(audioFilePath)) { Monitor.Log($"Audio file not found: {audioFilePath}", LogLevel.Warn); return; }
                using (var stream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read)) { SoundEffect sound = SoundEffect.FromStream(stream); currentVoiceInstance = sound.CreateInstance(); currentVoiceInstance.Play(); Monitor.Log($"Playing: {Path.GetFileName(audioFilePath)}", LogLevel.Debug); }
            }
            catch (Exception ex) { Monitor.Log($"Failed to play audio '{audioFilePath}': {ex.Message}", LogLevel.Error); Monitor.Log(ex.ToString(), LogLevel.Trace); currentVoiceInstance = null; }
        }

        // --- Console Command Setup & Implementation ---
        private void SetupConsoleCommands(ICommandHelper commands)
        {
            commands.Add("voice_create_template",
                         "Generates template content.json(s) for vanilla character(s) in specified language(s).\n\nUsage:\n  voice_create_template <CharacterName|all> [LanguageCode|all]\n\nExamples:\n  voice_create_template Harvey              (generates English template for Harvey)\n  voice_create_template Abigail fr-FR         (generates French template for Abigail)\n  voice_create_template all es-ES           (generates Spanish templates for all chars)\n  voice_create_template Harvey all          (generates template for Harvey in all languages)\n  voice_create_template all all             (generates templates for all chars in all langs)",
                         this.GenerateTemplateCommand);

            commands.Add("voice_list_chars",
                         "Lists known characters found in Game1.characterData.",
                         this.ListCharactersCommand);
        }

        private void GenerateTemplateCommand(string command, string[] args)
        {
            if (args.Length < 1)
            { this.Monitor.Log("Please provide a character name or 'all', and optionally a language code or 'all'.", LogLevel.Error); this.Monitor.Log("Usage: voice_create_template <CharacterName|all> [LanguageCode|all]", LogLevel.Info); return; }
            if (!Context.IsWorldReady)
            { this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn); return; }

            string targetCharacterArg = args[0];
            string targetLanguageArg = (args.Length > 1) ? args[1] : (Config.DefaultLanguage ?? "en");

            List<string> languagesToProcess = new List<string>();
            List<string> charactersToProcess = new List<string>();

            // Determine Languages
            if (targetLanguageArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetLanguageArg == "*")
            { languagesToProcess.AddRange(this.KnownStardewLanguages); this.Monitor.Log($"Processing for all {languagesToProcess.Count} known languages.", LogLevel.Info); }
            else
            { languagesToProcess.Add(GetValidatedLanguageCode(targetLanguageArg)); this.Monitor.Log($"Processing for language: {languagesToProcess[0]}", LogLevel.Info); }

            // Determine Characters
            if (targetCharacterArg.Equals("all", StringComparison.OrdinalIgnoreCase) || targetCharacterArg == "*")
            {
                this.Monitor.Log("Gathering list of all characters from Game1.characterData...", LogLevel.Info);
                try
                {
                    if (Game1.characterData != null && Game1.characterData.Any())
                    {
                        charactersToProcess = Game1.characterData.Keys.Where(name => !string.IsNullOrWhiteSpace(name) && IsKnownVanillaVillager(name)).OrderBy(name => name).ToList();
                        this.Monitor.Log($"Found {charactersToProcess.Count} characters to process.", LogLevel.Info);
                    }
                    else { this.Monitor.Log("Game1.characterData is null or empty. Cannot process 'all'.", LogLevel.Error); return; }
                }
                catch (Exception ex) { this.Monitor.Log($"Error retrieving character list: {ex.Message}", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); return; }
            }
            else { charactersToProcess.Add(targetCharacterArg); }

            // Loop through languages and characters
            int totalSuccessCount = 0; int totalFailCount = 0;
            string baseTemplateDir = Path.Combine(this.Helper.DirectoryPath, "GeneratedTemplates");

            foreach (string languageCode in languagesToProcess)
            {
                this.Monitor.Log($"--- Processing Language: {languageCode} ---", LogLevel.Info);
                int langSuccessCount = 0; int langFailCount = 0;
                foreach (string characterName in charactersToProcess)
                {
                    if (GenerateSingleTemplate(characterName, languageCode, baseTemplateDir)) { langSuccessCount++; } else { langFailCount++; }
                }
                this.Monitor.Log($"Language {languageCode} Summary - Generated: {langSuccessCount}, Failed/Skipped: {langFailCount}", LogLevel.Info);
                totalSuccessCount += langSuccessCount; totalFailCount += langFailCount;
            }

            this.Monitor.Log($"--- Overall Template Generation Complete ---", LogLevel.Info);
            this.Monitor.Log($"Total Successfully generated: {totalSuccessCount}", LogLevel.Info);
            this.Monitor.Log($"Total Failed/Skipped: {totalFailCount}", LogLevel.Info);
        }

        private bool GenerateSingleTemplate(string characterName, string languageCode, string baseOutputDir)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return false;

            try
            {
                var discoveredKeyTextPairs = new Dictionary<string, string>();

                // Load individual Dialogue File (Language Aware)
                string dialogueAssetKey = $"Characters/Dialogue/{characterName}";
                bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
                if (!isEnglish) { dialogueAssetKey += $".{languageCode}"; }

                try
                {
                    var dialogueData = this.Helper.GameContent.Load<Dictionary<string, string>>(dialogueAssetKey);
                    if (dialogueData != null) { foreach (var kvp in dialogueData) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; } Monitor.Log($"Loaded {dialogueData.Count} keys from '{dialogueAssetKey}'.", LogLevel.Debug); }
                    else { Monitor.Log($"'{dialogueAssetKey}' loaded null.", LogLevel.Trace); }
                }
                catch (Microsoft.Xna.Framework.Content.ContentLoadException) { Monitor.Log($"Asset '{dialogueAssetKey}' not found.", LogLevel.Trace); }
                catch (Exception ex) { Monitor.Log($"Error loading '{dialogueAssetKey}': {ex.Message}", LogLevel.Warn); }

                // Check Strings/Characters (Language Aware)
                var stringCharData = GetVanillaCharacterStringKeys(characterName, languageCode, this.Helper.GameContent);
                foreach (var kvp in stringCharData) { discoveredKeyTextPairs[kvp.Key] = kvp.Value; } // Merge/overwrite

                // TODO: Add parsing for Events, Festivals, etc.

                Monitor.Log($"Found {discoveredKeyTextPairs.Count} potential key/text pairs total for '{characterName}' (Lang: {languageCode}).", LogLevel.Debug);
                if (!discoveredKeyTextPairs.Any()) { Monitor.Log($"No keys found for '{characterName}' in language '{languageCode}'. Skipping template generation.", LogLevel.Warn); return false; }

                // Create Manifest Structure
                var wrapper = new VoicePackWrapperTemplate();
                var manifest = new VoicePackManifestTemplate
                {
                    VoicePackId = $"Vanilla_{characterName}_{languageCode}_Template",
                    VoicePackName = $"{characterName} - Vanilla Template ({languageCode})",
                    Character = characterName,
                    Language = languageCode
                };
                foreach (var kvp in discoveredKeyTextPairs.OrderBy(p => p.Key))
                {
                    manifest.Entries.Add(new VoiceEntryTemplate
                    {
                        DialogueKey = kvp.Key,
                        DialogueText = SanitizeDialogueText(kvp.Value),
                        AudioPath = $"assets/{languageCode}/{characterName}/{SanitizeKeyForFileName(kvp.Key)}.wav"
                    });
                }
                wrapper.VoicePacks.Add(manifest);
                string jsonOutput = JsonConvert.SerializeObject(wrapper, Newtonsoft.Json.Formatting.Indented);

                // Define Output Paths
                string langSpecificDir = Path.Combine(baseOutputDir, languageCode);
                string templateJsonPath = Path.Combine(langSpecificDir, $"{characterName}_VoicePack_Template.json");
                string assetsBasePath = Path.Combine(langSpecificDir, "assets");
                string assetsLangPath = Path.Combine(assetsBasePath, languageCode);
                string assetsCharacterPath = Path.Combine(assetsLangPath, characterName);

                // Save JSON File
                Directory.CreateDirectory(langSpecificDir);
                Monitor.Log($"Attempting to write template JSON to: {templateJsonPath}", LogLevel.Debug);
                File.WriteAllText(templateJsonPath, jsonOutput);
                bool fileExists = File.Exists(templateJsonPath);
                Monitor.Log($"JSON File.Exists check immediately after write returned: {fileExists}", LogLevel.Debug);
                if (!fileExists && !string.IsNullOrWhiteSpace(jsonOutput)) { Monitor.Log($"Write operation for JSON completed without error, but File.Exists returned false! Output path: {templateJsonPath}", LogLevel.Error); }

                // Create Asset Folders
                try
                {
                    Monitor.Log($"Attempting to create asset directory: {assetsCharacterPath}", LogLevel.Trace); Directory.CreateDirectory(assetsCharacterPath);
                    if (!Directory.Exists(assetsCharacterPath)) { Monitor.Log($"Failed to verify creation of asset directory: {assetsCharacterPath}", LogLevel.Warn); }
                    else { Monitor.Log($"Asset directory structure created successfully.", LogLevel.Trace); }
                }
                catch (Exception dirEx) { Monitor.Log($"Error creating asset directory '{assetsCharacterPath}': {dirEx.Message}", LogLevel.Error); }

                Monitor.Log($"Successfully generated template for {characterName} ({languageCode})!", LogLevel.Info);
                Monitor.Log($"Saved JSON to: {templateJsonPath}", LogLevel.Info);
                Monitor.Log($"Created asset folder structure at: {assetsCharacterPath}", LogLevel.Info);
                return true;

            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to generate template for {characterName} ({languageCode}): {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Error);
                return false;
            }
        }

        private Dictionary<string, string> GetVanillaCharacterStringKeys(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var keyTextPairs = new Dictionary<string, string>();
            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string assetKey = isEnglish ? "Strings/Characters" : $"Strings/Characters.{languageCode}";

            try
            {
                var characterStrings = gameContent.Load<Dictionary<string, string>>(assetKey);
                if (characterStrings == null) { Monitor.Log($"'{assetKey}' loaded as null!", LogLevel.Warn); return keyTextPairs; }
                Monitor.Log($"'{assetKey}' loaded with {characterStrings.Count} entries.", LogLevel.Trace);
                string prefix = characterName + "_"; string marriagePrefix = "MarriageDialogue." + characterName + "_"; int foundCount = 0;
                foreach (var kvp in characterStrings)
                {
                    if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || kvp.Key.StartsWith(marriagePrefix, StringComparison.OrdinalIgnoreCase))
                    { keyTextPairs[kvp.Key] = kvp.Value; foundCount++; }
                }
                Monitor.Log($"Found {foundCount} key/text pairs matching prefixes in '{assetKey}' for {characterName}.", LogLevel.Trace);
            }
            catch (Microsoft.Xna.Framework.Content.ContentLoadException) { Monitor.Log($"Asset '{assetKey}' not found.", LogLevel.Trace); }
            catch (Exception ex) { Monitor.Log($"Error loading/parsing {assetKey}: {ex.Message}", LogLevel.Error); Monitor.Log(ex.ToString(), LogLevel.Trace); }
            return keyTextPairs;
        }

        private void ListCharactersCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady) { this.Monitor.Log("Please load a save file before running this command.", LogLevel.Warn); return; }
            this.Monitor.Log("Listing characters found in Game1.characterData...", LogLevel.Info);
            try
            {
                if (Game1.characterData == null) { this.Monitor.Log("Game1.characterData is null.", LogLevel.Error); return; }
                var characterKeys = Game1.characterData.Keys.OrderBy(name => name).ToList();
                if (!characterKeys.Any()) { this.Monitor.Log("Game1.characterData is empty.", LogLevel.Warn); return; }
                this.Monitor.Log($"Found {characterKeys.Count} character entries:", LogLevel.Info);
                foreach (string key in characterKeys)
                {
                    if (key == "???" || key == "Bear" || key == "Old Mariner" || key == "Grandpa" || key == "Bouncer" || key == "Henchman" || key == "Mister Qi" || key == "Governor" || key == "Welwick" || key == "Birdie" || key == "Gil") { continue; }
                    this.Monitor.Log($"- {key}", LogLevel.Info);
                }
            }
            catch (Exception ex) { this.Monitor.Log($"An error occurred while listing characters from Game1.characterData: {ex.Message}", LogLevel.Error); this.Monitor.Log(ex.ToString(), LogLevel.Trace); }
        }

        // --- Utility Helpers ---
        private string SanitizeKeyForFileName(string key)
        {
            key = key.Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace(" ", "_").Replace(".", "_");
            key = Regex.Replace(key, @"[^\w\-]", "");
            const int MaxLength = 60; if (key.Length > MaxLength) key = key.Substring(0, MaxLength); if (string.IsNullOrWhiteSpace(key)) key = "invalid_key";
            return key;
        }

        private bool IsKnownVanillaVillager(string name)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey", "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane",
                "Caroline", "Clint", "Demetrius", "Evelyn", "George", "Gus", "Jas", "Jodi", "Kent", "Lewis", "Linus", "Marnie",
                "Pam", "Pierre", "Robin", "Vincent", "Willy", "Wizard", "Krobus", "Dwarf", "Sandy", "Leo" };
            return known.Contains(name);
        }

        private string GetValidatedLanguageCode(string requestedLang)
        {
            var langMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "en", "en" }, { "english", "en" }, { "es", "es-ES" }, { "spanish", "es-ES" }, { "zh", "zh-CN" }, { "chinese", "zh-CN" }, { "ja", "ja-JP" }, { "japanese", "ja-JP" }, { "pt", "pt-BR" }, { "portuguese", "pt-BR" }, { "fr", "fr-FR" }, { "french", "fr-FR" }, { "ko", "ko-KR" }, { "korean", "ko-KR" }, { "it", "it-IT" }, { "italian", "it-IT" }, { "de", "de-DE" }, { "german", "de-DE" }, { "hu", "hu-HU" }, { "hungarian", "hu-HU" }, { "ru", "ru-RU" }, { "russian", "ru-RU" }, { "tr", "tr-TR" }, { "turkish", "tr-TR" } };
            foreach (var kvp in langMap.ToList()) { if (!langMap.ContainsKey(kvp.Value)) langMap[kvp.Value] = kvp.Value; }
            if (langMap.TryGetValue(requestedLang, out string stardewCode)) { return stardewCode; }
            this.Monitor.Log($"Language code '{requestedLang}' not explicitly mapped, using as-is.", LogLevel.Trace);
            return requestedLang;
        }

        private string SanitizeDialogueText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = Regex.Replace(text, @"\$[a-zA-Z]\b", ""); text = Regex.Replace(text, @"%[a-zA-Z]+\b", ""); text = Regex.Replace(text, @"#[^#]+#", ""); text = Regex.Replace(text, @"\^", ""); text = Regex.Replace(text, @"<", ""); text = Regex.Replace(text, @"\\", "");
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();
            return text;
        }

    } // End of ModEntry class
} // End of namespace