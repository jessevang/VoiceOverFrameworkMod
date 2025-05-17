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
        private string lastSpeakerName = null;

        internal NPC CurrentDialogueSpeaker = null;
        internal string CurrentDialogueOriginalKey = null;
        internal int CurrentDialogueTotalPages { get; set; } = 1;
        internal int CurrentDialoguePage { get; set; } = 0;
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
            if (!string.IsNullOrWhiteSpace(currentDisplayedString) && currentDisplayedString != lastDialogueText)
            {

                lastDialogueText = currentDisplayedString; 
                lastSpeakerName = currentSpeaker?.Name;
                wasDialogueUpLastTick = true;

                if (currentSpeaker != null)
                {

                    string farmerName = Game1.player.Name;

                    // Step 1: Reverse the farmer name substitution to recreate the original '@' format

                    string potentialOriginalText = currentDisplayedString;
                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                    {
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

                    }


                    // Step 2: Apply sanitization pipeline to the RECONSTRUCTED text

                    string sanitizedStep1 = SanitizeDialogueText(potentialOriginalText); // Apply main sanitizer
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim(); // Apply #tag# removal

                    //get current language
                    LocalizedContentManager.LanguageCode currentLanguageCode = LocalizedContentManager.CurrentLanguageCode;
                  
                    // Use the FINAL reconstructed and cleaned key for lookup
                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        if (Config.developerModeOn)
                        {
                            // Log the key being used for lookup
                            Monitor.Log($"Attempting voice for '{currentSpeaker.Name}'. Lookup Key: '{finalLookupKey}' (Derived from Displayed: '{currentDisplayedString}')", LogLevel.Debug);
                            Monitor.Log($" [VOICE DEBUG]", LogLevel.Debug);
                            Monitor.Log($"     Current Speaker: {currentSpeaker.Name}", LogLevel.Info);
                            Monitor.Log($"     Displayed:    \"{currentDisplayedString}\"", LogLevel.Debug);
                            Monitor.Log($"     Reversed:     \"{potentialOriginalText}\"", LogLevel.Debug);
                            Monitor.Log($"     Sanitized:    \"{sanitizedStep1}\"", LogLevel.Debug);
                            Monitor.Log($"     Final Lookup: \"{finalLookupKey}\"", LogLevel.Debug);
                        }

                        // Pass the RECONSTRUCTED and sanitized key to the playback logic
                        TryToPlayVoice(currentSpeaker.Name, finalLookupKey, currentLanguageCode); // Pass the enum code
                    }
                    else
                    {
                        
                    }
                }
                else
                {
                   
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
                try
                {
                    currentVoiceInstance.Stop(true); 
                                                     
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error stopping voice instance during dialogue reset: {ex.Message}", LogLevel.Warn);
                }
            }


            CurrentDialogueSpeaker = null;
            CurrentDialogueOriginalKey = null;
            CurrentDialoguePage = 0;
            IsMultiPageDialogueActive = false;
        }
    }
}