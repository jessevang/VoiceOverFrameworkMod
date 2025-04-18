﻿using System;
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

        private string GetAudioPathToPlay(string characterName, string sanitizedDialogueText, LocalizedContentManager.LanguageCode languageCode)
        {
            if (Config == null || SelectedVoicePacks == null || VoicePacksByCharacter == null)
            {
                // Monitor?.Log potentially if Config is null but others aren't? Or just fail silently.
                return null;
            }

            // Get all loaded packs for this character
            if (!VoicePacksByCharacter.TryGetValue(characterName, out var availablePacks) || !availablePacks.Any())
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] No loaded voice packs found for character '{characterName}'.", LogLevel.Trace);
                return null; // No packs loaded for this character
            }

            // Determine the desired VoicePackId from config
            if (!SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId) || string.IsNullOrEmpty(selectedVoicePackId) || selectedVoicePackId.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] No voice pack selected (or set to 'None') for '{characterName}' in config.", LogLevel.Trace);
                return null; // No pack selected or explicitly disabled
            }

            // --- Language Selection Logic ---
            // 1. Primary target is the actual game language passed in
            string primaryLangStr = languageCode.ToString().ToLowerInvariant(); // e.g., "en", "zh"

            // 2. Secondary target is the user's configured default (might be the same as primary)
            string configDefaultLangStr = (Config.DefaultLanguage ?? "en").ToLowerInvariant();

            // 3. Final fallback is English (if configured)
            string hardcodedFallbackLangStr = "en";
            bool allowFallbackToEnglish = Config.FallbackToDefaultIfMissing;

            VoicePack packToUse = null;
            string languageUsed = ""; // Keep track of which language succeeded

            // --- Attempt 1: Find pack matching Selected ID and Primary Game Language ---
            if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Attempt 1: Searching for ID '{selectedVoicePackId}' in Primary Lang '{primaryLangStr}' for '{characterName}'.", LogLevel.Trace);
            packToUse = availablePacks.FirstOrDefault(p =>
                p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                p.Language.ToLowerInvariant().StartsWith(primaryLangStr, StringComparison.OrdinalIgnoreCase));

            if (packToUse != null) languageUsed = primaryLangStr;

            // --- Attempt 2: Find pack matching Selected ID and Configured Default Language (if different from primary) ---
            if (packToUse == null && primaryLangStr != configDefaultLangStr)
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Attempt 2: Searching for ID '{selectedVoicePackId}' in Config Default Lang '{configDefaultLangStr}' for '{characterName}'.", LogLevel.Trace);
                packToUse = availablePacks.FirstOrDefault(p =>
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.ToLowerInvariant().StartsWith(configDefaultLangStr, StringComparison.OrdinalIgnoreCase));
                if (packToUse != null) languageUsed = configDefaultLangStr;
            }

            // --- Attempt 3: Find pack matching Selected ID and Hardcoded English Fallback (if enabled and needed) ---
            // Only try English fallback if:
            // - Fallback is enabled in config
            // - We haven't found a pack yet
            // - Neither the primary language NOR the config default language was English (avoid re-checking)
            if (packToUse == null && allowFallbackToEnglish && primaryLangStr != hardcodedFallbackLangStr && configDefaultLangStr != hardcodedFallbackLangStr)
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Attempt 3: Searching for ID '{selectedVoicePackId}' in Hardcoded Fallback Lang '{hardcodedFallbackLangStr}' for '{characterName}'.", LogLevel.Trace);
                packToUse = availablePacks.FirstOrDefault(p =>
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.ToLowerInvariant().StartsWith(hardcodedFallbackLangStr, StringComparison.OrdinalIgnoreCase));
                if (packToUse != null) languageUsed = hardcodedFallbackLangStr;
            }

            // --- Check if a pack was found ---
            if (packToUse == null)
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] FAILURE: Could not find suitable pack for ID='{selectedVoicePackId}', Char='{characterName}'. Tried Langs: Primary='{primaryLangStr}', ConfigDefault='{configDefaultLangStr}', EnglishFallbackEnabled='{allowFallbackToEnglish}'.", LogLevel.Warn);
                return null;
            }

            if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Using pack: '{packToUse.VoicePackName}' (ID: {packToUse.VoicePackId}, Lang: {packToUse.Language}) - Found using language '{languageUsed}'.", LogLevel.Debug);

            // --- THE KEY LOOKUP (using the found pack) ---
            if (packToUse.Entries != null && packToUse.Entries.TryGetValue(sanitizedDialogueText, out string relativeAudioPath))
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] SUCCESS: Found relative path '{relativeAudioPath}' for text '{sanitizedDialogueText}' in pack '{packToUse.VoicePackName}'.", LogLevel.Debug);

                // Ensure BaseAssetPath is valid before combining
                if (string.IsNullOrEmpty(packToUse.BaseAssetPath))
                {
                    Monitor?.Log($"[GetAudioPath] ERROR: BaseAssetPath is null or empty for pack '{packToUse.VoicePackName}'. Cannot resolve full path.", LogLevel.Error);
                    return null;
                }
                // Return the ABSOLUTE path by combining BaseAssetPath and relative path
                // Assuming BaseAssetPath is the directory containing the 'assets' folder for that pack
                return PathUtilities.NormalizePath(Path.Combine(packToUse.BaseAssetPath, relativeAudioPath));
            }
            else
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] FAILED LOOKUP: Sanitized text '{sanitizedDialogueText}' not found within the 'Entries' of selected pack '{packToUse.VoicePackName}' (Lang: '{packToUse.Language}').", LogLevel.Trace);
                // Optional: Could try a fallback lookup within the *same* pack if keys differ slightly, but stick to exact match for now.
                return null;
            }
        }




        // Takes the character and *sanitized* text, finds the path, and plays it.
        // Takes the character and *sanitized* text, finds the path, and plays it.
        public void TryToPlayVoice(string characterName, string sanitizedDialogueText, LocalizedContentManager.LanguageCode languageCode)
        {
            if (Config.developerModeOn)
            {
                Monitor.Log($"[TryPlayVoice] Attempting lookup: Lang='{languageCode}', Char='{characterName}', SanitizedText='{sanitizedDialogueText}'", LogLevel.Trace); // Added Lang to log
            }


            // *** PASS languageCode DOWN to GetAudioPathToPlay ***
            string fullAudioPath = GetAudioPathToPlay(characterName, sanitizedDialogueText, languageCode);

            if (!string.IsNullOrWhiteSpace(fullAudioPath))
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"[TryPlayVoice] Full path resolved: '{fullAudioPath}'. Calling PlayVoiceFromFile.", LogLevel.Debug);
                }

                PlayVoiceFromFile(fullAudioPath);
            }
            else
            {
                if (Config.developerModeOn)
                {
                    Monitor.Log($"[TryPlayVoice] No audio path found for Lang='{languageCode}', Char='{characterName}', SanitizedText='{sanitizedDialogueText}'. Playback aborted.", LogLevel.Trace); // Added Lang to log
                }
            }
        }


        // Loads and plays an audio file from the specified absolute path.
        private void PlayVoiceFromFile(string absoluteAudioFilePath)
        {
            if (string.IsNullOrWhiteSpace(absoluteAudioFilePath))
            {
                if (Config.developerModeOn)
                {
                Monitor.Log($"[PlayVoiceFromFile] Attempted to play null or empty audio file path. Aborting.", LogLevel.Warn);

                }
                return;
            }

            if (Config.developerModeOn)
            {
                Monitor.Log($"[PlayVoiceFromFile] Request received for: '{absoluteAudioFilePath}'", LogLevel.Debug);
            }
  

            try
            {
                if (!File.Exists(absoluteAudioFilePath))
                {
                    if (Config.developerModeOn)
                    {
                        Monitor.Log($"[PlayVoiceFromFile] ERROR: File not found at path: {absoluteAudioFilePath}", LogLevel.Error);
                    }

                    return;
                }
                if (Config.developerModeOn)
                {
                    Monitor.Log($"[PlayVoiceFromFile] File exists. Proceeding with load for: {absoluteAudioFilePath}", LogLevel.Trace);
                }



                // --- Stop and Dispose Previous Instance ---
                SoundEffectInstance previousInstance = currentVoiceInstance; // Hold reference temporarily
                currentVoiceInstance = null; // Clear the main reference immediately

                if (previousInstance != null)
                {
                  
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
                           
                        }
                        catch (ObjectDisposedException) { /* Ignore */ }
                        catch (Exception ex) { Monitor.Log($"[PlayVoiceFromFile] Error stopping/disposing previous instance: {ex.Message}", LogLevel.Warn); }
                    }
                }


                // --- Load and Play New Sound ---
                SoundEffect sound;
                if (Config.developerModeOn)
                {
                     Monitor.Log($"[PlayVoiceFromFile] Creating FileStream for: {absoluteAudioFilePath}", LogLevel.Trace);
                }
               
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

                if (Config.developerModeOn)
                {
                    Monitor.Log($"[PlayVoiceFromFile] Calling Play() for {Path.GetFileName(absoluteAudioFilePath)}...", LogLevel.Debug);
                }
               
                newInstance.Play();

                if (Config.developerModeOn)
                {
                    Monitor.Log($"[PlayVoiceFromFile] Play() called successfully for '{Path.GetFileName(absoluteAudioFilePath)}'.", LogLevel.Debug);
                }


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
                            if (Config.developerModeOn)
                            {
                                Monitor.Log($"[Cleanup] Error disposing instance: {ex.Message}", LogLevel.Warn);
                            }
                            
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



    }
}