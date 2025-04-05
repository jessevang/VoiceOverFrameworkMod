// DialoguePatch.cs
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Reflection; // Required for MethodBase and BindingFlags

namespace VoiceOverFrameworkMod // Ensure this matches your main namespace
{
    internal static class DialoguePatches
    {
        // *** USING AccessTools TO FIND THE PROTECTED METHOD ***
        [HarmonyPatch] // Apply patch to the class, TargetMethod will specify the actual method
        internal static class Dialogue_parseDialogueString_Patch
        {
            // Use HarmonyPrepare/TargetMethod to find the specific protected method
            internal static MethodBase TargetMethod()
            {
                try
                {
                    // Find the protected virtual method with the signature (string, string)
                    // BindingFlags are needed to find non-public members
                    var method = typeof(Dialogue).GetMethod(
                        "parseDialogueString",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, // Include NonPublic
                        null, // Binder (usually null)
                        new Type[] { typeof(string), typeof(string) }, // Explicit parameter types
                        null  // Parameter modifiers (usually null)
                    );

                    if (method == null)
                    {
                        ModEntry.Instance?.Monitor.Log($"ERROR: Could not find Dialogue.parseDialogueString(string, string) via reflection!", LogLevel.Error);
                    }
                    else
                    {
                        ModEntry.Instance?.Monitor.Log($"Successfully found Dialogue.parseDialogueString(string, string) via reflection for patching.", LogLevel.Trace);
                    }
                    return method; // Return the found method (or null if not found)
                }
                catch (Exception ex)
                {
                    ModEntry.Instance?.Monitor.Log($"ERROR finding Dialogue.parseDialogueString target method: {ex.Message}", LogLevel.Error);
                    ModEntry.Instance?.Monitor.Log(ex.ToString(), LogLevel.Trace);
                    return null;
                }
            }

            // Prefix signature must match the target method's parameters
            internal static void Prefix(Dialogue __instance, string masterString, string translationKey)
            {
                try
                {
                    NPC speaker = __instance?.speaker;

                    if (ModEntry.Instance == null || speaker == null || string.IsNullOrWhiteSpace(translationKey))
                    {
                        return;
                    }

                    // Key Normalization Logic (keep this from the previous fix)
                    string dialogueKeyToUse = translationKey;
                    string expectedPrefix = $"Characters\\Dialogue\\{speaker.Name}:";
                    if (translationKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        dialogueKeyToUse = translationKey.Substring(expectedPrefix.Length);
                        // ModEntry.Instance.Monitor.Log($"[HarmonyPatch] Normalizing key '{translationKey}' to '{dialogueKeyToUse}'", LogLevel.Trace); // Optional Trace
                    }

                    if (string.IsNullOrWhiteSpace(dialogueKeyToUse))
                    {
                        // ModEntry.Instance.Monitor.Log($"[HarmonyPatch] Dialogue key became empty after normalization ('{translationKey}'). Skipping.", LogLevel.Trace); // Optional Trace
                        return;
                    }

                    // Use the normalized key to play voice
                    // ModEntry.Instance.Monitor.Log($"[HarmonyPatch] Attempting lookup with Key '{dialogueKeyToUse}' for speaker '{speaker.Name}'.", LogLevel.Trace); // Optional Trace
                    ModEntry.Instance.TryPlayVoice(speaker.Name, dialogueKeyToUse); // Use dialogueKeyToUse
                }
                catch (Exception ex)
                {
                    ModEntry.Instance?.Monitor.Log($"ERROR in Dialogue.parseDialogueString Prefix patch: {ex.Message}", LogLevel.Error);
                    ModEntry.Instance?.Monitor.Log(ex.ToString(), LogLevel.Trace);
                }
            }
        }
        // *** END OF REVISED PATCH ***

        // *** TODO: Add more patches if needed ***
    }
}