// ModEntry.SpeechBubblePoller.cs
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;
using System.Linq;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        // Non-public NPC fields (SDV 1.6) via Harmony
        private static readonly AccessTools.FieldRef<NPC, string> NPC_textAboveHead
            = AccessTools.FieldRefAccess<NPC, string>("textAboveHead");
        private static readonly AccessTools.FieldRef<NPC, int> NPC_textAboveHeadTimer
            = AccessTools.FieldRefAccess<NPC, int>("textAboveHeadTimer");
        private static readonly AccessTools.FieldRef<NPC, int> NPC_textAboveHeadPreTimer
            = AccessTools.FieldRefAccess<NPC, int>("textAboveHeadPreTimer");
        private static readonly AccessTools.FieldRef<NPC, float> NPC_textAboveHeadAlpha
            = AccessTools.FieldRefAccess<NPC, float>("textAboveHeadAlpha");

        /// <summary>Poll NPC speech bubbles and play audio once per bubble instance.</summary>
        private void CheckForSpeechBubblesLevel1()
        {
            // Don’t compete with DialogueBox VO.
            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox)
                return;

            var npcs = Game1.currentLocation?.characters?.OfType<NPC>();
            if (npcs == null) return;

            foreach (var npc in npcs)
            {
                if (npc == null) continue;

                string text = NPC_textAboveHead(npc);
                int timer = NPC_textAboveHeadTimer(npc);
                int pre = NPC_textAboveHeadPreTimer(npc);
                float alpha = NPC_textAboveHeadAlpha(npc);

                // Bubble gone → reset state
                if (string.IsNullOrWhiteSpace(text) || timer <= 0)
                {
                    if (_bubbleStates.TryGetValue(npc, out var gone))
                    {
                        gone.LastText = null;
                        gone.LastTimer = -1;
                        gone.Played = false;
                    }
                    continue;
                }

                // Must be actually visible
                if (pre > 0 || alpha <= 0f)
                    continue;

                var state = _bubbleStates.GetOrCreateValue(npc);

                // Detect new bubble instance
                bool isNewInstance =
                    state.LastText == null
                    || !string.Equals(text, state.LastText, StringComparison.Ordinal)
                    || timer > state.LastTimer + 5; // timer reset/jump = new bubble

                if (isNewInstance)
                {
                    state.LastText = text;
                    state.LastTimer = timer;
                    state.Played = false;
                }
                else
                {
                    state.LastTimer = timer;
                }

                if (state.Played)
                    continue;

                string characterName = npc.Name;
                var pack = GetSelectedVoicePack(characterName);
                if (pack == null)
                {
                    state.Played = true;
                    continue;
                }

                string patternKey = SanitizeBubbleText(text);
                if (string.IsNullOrWhiteSpace(patternKey))
                {
                    state.Played = true;
                    continue;
                }

                string fullPath = null;

                if (pack.FormatMajor >= 2)
                {
                    if (pack.EntriesByDisplayPattern != null &&
                        pack.EntriesByDisplayPattern.TryGetValue(patternKey, out var relA))
                    {
                        fullPath = PathUtilities.NormalizePath(Path.Combine(pack.BaseAssetPath, relA));
                    }
                    else if (pack.Entries != null &&
                             pack.Entries.TryGetValue(patternKey, out var relB))
                    {
                        fullPath = PathUtilities.NormalizePath(Path.Combine(pack.BaseAssetPath, relB));
                    }
                }
                else
                {
                    // V1 fallback
                    TryToPlayVoice(characterName, patternKey, LocalizedContentManager.CurrentLanguageCode);
                    state.Played = true;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    if (Config.developerModeOn)
                        Monitor.Log($"[Bubble] {characterName}: \"{text}\" → \"{patternKey}\"", LogLevel.Info);

                    if (File.Exists(fullPath))
                    {
                        PlayVoiceFromFile(fullPath);
                        state.Played = true;
                    }
                    else
                    {
                        if (_collectV2Failures)
                            V2AddFailure(
                                speaker: characterName,
                                dialogue: text,
                                removables: Enumerable.Empty<string>(),
                                stripped: SanitizeBubbleText(text),
                                key: patternKey,
                                matched: true,
                                missingAudio: true,
                                audioPath: fullPath
                            );
                        state.Played = true;
                    }
                }
                else
                {
                    if (_collectV2Failures)
                        V2AddFailure(
                            speaker: characterName,
                            dialogue: text,
                            removables: Enumerable.Empty<string>(),
                            stripped: SanitizeBubbleText(text),
                            key: patternKey,
                            matched: false,
                            missingAudio: false
                        );

                    if (Config.developerModeOn)
                        Monitor.Log($"[Bubble] No pack match for {characterName} pattern \"{patternKey}\".", LogLevel.Trace);

                    state.Played = true;
                }
            }
        }



    }
}
