using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Microsoft.Xna.Framework.Audio; // Needed for SoundState check in ResetDialogueState

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
            string currentDialogueString = null;

            if (isDialogueBoxVisible)
            {
                DialogueBox dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDialogueString = dialogueBox?.getCurrentString();
            }
            // else { /* Check other menu types like LetterViewerMenu if needed */ }


            // --- State Change Detection ---

            // Case 1: Dialogue just appeared or the text changed
            if (!string.IsNullOrWhiteSpace(currentDialogueString) && currentDialogueString != lastDialogueText)
            {
                // Monitor.Log($"Dialogue changed/appeared. Speaker: '{currentSpeaker?.Name ?? "None"}'. Text: '{currentDialogueString}'", LogLevel.Trace);
                lastDialogueText = currentDialogueString;
                lastSpeakerName = currentSpeaker?.Name;
                wasDialogueUpLastTick = true;

                if (currentSpeaker != null)
                {
                    // Sanitize the text *before* passing it to the playback logic
                    string sanitizedText = SanitizeDialogueText(currentDialogueString); // Use the utility method

                    if (!string.IsNullOrWhiteSpace(sanitizedText))
                    {
                        Monitor.Log($"Attempting voice for '{currentSpeaker.Name}'. Sanitized: '{sanitizedText}'", LogLevel.Debug);
                        TryToPlayVoice(currentSpeaker.Name, sanitizedText); // Call playback logic (in ModEntry.Playback.cs)
                    }
                    else
                    {
                        // Monitor.Log($"Dialogue for '{currentSpeaker.Name}' sanitized to empty. Original: '{currentDialogueString}'. Skipping.", LogLevel.Trace);
                    }
                }
                else
                {
                    // Monitor.Log($"Dialogue detected but speaker is null. Text: '{currentDialogueString}'. Skipping.", LogLevel.Trace);
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