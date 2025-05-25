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

        // Main dialogue check loop called every tick (or less often if adjusted).
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
           

            if (e.IsMultipleOf(2)) // Check every other tick
            {
                CleanupStoppedVoiceInstances();
            }

            
            CheckForDialogue();
     

        }



        // Checks the current game state for active dialogue boxes and triggers voice playback.
        private void CheckForDialogue()
        {
            
            if (Game1.currentLocation == null || Game1.player == null )//||!Context.IsWorldReady )  commentout if world is ready to see if we can test bus event.
            {
                if (lastDialogueText != null) ResetDialogueState(); // Clear state if we exit world context
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;



            string currentDisplayedString = null; // Renamed for clarity

            if (isDialogueBoxVisible)
            {
                DialogueBox dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDisplayedString = dialogueBox?.getCurrentString();

            }


            // Case 1: Dialogue just appeared or the text/page changed
            if (!string.IsNullOrWhiteSpace(currentDisplayedString)
    && (currentDisplayedString != lastDialogueText ))
            {
                lastDialogueText = currentDisplayedString;

                lastSpeakerName = currentSpeaker?.Name;
                wasDialogueUpLastTick = true;

                if (currentDisplayedString != lastDialogueText)
                {
                   // Increment only when text changes

                    if (Game1.activeClickableMenu is DialogueBox db)
                    {
                        var field = typeof(DialogueBox).GetField("dialogues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(db) is List<string> dialoguePages)
                           ;
                       
                          
                    }
                    else
                    {
                        
                    }
                }

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
                        if (gameLanguage == voicePackLanguage)
                        {
                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[VOICE] Game and voice pack language are the same ({gameLanguage}). Using sanitized key directly.", LogLevel.Trace);
                                Monitor.Log($"Attempting voice for '{characterName}'. Lookup Key: '{finalLookupKey}' (From Displayed: '{currentDisplayedString}')", LogLevel.Debug);
                            }

                            TryToPlayVoice(characterName, finalLookupKey, currentLanguageCode);
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
                            }
                        }
                    }
                }
            }


            // Case 2: Dialogue just closed
            else if (!isDialogueBoxVisible && wasDialogueUpLastTick)
            {

                ResetDialogueState(); 
            }

        }

        private void ResetDialogueState()
        {
            lastDialogueText = null;
            lastSpeakerName = null;
            wasDialogueUpLastTick = false;



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