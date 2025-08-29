using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Microsoft.Xna.Framework.Audio;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // Dialogue State Tracking
        private bool wasDialogueUpLastTick = false;
        private string lastDialogueText = null;
        private int lastDialoguePage = -1;
        private string lastSpeakerName = null;
        private MultilingualDictionary Multilingual;

        internal NPC CurrentDialogueSpeaker = null;
        internal string CurrentDialogueOriginalKey = null;

        internal bool IsMultiPageDialogueActive { get; set; } = false;


        // --- Tunables ---
        private const int DialogueCloseDebounceTicks = 0; // how many consecutive "not visible" ticks before we treat it as a real close
        //private const int TextStabilizeTicks = 8;         // how many consecutive "same text" ticks before we play audio


        // --- Dialogue debounce / replay guards ---
        private int _dialogueNotVisibleTicks = 0;   // count consecutive ticks the dialogue box is not visible
        private int _sameLineStableTicks = 0;       // count consecutive ticks the same text is shown
        private string _lastPlayedLookupKey = null; // last key actually played for this page



        // Chooses the right sanitizer based on the loaded pack's FormatMajor
        private string SanitizeForPack(string raw, VoicePack pack)
            => (pack?.FormatMajor ?? 1) >= 2 ? SanitizeDialogueTextV2(raw) : SanitizeDialogueText(raw);



        // Main dialogue check loop called every tick (or less often if adjusted).
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (e.IsMultipleOf(2)) // keep your existing cadence
                CleanupStoppedVoiceInstances();

            // ---- Pick exactly ONE pipeline per tick to avoid duplicate playback ----
            var speaker = Game1.currentSpeaker;
            if (speaker != null)
            {
                var pack = GetSelectedVoicePack(speaker.Name);
                if (pack != null && pack.FormatMajor >= 2)
                {
                    CheckForDialogueV2();
                    return; // prevent V1 from also handling the same line
                }
            }

            CheckForDialogue();
        }




        // V2-only dialogue checker that reuses V1 state fields (lastDialogueText, wasDialogueUpLastTick, etc.)
        private void CheckForDialogueV2()
        {
            // Do nothing if no location/player
            if (Game1.currentLocation == null || Game1.player == null)
            {
                if (lastDialogueText != null) ResetDialogueState(); // reuse V1 reset
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;

            // Only proceed if the current speaker has a selected V2 pack
            VoicePack selectedPack = null;
            if (currentSpeaker != null)
            {
                selectedPack = GetSelectedVoicePack(currentSpeaker.Name);
                if (selectedPack == null || selectedPack.FormatMajor < 2)
                {
                    // Not a V2 speaker/pack — let your existing V1 pipeline handle it
                    if (wasDialogueUpLastTick) ResetDialogueState();
                    return;
                }
            }

            // --- Debounce transient close between pages ---
            if (!isDialogueBoxVisible)
            {
                if (wasDialogueUpLastTick)
                {
                    _dialogueNotVisibleTicks++;
                    if (_dialogueNotVisibleTicks >= DialogueCloseDebounceTicks) // require consecutive ticks before reset
                    {
                        ResetDialogueState();
                        _dialogueNotVisibleTicks = 0;
                    }
                }
                return; // nothing to do this tick
            }
            else
            {
                _dialogueNotVisibleTicks = 0; // visible, clear debounce
            }

            string currentDisplayedString = null;
            if (isDialogueBoxVisible)
            {
                var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDisplayedString = dialogueBox?.getCurrentString();
            }

            // --- Text stabilization ---
            if (!string.IsNullOrWhiteSpace(currentDisplayedString))
            {
                if (currentDisplayedString == lastDialogueText)
                {
                    _sameLineStableTicks++;
                }
                else
                {
                    // new text/page detected
                    lastDialogueText = currentDisplayedString;  // reuse V1 state
                    lastSpeakerName = currentSpeaker?.Name;     // reuse V1 state
                    wasDialogueUpLastTick = true;               // reuse V1 state
                    _sameLineStableTicks = 0;
                    _lastPlayedLookupKey = null;                // allow one play for this page
                }

                if (_sameLineStableTicks < Config.TextStabilizeTicks) // wait more ticks for stability
                    return;

                if (currentSpeaker != null)
                {
                    string farmerName = Game1.player.Name;
                    string potentialOriginalText = currentDisplayedString;

                    // normalize farmer name to '@' - same assumption as V1
                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

                    // --- V2 sanitizer for the lookup key ---
                    string sanitizedStep1 = SanitizeDialogueTextV2(potentialOriginalText);

                    // Remove inline #...# emote/meta after sanitization
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                    var currentLanguageCode = LocalizedContentManager.CurrentLanguageCode;
                    string gameLanguage = currentLanguageCode.ToString();
                    string characterName = currentSpeaker.Name;
                    string voicePackLanguage = GetVoicePackLanguageForCharacter(characterName);

                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        // Prevent replay on same page
                        if (string.Equals(finalLookupKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                        {
                            if (Config.developerModeOn)
                                Monitor.Log($"[VOICE V2] Debounced repeat key on same page: '{finalLookupKey}'", LogLevel.Trace);
                            return;
                        }

                        if (gameLanguage == voicePackLanguage)
                        {
                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[VOICE V2] Same language ({gameLanguage}). Using sanitized key.", LogLevel.Trace);
                                Monitor.Log($"Attempting V2 voice for '{characterName}'. Lookup Key: '{finalLookupKey}' (From Displayed: '{currentDisplayedString}')", LogLevel.Debug);
                            }
                            TryToPlayVoice(characterName, finalLookupKey, currentLanguageCode);
                            _lastPlayedLookupKey = finalLookupKey; // mark as played
                        }
                        else
                        {
                            // multilingual path (reuse your dictionary)
                            if (Config.developerModeOn)
                                Monitor.Log($"[VOICE V2 - MULTILINGUAL] Resolving cross-lang mapping...", LogLevel.Info);

                            string resolvedFrom = Multilingual?.GetDialogueFrom(characterName, gameLanguage, voicePackLanguage, currentDisplayedString);

                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"Character: {characterName}", LogLevel.Info);
                                Monitor.Log($"Game Language: {gameLanguage}", LogLevel.Info);
                                Monitor.Log($"Voice Pack Language: {voicePackLanguage}", LogLevel.Info);
                                Monitor.Log($"Original Game Dialogue: \"{currentDisplayedString}\"", LogLevel.Info);
                                Monitor.Log($"Sanitized (V2): \"{finalLookupKey}\"", LogLevel.Info);
                                if (!string.IsNullOrEmpty(resolvedFrom))
                                    Monitor.Log($"Dictionary Match Found: DialogueFrom = \"{resolvedFrom}\"", LogLevel.Info);
                            }

                            if (!string.IsNullOrEmpty(resolvedFrom))
                            {
                                TryToPlayVoiceFromDialogueKey(characterName, resolvedFrom, currentLanguageCode);
                                _lastPlayedLookupKey = finalLookupKey; // mark as played
                            }
                        }
                    }
                }
            }
            // no immediate reset here; handled by debounce above
        }






        // Checks the current game state for active dialogue boxes and triggers voice playback.
        private void CheckForDialogue()
        {
            if (Game1.currentLocation == null || Game1.player == null) // || !Context.IsWorldReady
            {
                if (lastDialogueText != null) ResetDialogueState(); // Clear state if we exit world context
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;

            // --- Debounce transient close between pages ---
            if (!isDialogueBoxVisible)
            {
                if (wasDialogueUpLastTick)
                {
                    _dialogueNotVisibleTicks++;
                    if (_dialogueNotVisibleTicks >= DialogueCloseDebounceTicks) // require consecutive ticks before reset
                    {
                        ResetDialogueState();
                        _dialogueNotVisibleTicks = 0;
                    }
                }
                return;
            }
            else
            {
                _dialogueNotVisibleTicks = 0; // visible again
            }

            string currentDisplayedString = null; // Renamed for clarity
            if (isDialogueBoxVisible)
            {
                DialogueBox dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDisplayedString = dialogueBox?.getCurrentString();
            }

            // --- Text stabilization ---
            if (!string.IsNullOrWhiteSpace(currentDisplayedString))
            {
                if (currentDisplayedString == lastDialogueText)
                {
                    _sameLineStableTicks++;
                }
                else
                {
                    // new text/page detected
                    lastDialogueText = currentDisplayedString;
                    lastSpeakerName = currentSpeaker?.Name;
                    wasDialogueUpLastTick = true;
                    _sameLineStableTicks = 0;
                    _lastPlayedLookupKey = null; // allow one play for this page
                }

                if (_sameLineStableTicks < Config.TextStabilizeTicks) // wait more ticks for stability
                    return;

                if (currentSpeaker != null)
                {
                    string farmerName = Game1.player.Name;
                    string potentialOriginalText = currentDisplayedString;

                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

                    string sanitizedStep1 = SanitizeDialogueText(potentialOriginalText);
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                    LocalizedContentManager.LanguageCode currentLanguageCode = LocalizedContentManager.CurrentLanguageCode;
                    string gameLanguage = currentLanguageCode.ToString();
                    string characterName = currentSpeaker.Name;
                    string voicePackLanguage = GetVoicePackLanguageForCharacter(characterName);

                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        // Prevent replay on same page
                        if (string.Equals(finalLookupKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                        {
                            if (Config.developerModeOn)
                                Monitor.Log($"[VOICE] Debounced repeat key on same page: '{finalLookupKey}'", LogLevel.Trace);
                            return;
                        }

                        if (gameLanguage == voicePackLanguage)
                        {
                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[VOICE] Game and voice pack language are the same ({gameLanguage}). Using sanitized key directly.", LogLevel.Trace);
                                Monitor.Log($"Attempting voice for '{characterName}'. Lookup Key: '{finalLookupKey}' (From Displayed: '{currentDisplayedString}')", LogLevel.Debug);
                            }

                            TryToPlayVoice(characterName, finalLookupKey, currentLanguageCode);
                            _lastPlayedLookupKey = finalLookupKey; // mark as played
                        }
                        else
                        {
                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[ERROR] Null detected in GetDialogueFrom inputs:", LogLevel.Error);
                                Monitor.Log($"  Multilingual: {(Multilingual == null ? "null" : "OK")}", LogLevel.Error);
                                Monitor.Log($"  characterName: {(characterName ?? "null")}", LogLevel.Error);
                                Monitor.Log($"  gameLanguage: {(gameLanguage ?? "null")}", LogLevel.Error);
                                Monitor.Log($"  voicePackLanguage: {(voicePackLanguage ?? "null")}", LogLevel.Error);
                                Monitor.Log($"  currentDisplayedString: {(currentDisplayedString ?? "null")}", LogLevel.Error);
                            }

                            string resolvedFrom = Multilingual.GetDialogueFrom(characterName, gameLanguage, voicePackLanguage, currentDisplayedString);

                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[VOICE - MULTILINGUAL]", LogLevel.Info);
                                Monitor.Log($"Character: {characterName}", LogLevel.Info);
                                Monitor.Log($"Game Language: {gameLanguage}", LogLevel.Info);
                                Monitor.Log($"Voice Pack Language: {voicePackLanguage}", LogLevel.Info);
                                Monitor.Log($"Original Game Dialogue: \"{currentDisplayedString}\"", LogLevel.Info);
                                Monitor.Log($"Sanitized Game Dialogue: \"{Regex.Replace(SanitizeDialogueText(currentDisplayedString?.Replace(farmerName, "@")), @"#.+?#", "").Trim()}\"", LogLevel.Info);
                                if (resolvedFrom != null)
                                    Monitor.Log($"Dictionary Match Found:  DialogueFrom = \"{resolvedFrom}\"", LogLevel.Info);
                            }

                            if (!string.IsNullOrEmpty(resolvedFrom))
                            {
                                var pack = GetSelectedVoicePack(characterName);

                                string finalKey = resolvedFrom;

                                if (Config.developerModeOn)
                                    Monitor.Log($"[VOICE - MULTILINGUAL] Adjusted key with page (capped): {finalKey}", LogLevel.Debug);

                                TryToPlayVoiceFromDialogueKey(characterName, finalKey, currentLanguageCode);
                                _lastPlayedLookupKey = finalLookupKey; // mark as played
                            }
                        }
                    }
                }
            }
            // No longer reset immediately here; handled by debounce above
        }

        private void ResetDialogueState()
        {
            lastDialogueText = null;
            lastSpeakerName = null;
            wasDialogueUpLastTick = false;

            // reset debounce/stability guards
            _dialogueNotVisibleTicks = 0;
            _sameLineStableTicks = 0;
            _lastPlayedLookupKey = null;

            if (currentVoiceInstance != null && !currentVoiceInstance.IsDisposed && currentVoiceInstance.State == SoundState.Playing)
            {
                try { currentVoiceInstance.Stop(true); }
                catch (Exception ex) { Monitor.Log($"Error stopping voice instance during dialogue reset: {ex.Message}", LogLevel.Warn); }
            }

            CurrentDialogueSpeaker = null;
            CurrentDialogueOriginalKey = null;
            IsMultiPageDialogueActive = false;
        }
    }
}
