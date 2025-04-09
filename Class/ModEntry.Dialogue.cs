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
            // --- Call Cleanup First ---
            // Check and dispose of any SoundEffectInstances that finished playing.
            // Run less often if performance is a concern, but every few ticks is usually fine.
            if (e.IsMultipleOf(2)) // Check every other tick
            {
                CleanupStoppedVoiceInstances(); // This method is defined in ModEntry.Playback.cs
            }

            // --- Then Check for New Dialogue ---
            CheckForDialogue();
        }

        // Checks the current game state for active dialogue boxes and triggers voice playback.
        private void CheckForDialogue()
        {
            // Ensure context is valid
            if (!Context.IsWorldReady || Game1.currentLocation == null || Game1.player == null)
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
                // getCurrentString() returns the currently visible page/segment AFTER @ replacement
                currentDisplayedString = dialogueBox?.getCurrentString();
            }
            // else { /* Check other menu types if needed */ }


            // --- State Change Detection ---

            // Case 1: Dialogue just appeared or the text/page changed
            // Use the DISPLAYED string for change detection ONLY
            if (!string.IsNullOrWhiteSpace(currentDisplayedString) && currentDisplayedString != lastDialogueText)
            {
                // Monitor.Log($"Dialogue changed/appeared. Speaker: '{currentSpeaker?.Name ?? "None"}'. Displayed Text: '{currentDisplayedString}'", LogLevel.Trace);
                lastDialogueText = currentDisplayedString; // Store the *displayed* text to detect next change
                lastSpeakerName = currentSpeaker?.Name;
                wasDialogueUpLastTick = true;

                if (currentSpeaker != null)
                {
                    // --- Construct the Lookup Key ---

                    // Get the current farmer's name
                    string farmerName = Game1.player.Name;

                    // Step 1: Reverse the farmer name substitution to recreate the original '@' format
                    // IMPORTANT: Only do this if the farmer's name is actually present!
                    string potentialOriginalText = currentDisplayedString;
                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                    {
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");
                        // Optional: Add logging here if you want to see the reversal in action
                        // Monitor.Log($"Reversed farmer name. Key candidate: '{potentialOriginalText}'", LogLevel.Trace);
                    }
                    // Now 'potentialOriginalText' should resemble the text with '@' IF the name was present.
                    // If the name wasn't present, it remains unchanged.

                    // Step 2: Apply sanitization pipeline to the RECONSTRUCTED text
                    // Use the text potentially containing '@' for sanitization matching your audio file keys/naming convention.
                    string sanitizedStep1 = SanitizeDialogueText(potentialOriginalText); // Apply main sanitizer
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim(); // Apply #tag# removal

                    // Use the FINAL reconstructed and cleaned key for lookup
                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        if (Config.developerModeOn)
                        {
                            // Log the key being used for lookup
                            Monitor.Log($"Attempting voice for '{currentSpeaker.Name}'. Lookup Key: '{finalLookupKey}' (Derived from Displayed: '{currentDisplayedString}')", LogLevel.Debug);
                        }

                        // Pass the RECONSTRUCTED and sanitized key to the playback logic
                        TryToPlayVoice(currentSpeaker.Name, finalLookupKey);
                    }
                    else
                    {
                        // Monitor.Log($"Dialogue for '{currentSpeaker.Name}' resulted in empty lookup key after reconstruction/sanitization. Original Displayed: '{currentDisplayedString}'. Skipping.", LogLevel.Trace);
                    }
                }
                else
                {
                    // Monitor.Log($"Dialogue detected but speaker is null. Text: '{currentDisplayedString}'. Skipping.", LogLevel.Trace);
                }
            }
            // Case 2: Dialogue just closed
            else if (!isDialogueBoxVisible && wasDialogueUpLastTick)
            {
                // Monitor.Log($"Dialogue box closed.", LogLevel.Trace);
                ResetDialogueState(); // Clear state and stop sound
            }
            // Case 3: Dialogue still open, text unchanged (do nothing unless handling page turns)
        }

        // Resets dialogue tracking state and stops any currently playing voice line.
        private void ResetDialogueState()
        {
            lastDialogueText = null;
            lastSpeakerName = null;
            wasDialogueUpLastTick = false;

            // Stop the currently playing voice instance immediately if dialogue closes
            if (currentVoiceInstance != null && !currentVoiceInstance.IsDisposed && currentVoiceInstance.State == SoundState.Playing)
            {
                try
                {
                    currentVoiceInstance.Stop(true); // Immediate stop
                                                     // Monitor.Log($"Stopped voice playback as dialogue closed/reset.", LogLevel.Trace);
                                                     // Dispose might happen via cleanup, but stopping is important here.
                                                     // We don't remove from activeVoiceInstances here; cleanup handles that based on Stopped state.
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error stopping voice instance during dialogue reset: {ex.Message}", LogLevel.Warn);
                }
            }

            // Reset multi-page tracking if implemented
            CurrentDialogueSpeaker = null;
            CurrentDialogueOriginalKey = null;
            CurrentDialoguePage = 0;
            IsMultiPageDialogueActive = false;
        }
    }
}