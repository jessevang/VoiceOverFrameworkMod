using System;
using System.Collections.Generic; // Needed for List
using System.Diagnostics; // Optional: For logging timing
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Utilities; // For PathUtilities
using StardewValley; // For Game1 access

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // The currently playing voice instance (or the last one started)
        private SoundEffectInstance currentVoiceInstance;
        // List to track instances that need disposal check after they stop
        private readonly List<SoundEffectInstance> activeVoiceInstances = new();
        // Lock object for safe modification of the list (optional if only accessed from main thread)
        private readonly object listLock = new object();


        // Finds the relative audio path for a given character/text based on config.
        // Returns the *absolute* path if found, otherwise null.
        private string GetAudioPathToPlay(string characterName, string sanitizedDialogueText)
        {
            if (Config == null || SelectedVoicePacks == null) return null; // Config not loaded

            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks) || !availablePacks.Any())
            {
                // Monitor.Log($"[GetAudioPath] No loaded voice packs found for character '{characterName}'.", LogLevel.Trace);
                return null; // No packs loaded for this character
            }

            // Determine the desired VoicePackId from config
            if (!SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) || string.IsNullOrEmpty(selectedVoicePackId) || selectedVoicePackId.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                // Monitor.Log($"[GetAudioPath] No voice pack selected (or set to 'None') for '{characterName}' in config.", LogLevel.Trace);
                return null; // No pack selected or explicitly disabled
            }

            // Monitor.Log($"[GetAudioPath] Trying to use configured VoicePackId '{selectedVoicePackId}' for '{characterName}'.", LogLevel.Trace);


            // Determine target language(s)
            string targetLanguage = Config.DefaultLanguage ?? "en"; // Use configured default
            string fallbackLanguage = "en"; // Hardcoded fallback
            bool tryFallback = Config.FallbackToDefaultIfMissing && !targetLanguage.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase);

            // 1. Try finding the selected pack in the target language
            VoicePack packToUse = availablePacks.FirstOrDefault(p =>
                p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                p.Language.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase));

            bool usedFallbackLanguage = false;

            // 2. Try finding the selected pack in the fallback language (if applicable)
            if (packToUse == null && tryFallback)
            {
                // Monitor.Log($"[GetAudioPath] Pack '{selectedVoicePackId}' not found for primary language '{targetLanguage}', trying fallback '{fallbackLanguage}'.", LogLevel.Trace);
                packToUse = availablePacks.FirstOrDefault(p =>
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase));
                if (packToUse != null) usedFallbackLanguage = true;
            }

            if (packToUse == null)
            {
                // Monitor.Log($"[GetAudioPath] Failed to find a loaded voice pack matching ID='{selectedVoicePackId}' for character '{characterName}' (Target Lang='{targetLanguage}', Fallback Tried: {tryFallback}).", LogLevel.Warn);
                return null;
            }

            // Monitor.Log($"[GetAudioPath] Using pack: '{packToUse.VoicePackName}' (ID: {packToUse.VoicePackId}, Lang: {packToUse.Language}, Fallback Used: {usedFallbackLanguage})", LogLevel.Trace);


            // *** THE KEY LOOKUP ***
            if (packToUse.Entries.TryGetValue(sanitizedDialogueText, out string relativeAudioPath))
            {
                // Monitor.Log($"[GetAudioPath] SUCCESS: Found relative path '{relativeAudioPath}' for text '{sanitizedDialogueText}' in pack '{packToUse.VoicePackName}'.", LogLevel.Debug);
                // Return the ABSOLUTE path by combining BaseAssetPath and relative path
                return PathUtilities.NormalizePath(Path.Combine(packToUse.BaseAssetPath, relativeAudioPath));
            }
            else
            {
                // Monitor.Log($"[GetAudioPath] FAILED: Sanitized text '{sanitizedDialogueText}' not found within the 'Entries' of selected pack '{packToUse.VoicePackName}' (Lang: '{packToUse.Language}').", LogLevel.Trace);
                return null;
            }
        }


        // Takes the character and *sanitized* text, finds the path, and plays it.
        public void TryToPlayVoice(string characterName, string sanitizedDialogueText)
        {
            // Monitor.Log($"[TryPlayVoice] Attempting lookup: Char='{characterName}', SanitizedText='{sanitizedDialogueText}'", LogLevel.Trace);

            string fullAudioPath = GetAudioPathToPlay(characterName, sanitizedDialogueText);

            if (!string.IsNullOrWhiteSpace(fullAudioPath))
            {
                // Monitor.Log($"[TryPlayVoice] Full path resolved: '{fullAudioPath}'. Calling PlayVoiceFromFile.", LogLevel.Debug);
                PlayVoiceFromFile(fullAudioPath);
            }
            else
            {
                // Monitor.Log($"[TryPlayVoice] No audio path found for Char='{characterName}', SanitizedText='{sanitizedDialogueText}'. Playback aborted.", LogLevel.Trace);
            }
        }


        // Loads and plays an audio file from the specified absolute path.
        private void PlayVoiceFromFile(string absoluteAudioFilePath)
        {
            if (string.IsNullOrWhiteSpace(absoluteAudioFilePath))
            {
                Monitor.Log($"[PlayVoiceFromFile] Attempted to play null or empty audio file path. Aborting.", LogLevel.Warn);
                return;
            }

            Monitor.Log($"[PlayVoiceFromFile] Request received for: '{absoluteAudioFilePath}'", LogLevel.Debug);

            try
            {
                if (!File.Exists(absoluteAudioFilePath))
                {
                    Monitor.Log($"[PlayVoiceFromFile] ERROR: File not found at path: {absoluteAudioFilePath}", LogLevel.Error);
                    return;
                }
                // Monitor.Log($"[PlayVoiceFromFile] File exists. Proceeding with load for: {absoluteAudioFilePath}", LogLevel.Trace);


                // --- Stop and Dispose Previous Instance ---
                SoundEffectInstance previousInstance = currentVoiceInstance; // Hold reference temporarily
                currentVoiceInstance = null; // Clear the main reference immediately

                if (previousInstance != null)
                {
                    // Monitor.Log($"[PlayVoiceFromFile] Checking previous instance. State: {previousInstance.State}, IsDisposed: {previousInstance.IsDisposed}", LogLevel.Trace);
                    // Also remove the previous instance from the tracking list
                    lock (listLock)
                    {
                        activeVoiceInstances.Remove(previousInstance);
                    }

                    if (!previousInstance.IsDisposed)
                    {
                        try
                        {
                            previousInstance.Stop(true); // Immediate stop
                            previousInstance.Dispose();
                            // Monitor.Log($"[PlayVoiceFromFile] Stopped and disposed previous instance.", LogLevel.Trace);
                        }
                        catch (ObjectDisposedException) { /* Ignore */ }
                        catch (Exception ex) { Monitor.Log($"[PlayVoiceFromFile] Error stopping/disposing previous instance: {ex.Message}", LogLevel.Warn); }
                    }
                }


                // --- Load and Play New Sound ---
                SoundEffect sound;
                // Monitor.Log($"[PlayVoiceFromFile] Creating FileStream for: {absoluteAudioFilePath}", LogLevel.Trace);
                using (var stream = new FileStream(absoluteAudioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Monitor.Log($"[PlayVoiceFromFile] FileStream opened. Calling SoundEffect.FromStream...", LogLevel.Trace);
                    sound = SoundEffect.FromStream(stream);
                    // Monitor.Log($"[PlayVoiceFromFile] SoundEffect created from stream.", LogLevel.Trace);
                }

                // Monitor.Log($"[PlayVoiceFromFile] Creating SoundEffectInstance...", LogLevel.Trace);
                var newInstance = sound.CreateInstance();
                // Monitor.Log($"[PlayVoiceFromFile] SoundEffectInstance created.", LogLevel.Trace);

                // Apply volume
                float gameVolume = Game1.options.soundVolumeLevel;
                float masterVolume = Config?.MasterVolume ?? 1.0f;
                newInstance.Volume = Math.Clamp(gameVolume * masterVolume, 0.0f, 1.0f);
                // Monitor.Log($"[PlayVoiceFromFile] Setting volume. GameVol={gameVolume:F2}, MasterVol={masterVolume:F2}, Applied={newInstance.Volume:F2}", LogLevel.Trace);

                // Play the sound
                // Monitor.Log($"[PlayVoiceFromFile] Calling Play() for {Path.GetFileName(absoluteAudioFilePath)}...", LogLevel.Debug);
                newInstance.Play();
                Monitor.Log($"[PlayVoiceFromFile] Play() called successfully for '{Path.GetFileName(absoluteAudioFilePath)}'.", LogLevel.Debug);

                // Assign to currentVoiceInstance AFTER successful play
                currentVoiceInstance = newInstance;

                // ADD the new instance to the tracking list for cleanup later
                lock (listLock)
                {
                    activeVoiceInstances.Add(currentVoiceInstance);
                    // Monitor.Log($"[PlayVoiceFromFile] Added new instance to active tracking list (Count: {activeVoiceInstances.Count}).", LogLevel.Trace);
                }
            }
            // Specific exceptions first
            catch (NoAudioHardwareException) { Monitor.LogOnce("[PlayVoiceFromFile] No audio hardware detected.", LogLevel.Warn); }
            catch (FileNotFoundException) { Monitor.Log($"[PlayVoiceFromFile] ERROR: File not found (FileNotFoundException caught) at path: {absoluteAudioFilePath}", LogLevel.Error); }
            catch (DirectoryNotFoundException) { Monitor.Log($"[PlayVoiceFromFile] ERROR: Directory not found for path: {absoluteAudioFilePath}", LogLevel.Error); }
            catch (IOException ioEx) { Monitor.Log($"[PlayVoiceFromFile] ERROR (IOException, e.g., file access): {absoluteAudioFilePath}. Message: {ioEx.Message}", LogLevel.Error); Monitor.Log(ioEx.ToString(), LogLevel.Trace); }
            catch (InvalidOperationException opEx) { Monitor.Log($"[PlayVoiceFromFile] ERROR (InvalidOperationException - often bad WAV/XNA issue): {absoluteAudioFilePath}. Message: {opEx.Message}", LogLevel.Error); Monitor.Log(opEx.ToString(), LogLevel.Trace); }
            catch (Exception ex) // Catch-all
            {
                Monitor.Log($"[PlayVoiceFromFile] FAILED ({ex.GetType().Name}): {absoluteAudioFilePath}. Message: {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
        }


        // Called periodically (e.g., from UpdateTicked) to dispose instances that have finished playing.
        internal void CleanupStoppedVoiceInstances()
        {
            if (!activeVoiceInstances.Any()) return; // Quick exit if nothing to check

            // Stopwatch sw = Stopwatch.StartNew(); // Optional: Timing
            int disposedCount = 0;

            lock (listLock)
            {
                // Iterate backwards to safely remove items while iterating
                for (int i = activeVoiceInstances.Count - 1; i >= 0; i--)
                {
                    var instance = activeVoiceInstances[i];

                    if (instance != null && !instance.IsDisposed && instance.State == SoundState.Stopped)
                    {
                        // Monitor.Log($"[Cleanup] Found stopped instance (Index: {i}). Disposing.", LogLevel.Trace);
                        try
                        {
                            instance.Dispose();
                            disposedCount++;
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"[Cleanup] Error disposing instance: {ex.Message}", LogLevel.Warn);
                        }
                        activeVoiceInstances.RemoveAt(i); // Remove from list
                    }
                    else if (instance == null || instance.IsDisposed)
                    {
                        // Clean up entries that somehow became null or were disposed elsewhere
                        // Monitor.Log($"[Cleanup] Removing null or already disposed instance entry (Index: {i}).", LogLevel.Trace);
                        activeVoiceInstances.RemoveAt(i);
                    }
                }
            }

            // sw.Stop(); // Optional: Timing
            // if (disposedCount > 0) Monitor.Log($"[Cleanup] Disposed {disposedCount} instances. Time: {sw.ElapsedMilliseconds}ms. Remaining active: {activeVoiceInstances.Count}", LogLevel.Trace);
        }

        // Marked as deprecated, recommend using TryToPlayVoice flow instead.
        public string DEPRECATED_GetAudioPath_ResolvesOnlyRelative(string characterName, string dialogueText, string languageCode, string desiredVoicePackId = null)
        {
            this.Monitor.Log("DEPRECATED_GetAudioPath_ResolvesOnlyRelative called. Consider using TryToPlayVoice.", LogLevel.Warn);
            // Original logic is now part of GetAudioPathToPlay. This just simulates a call.
            return GetAudioPathToPlay(characterName, SanitizeDialogueText(dialogueText));
        }

    }
}