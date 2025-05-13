using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

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
            if (packToUse == null && allowFallbackToEnglish && primaryLangStr != hardcodedFallbackLangStr && configDefaultLangStr != hardcodedFallbackLangStr)
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Attempt 3: Searching for ID '{selectedVoicePackId}' in Hardcoded Fallback Lang '{hardcodedFallbackLangStr}' for '{characterName}'.", LogLevel.Trace);
                packToUse = availablePacks.FirstOrDefault(p =>
                    p.VoicePackId.Equals(selectedVoicePackId, StringComparison.OrdinalIgnoreCase) &&
                    p.Language.ToLowerInvariant().StartsWith(hardcodedFallbackLangStr, StringComparison.OrdinalIgnoreCase));
                if (packToUse != null) languageUsed = hardcodedFallbackLangStr;
            }


            if (packToUse == null)
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] FAILURE: Could not find suitable pack for ID='{selectedVoicePackId}', Char='{characterName}'. Tried Langs: Primary='{primaryLangStr}', ConfigDefault='{configDefaultLangStr}', EnglishFallbackEnabled='{allowFallbackToEnglish}'.", LogLevel.Warn);
                return null;
            }

            if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] Using pack: '{packToUse.VoicePackName}' (ID: {packToUse.VoicePackId}, Lang: {packToUse.Language}) - Found using language '{languageUsed}'.", LogLevel.Debug);


            //Extra logging to determine if dialogue is found in voice pack file, or partial match
            if (Config.developerModeOn)
            {
                bool exactMatch = packToUse.Entries.ContainsKey(sanitizedDialogueText);

                string fuzzyMatchKey = packToUse.Entries.Keys
                    .FirstOrDefault(k => SanitizeDialogueText(k).Equals(sanitizedDialogueText, StringComparison.OrdinalIgnoreCase));

                var partialMatches = packToUse.Entries.Keys
                    .Where(k =>
                        SanitizeDialogueText(k).Contains(sanitizedDialogueText, StringComparison.OrdinalIgnoreCase) ||
                        sanitizedDialogueText.Contains(SanitizeDialogueText(k), StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                Monitor.Log($"[DEV CHECK]  Voice pack: '{packToUse.VoicePackName}' ({packToUse.Entries.Count} entries)", LogLevel.Debug);
                Monitor.Log($"[DEV CHECK]  Sanitized target: \"{sanitizedDialogueText}\"", LogLevel.Debug);

                if (exactMatch)
                {
                    Monitor.Log($"[DEV CHECK]  Exact match found in Entries.", LogLevel.Debug);
                }
                else if (fuzzyMatchKey != null)
                {
                    Monitor.Log($"[DEV CHECK]  No exact match, but found fuzzy match key:", LogLevel.Debug);
                    Monitor.Log($"             \"{fuzzyMatchKey}\" (sanitized as \"{SanitizeDialogueText(fuzzyMatchKey)}\")", LogLevel.Debug);
                }
                else
                {
                    Monitor.Log($"[DEV CHECK]  No exact or sanitized match found.", LogLevel.Warn);
                }

                if (partialMatches.Count > 0)
                {
                    Monitor.Log($"[DEV CHECK]  Partial matches (up to 5):", LogLevel.Debug);
                    foreach (var match in partialMatches)
                    {
                        Monitor.Log($"             \"{match}\" → Sanitized: \"{SanitizeDialogueText(match)}\"", LogLevel.Debug);
                    }
                }
            }




            if (packToUse.Entries != null && packToUse.Entries.TryGetValue(sanitizedDialogueText, out string relativeAudioPath))
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] SUCCESS: Found relative path '{relativeAudioPath}' for text '{sanitizedDialogueText}' in pack '{packToUse.VoicePackName}'.", LogLevel.Debug);


                if (string.IsNullOrEmpty(packToUse.BaseAssetPath))
                {
                    Monitor?.Log($"[GetAudioPath] ERROR: BaseAssetPath is null or empty for pack '{packToUse.VoicePackName}'. Cannot resolve full path.", LogLevel.Error);
                    return null;
                }
 
                return PathUtilities.NormalizePath(Path.Combine(packToUse.BaseAssetPath, relativeAudioPath));
            }
            else
            {
                if (Config.developerModeOn) Monitor?.Log($"[GetAudioPath] FAILED LOOKUP: Sanitized text '{sanitizedDialogueText}' not found within the 'Entries' of selected pack '{packToUse.VoicePackName}' (Lang: '{packToUse.Language}').", LogLevel.Trace);
                return null;
            }
        }




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


        // Loads and plays an audio file from the specified absolute path. Updated to Support wav and ogg
        private void PlayVoiceFromFile(string absoluteAudioFilePath)
        {
            if (string.IsNullOrWhiteSpace(absoluteAudioFilePath))
            {
                Monitor?.Log($"[PlayVoiceFromFile] Attempted to play null or empty audio file path. Aborting.", LogLevel.Warn);
                return;
            }

            try
            {
                if (!File.Exists(absoluteAudioFilePath))
                {
                    Monitor?.Log($"[PlayVoiceFromFile] ERROR: File not found at path: {absoluteAudioFilePath}", LogLevel.Error);
                    return;
                }

                // Stop and dispose previous instance
                SoundEffectInstance previousInstance = currentVoiceInstance;
                currentVoiceInstance = null;

                if (previousInstance != null)
                {
                    lock (listLock)
                    {
                        activeVoiceInstances.Remove(previousInstance);
                    }
                    if (!previousInstance.IsDisposed)
                    {
                        try
                        {
                            previousInstance.Stop(true);
                            previousInstance.Dispose();
                        }
                        catch (ObjectDisposedException) { }
                        catch (Exception ex) { Monitor.Log($"[PlayVoiceFromFile] Error disposing previous instance: {ex.Message}", LogLevel.Warn); }
                    }
                }

                string extension = Path.GetExtension(absoluteAudioFilePath).ToLowerInvariant();
                SoundEffect sound = null;

                if (extension == ".wav")
                {
                    // Normal WAV loading
                    using (var stream = new FileStream(absoluteAudioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        sound = SoundEffect.FromStream(stream);
                    }
                }
                else if (extension == ".ogg")
                {
                    // OGG loading
                    sound = LoadOggSoundEffect(absoluteAudioFilePath);
                }
                else
                {
                    Monitor.Log($"[PlayVoiceFromFile] Unsupported audio format '{extension}'. Only '.wav' and '.ogg' are supported.", LogLevel.Error);
                    return;
                }

                if (sound == null)
                {
                    Monitor.Log($"[PlayVoiceFromFile] Failed to create SoundEffect from file '{absoluteAudioFilePath}'.", LogLevel.Error);
                    return;
                }

                var newInstance = sound.CreateInstance();

                float gameVolume = Game1.options.soundVolumeLevel;
                float masterVolume = Config?.MasterVolume ?? 1.0f;
                newInstance.Volume = Math.Clamp(gameVolume * masterVolume, 0.0f, 1.0f);

                newInstance.Play();

                currentVoiceInstance = newInstance;

                lock (listLock)
                {
                    activeVoiceInstances.Add(currentVoiceInstance);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[PlayVoiceFromFile] Exception: {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
        }


        //Used to support .ogg files
        private SoundEffect LoadOggSoundEffect(string path)
        {
            try
            {
                using var vorbis = new NVorbis.VorbisReader(path);

                int sampleRate = vorbis.SampleRate;
                int channels = vorbis.Channels;

                // Read all samples
                float[] floatBuffer = new float[vorbis.TotalSamples * channels];
                int samplesRead = vorbis.ReadSamples(floatBuffer, 0, floatBuffer.Length);

                // Convert float samples to 16-bit PCM
                byte[] byteBuffer = new byte[samplesRead * sizeof(short)];
                for (int i = 0; i < samplesRead; i++)
                {
                    short pcmSample = (short)Math.Clamp(floatBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
                    byteBuffer[i * 2] = (byte)(pcmSample & 0xFF);
                    byteBuffer[i * 2 + 1] = (byte)((pcmSample >> 8) & 0xFF);
                }

                return new SoundEffect(byteBuffer, sampleRate, (AudioChannels)channels);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[LoadOggSoundEffect] Failed to load OGG '{path}': {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
                return null;
            }
        }



        // Called periodically (e.g., from UpdateTicked) to dispose instances that have finished playing.
        internal void CleanupStoppedVoiceInstances()
        {
            if (!activeVoiceInstances.Any()) return; 


            int disposedCount = 0;

            lock (listLock)
            {
              
                for (int i = activeVoiceInstances.Count - 1; i >= 0; i--)
                {
                    var instance = activeVoiceInstances[i];

                    if (instance != null && !instance.IsDisposed && instance.State == SoundState.Stopped)
                    {
                        
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
                        activeVoiceInstances.RemoveAt(i); 
                    }
                    else if (instance == null || instance.IsDisposed)
                    {

                        activeVoiceInstances.RemoveAt(i);
                    }
                }
                
            }

           
        }



    }
}