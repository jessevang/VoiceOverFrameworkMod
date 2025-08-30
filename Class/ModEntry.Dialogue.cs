using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using System.Reflection;
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

        // --- V2 event serial state (per-event, per-speaker, increments once per displayed page) ---
        private string _v2_lastEventBase = null;
        private string _v2_lastEventPageFingerprint = null; // eventBase|speaker|sanitizedText
        private readonly Dictionary<string, int> _v2_eventSerialBySpeaker = new(StringComparer.OrdinalIgnoreCase);




        // Chooses the right sanitizer based on the loaded pack's FormatMajor
        private string SanitizeForPack(string raw, VoicePack pack)
            => (pack?.FormatMajor ?? 1) >= 2 ? SanitizeDialogueTextV2(raw) : SanitizeDialogueText(raw);







        // Main dialogue check loop called every tick (or less often if adjusted).
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (e.IsMultipleOf(2))
                CleanupStoppedVoiceInstances();

            var speaker = Game1.currentSpeaker;
            if (speaker != null)
            {
                var pack = GetSelectedVoicePack(speaker.Name);
                if (Config.developerModeOn)
                    Monitor.Log($"[Tick] Speaker={speaker.Name} Pack={(pack?.VoicePackName ?? "null")} FormatMajor={(pack?.FormatMajor ?? -1)}", LogLevel.Trace);

                if (pack != null && pack.FormatMajor >= 2)
                {
                    if (Config.developerModeOn)
                        Monitor.Log("[Tick] Using V2 pipeline", LogLevel.Trace);

                    CheckForDialogueV2();
                    return;
                }
            }

            if (Config.developerModeOn)
                Monitor.Log("[Tick] Using V1 pipeline", LogLevel.Trace);

            CheckForDialogue();
        }





        // V2-only dialogue checker that reuses V1 state fields (lastDialogueText, wasDialogueUpLastTick, etc.)
        private void CheckForDialogueV2()
        {
            // Do nothing if no location/player
            if (Game1.currentLocation == null || Game1.player == null)
            {
                if (lastDialogueText != null) ResetDialogueState(); // reuse V1 reset
                                                                    // also clear event-serial state
                _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
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
                    // clear event-serial state
                    _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
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
                        // clear event-serial state
                        _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
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
            bool newPageDetectedThisTick = false;
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
                    newPageDetectedThisTick = true;
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

                    // --- V2 sanitizer for the fallback text key ---
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

                        // Build context for TK candidates & fallback
                        var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                        var d = dialogueBox?.characterDialogue;

                        // Prefer the vanilla translation key (often "Characters/Dialogue/NPC:key"), fallback to temp if present
                        var tk = d?.TranslationKey ?? d?.temporaryDialogueKey;
                        string sourceKey = (tk != null && tk.StartsWith("Characters/Dialogue/", StringComparison.OrdinalIgnoreCase)) ? tk : null;

                        // Festival key if the TK points at 1.6 strings (or other Strings/* we decided to key)
                        string festKey = (tk != null && tk.StartsWith("Strings/1_6_Strings", StringComparison.OrdinalIgnoreCase)) ? tk : null;

                        // Event key (base) if we’re in an event
                        var ev = GetCurrentEvent();
                        string eventBase = ev != null && !IsFestivalEvent(ev) ? BuildEventBaseKeyForCurrent(ev) : null;

                        // Page (Dialogue.currentDialogueIndex is 0-based)
                        int pageZero = Math.Max(0, (d?.currentDialogueIndex ?? 0));

                        // --- Maintain per-event, per-speaker serial that increments once per displayed page ---
                        int? eventSerial = null;
                        if (!string.IsNullOrWhiteSpace(eventBase))
                        {
                            // reset serial map when event changes
                            if (!string.Equals(_v2_lastEventBase, eventBase, StringComparison.Ordinal))
                            {
                                _v2_eventSerialBySpeaker.Clear();
                                _v2_lastEventBase = eventBase;
                                _v2_lastEventPageFingerprint = null;
                            }

                            string pageFingerprint = $"{eventBase}|{characterName}|{finalLookupKey}";
                            if (!string.Equals(_v2_lastEventPageFingerprint, pageFingerprint, StringComparison.Ordinal))
                            {
                                // first time we see this specific page → bump serial for this speaker
                                string k = characterName;
                                if (!_v2_eventSerialBySpeaker.TryGetValue(k, out int cur))
                                    cur = -1;
                                cur++;
                                _v2_eventSerialBySpeaker[k] = cur;
                                _v2_lastEventPageFingerprint = pageFingerprint;
                            }

                            eventSerial = _v2_eventSerialBySpeaker[characterName];
                        }
                        else
                        {
                            // leaving event scope → clear serial bookkeeping
                            if (_v2_eventSerialBySpeaker.Count > 0)
                            {
                                _v2_eventSerialBySpeaker.Clear();
                                _v2_lastEventBase = null;
                                _v2_lastEventPageFingerprint = null;
                            }
                        }

                        // --- Compose TK candidates (Events: prefer game's exact key; then our :sN; no :pN) ---
                        var tkCandidates = new List<string>(8);

                        // 0) If the game provided an Events/* key (temporaryDialogueKey), try it *first*
                        string eventTk = (tk != null && tk.StartsWith("Events/", StringComparison.OrdinalIgnoreCase)) ? tk : null;
                        if (!string.IsNullOrWhiteSpace(eventTk))
                        {
                            tkCandidates.Add(eventTk);

                            // also try without any trailing ":pN" just in case (normalization)
                            string noPage = Regex.Replace(eventTk, @":p\d+\b", "", RegexOptions.CultureInvariant);
                            if (!string.Equals(noPage, eventTk, StringComparison.Ordinal))
                                tkCandidates.Add(noPage);
                        }

                        // Characters/Dialogue TK
                        if (!string.IsNullOrWhiteSpace(sourceKey))
                        {
                            tkCandidates.Add($"{sourceKey}:p{pageZero}");
                            tkCandidates.Add(sourceKey);
                        }

                        // Event TK via our running serial (fallback path)
                        if (!string.IsNullOrWhiteSpace(eventBase) && eventSerial.HasValue)
                        {
                            tkCandidates.Add($"{eventBase}:s{eventSerial.Value}");
                            // Also try raw eventBase (ultra-fallback)
                            tkCandidates.Add(eventBase);
                        }

                        // Festival / Strings TK
                        if (!string.IsNullOrWhiteSpace(festKey))
                        {
                            tkCandidates.Add($"{festKey}:p{pageZero}");
                            tkCandidates.Add(festKey);
                        }

                        // --- Fast path: try selected pack's TK map directly ---
                        if (gameLanguage == voicePackLanguage && selectedPack != null && selectedPack.FormatMajor >= 2)
                        {
                            // log lookup intent (show the first/primary candidate)
                            if (Config.developerModeOn)
                            {
                                string primaryKey = tkCandidates.FirstOrDefault() ?? "(none)";
                                Monitor.Log($"[V2-LOOKUP] char='{characterName}' selPack='{selectedPack.VoicePackId}' text='{currentDisplayedString}' | key='{primaryKey}'", LogLevel.Info);
                            }

                            string resolvedRel = null;
                            foreach (var cand in tkCandidates)
                            {
                                if (selectedPack.EntriesByTranslationKey.TryGetValue(cand, out resolvedRel))
                                {
                                    string fullPath = PathUtilities.NormalizePath(Path.Combine(selectedPack.BaseAssetPath, resolvedRel));

                                    if (Config.developerModeOn)
                                        Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{selectedPack.VoicePackName}' format={selectedPack.FormatMajor} match=Yes key='{cand}' path='{fullPath}'", LogLevel.Info);

                                    PlayVoiceFromFile(fullPath);
                                    _lastPlayedLookupKey = finalLookupKey; // mark as played for debounce
                                    return;
                                }
                            }

                            if (Config.developerModeOn)
                                Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{selectedPack.VoicePackName}' format={selectedPack.FormatMajor} match=No → fallback to V2/V1 text", LogLevel.Info);
                        }

                        // --- Build V2 context then try full V2 fallback (templated keys), then V1 text ---
                        var ctx = new VoiceLineContext
                        {
                            Speaker = characterName,
                            SourceKey = sourceKey,     // e.g. Characters/Dialogue/Abigail:Mon
                            FestivalKey = festKey,     // e.g. Strings/1_6_Strings:DesertFestival_Abigail
                            EventKey = eventBase,      // e.g. Events/SeedShop:1
                                                       // IMPORTANT: do NOT set Page for Events; keep it only for Characters/Dialogue and Strings
                            Page = string.IsNullOrWhiteSpace(eventBase) ? (pageZero + 1) : (int?)null,
                            Gender = Game1.player?.IsMale == true ? "M" : "F",
                        };

                        TryToPlayVoiceV2(characterName, ctx, currentLanguageCode, finalLookupKey);
                        _lastPlayedLookupKey = finalLookupKey; // mark as played for debounce
                    }
                }
            }
            // no immediate reset here; handled by debounce above
        }





        /// <summary>
        /// Try to play by raw TranslationKey(s) (e.g., "Characters/Dialogue/Abigail:Mon", "Events/SeedShop:1:s0").
        /// Returns true if playback started. Falls back to V1 if provided.
        /// </summary>
        public bool TryToPlayVoiceByTranslationKeys(
            string characterName,
            IEnumerable<string> tkCandidates,
            LocalizedContentManager.LanguageCode languageCode,
            string? sanitizedDialogueTextForV1Fallback = null)
        {
            var candidates = tkCandidates?.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            if (candidates.Count == 0)
                return false;

            // Resolve V2-capable pack for this character
            var fullPath = GetAudioPathByTranslationKeys(characterName, candidates, languageCode, out VoicePack? packUsed, out string? matchedKey);

            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                if (Config?.developerModeOn == true)
                    Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{packUsed?.VoicePackName}' format={(packUsed?.FormatMajor.ToString() ?? "?")} match=Yes key='{matchedKey}' path='{fullPath}'", LogLevel.Info);

                PlayVoiceFromFile(fullPath!);
                return true;
            }

            if (Config?.developerModeOn == true)
                Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{packUsed?.VoicePackName ?? "(none)"}' format={(packUsed?.FormatMajor.ToString() ?? "?")} match=No → fallback to V1 text", LogLevel.Info);

            // Optional fallback to V1 sanitized text
            if (!string.IsNullOrWhiteSpace(sanitizedDialogueTextForV1Fallback))
                TryToPlayVoice(characterName, sanitizedDialogueTextForV1Fallback!, languageCode);

            return false;
        }

        /// <summary>
        /// Resolve a V2 pack and try raw TK candidates against EntriesByTranslationKey. Returns absolute path if found.
        /// </summary>
        private string? GetAudioPathByTranslationKeys(
            string characterName,
            IEnumerable<string> tkCandidates,
            LocalizedContentManager.LanguageCode languageCode,
            out VoicePack? packUsed,
            out string? matchedKey)
        {
            packUsed = null;
            matchedKey = null;

            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks) || !availablePacks.Any())
                return null;

            if (!SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) ||
                string.IsNullOrEmpty(selectedVoicePackId) ||
                selectedVoicePackId.Equals("None", StringComparison.OrdinalIgnoreCase))
                return null;

            // language selection (same as V1)
            string primaryLangStr = languageCode.ToString().ToLowerInvariant();
            string configDefaultLangStr = (Config?.DefaultLanguage ?? "en").ToLowerInvariant();
            const string hardcodedFallbackLangStr = "en";
            bool allowFallbackToEnglish = Config?.FallbackToDefaultIfMissing ?? true;

            // Pick a V2-capable pack
            VoicePack? pack = availablePacks.FirstOrDefault(p =>
                p.FormatMajor >= 2 &&
                p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                p.Language.StartsWith(primaryLangStr, StringComparison.OrdinalIgnoreCase));

            if (pack == null && primaryLangStr != configDefaultLangStr)
            {
                pack = availablePacks.FirstOrDefault(p =>
                    p.FormatMajor >= 2 &&
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.StartsWith(configDefaultLangStr, StringComparison.OrdinalIgnoreCase));
            }

            if (pack == null && allowFallbackToEnglish && primaryLangStr != hardcodedFallbackLangStr && configDefaultLangStr != hardcodedFallbackLangStr)
            {
                pack = availablePacks.FirstOrDefault(p =>
                    p.FormatMajor >= 2 &&
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.StartsWith(hardcodedFallbackLangStr, StringComparison.OrdinalIgnoreCase));
            }

            // No V2 or no TK map? bail
            if (pack == null || pack.EntriesByTranslationKey == null || pack.EntriesByTranslationKey.Count == 0)
                return null;

            packUsed = pack;

            // Try exact TK match
            foreach (var tk in tkCandidates)
            {
                if (pack.EntriesByTranslationKey.TryGetValue(tk, out var rel))
                {
                    matchedKey = tk;
                    return PathUtilities.NormalizePath(Path.Combine(pack.BaseAssetPath, rel));
                }
            }

            // (Optional) If your packs keep templated TKs with {Vars} (rare), you can render here.
            // Not doing that by default because TKs are usually literal.

            return null;
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


       

        private Event GetCurrentEvent()
        {
            // Try both; different SDV versions expose one or the other
            var ev = Game1.CurrentEvent;
            if (ev == null)
                ev = Game1.currentLocation?.currentEvent;
            return ev;
        }

        private bool IsFestivalEvent(Event ev)
        {
            try { return ev?.isFestival == true || (ev?.id?.StartsWith("festival_", StringComparison.OrdinalIgnoreCase) ?? false); }
            catch { return false; }
        }

        /// <summary>
        /// Build "Events/{Map}:{NumericId}" from the active event.
        /// </summary>
        private string BuildEventBaseKeyForCurrent(Event ev)
        {
            try
            {
                string map = Game1.currentLocation?.Name ?? "Unknown";
                // ev.id for regular events is like "6963327/f Abigail 3500/O Abigail/t 610 1700"
                // extract the leading number
                string id = ev?.id;
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                var m = System.Text.RegularExpressions.Regex.Match(id, @"^(?<num>\d+)");
                if (!m.Success)
                    return null;

                string num = m.Groups["num"].Value;
                return $"Events/{map}:{num}";
            }
            catch { return null; }
        }

        /// <summary>
        /// Count how many speak-ish commands for this speaker occur up to and including the current command.
        /// This reproduces the s{index} we assigned at template time without Harmony.
        /// </summary>
        private int? ComputeSpeakIndexForCurrent(Event ev, string speakerName)
        {
            if (ev?.eventCommands == null || ev.eventCommands.Length == 0 || string.IsNullOrWhiteSpace(speakerName))
                return null;

            int current = ev.CurrentCommand; // public property
            if (current < 0) current = 0;
            if (current >= ev.eventCommands.Length) current = ev.eventCommands.Length - 1;

            int count = 0;
            for (int i = 0; i <= current; i++)
            {
                string line = ev.eventCommands[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // commands look like: "speak Abigail \"...\"" or "splitSpeak Abigail \"a~b\""
                bool isSpeak = line.StartsWith("speak ", StringComparison.OrdinalIgnoreCase);
                bool isSplit = line.StartsWith("splitSpeak ", StringComparison.OrdinalIgnoreCase);

                if (!isSpeak && !isSplit)
                    continue;

                // extract actor token (2nd token)
                string actor = null;
                var parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    actor = parts[1].Trim().Trim('"').TrimEnd('?'); // optional-NPC markers can appear with '?' suffix
                }

                if (actor != null && actor.Equals(speakerName, StringComparison.OrdinalIgnoreCase))
                {
                    // this occurrence contributes to the index
                    // by the time the dialogue shows, ev.CurrentCommand is still on this command,
                    // so including i==current gives the correct 0-based index.
                    count++;
                }
            }

            return count > 0 ? count - 1 : (int?)0; // if the first match is the current one, its index is 0
        }

        /// <summary>
        /// Pick entry where TranslationKey == key and DisplayPattern matches; prefer matching gender.
        /// </summary>
        private VoiceEntryTemplate PickBestByTkPatternGender(List<VoiceEntryTemplate> entries, string key, string pattern, string preferredGender)
        {
            return entries
                .Where(e =>
                    string.Equals(e.TranslationKey, key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.DisplayPattern, pattern, StringComparison.Ordinal))
                .OrderByDescending(e => RankGender(e.GenderVariant, preferredGender))
                .FirstOrDefault();
        }

        /// <summary>
        /// Pick entry where TranslationKey starts with prefix (for Events ...:sN[:splitK]) and DisplayPattern matches; prefer matching gender.
        /// </summary>
        private VoiceEntryTemplate PickBestByTkPrefixPatternGender(List<VoiceEntryTemplate> entries, string prefix, string pattern, string preferredGender)
        {
            return entries
                .Where(e =>
                    !string.IsNullOrWhiteSpace(e.TranslationKey) &&
                    e.TranslationKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.DisplayPattern, pattern, StringComparison.Ordinal))
                .OrderByDescending(e => RankGender(e.GenderVariant, preferredGender))
                .FirstOrDefault();
        }

        /// <summary>
        /// Gender scoring: exact match = 2, null/neutral = 1, mismatch = 0
        /// </summary>
        private int RankGender(string candidate, string preferred)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return 1;
            if (preferred != null && candidate.Equals(preferred, StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }



        // Events
        





    }
}
