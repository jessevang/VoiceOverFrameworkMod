
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        private void CheckForSpeechBubblesLevel1()
        {
            // Don't play bubbles while a DialogueBox is up (your dialogue path handles that)
            if (Game1.activeClickableMenu is DialogueBox)
                return;

            foreach (var ch in EnumerateBubbleSpeakers())
            {
                if (ch == null) continue;

                // NPC & Farmer share these fields via Character
                // We only care when a bubble is actually active
                var npc = ch as NPC;
                var textField = npc?.showTextAboveHead ?? (ch is Farmer f && f.textAboveHead != null ? f.textAboveHead : null);
                int timer = npc?.textAboveHeadTimer ?? (ch is Farmer f2 ? f2.textAboveHeadTimer : 0);
                int pre = npc?.textAboveHeadPreTimer ?? (ch is Farmer f3 ? f3.textAboveHeadPreTimer : 0);
                float alpha = npc?.textAboveHeadAlpha ?? (ch is Farmer f4 ? f4.textAboveHeadAlpha : 0f);

                if (string.IsNullOrWhiteSpace(textField) || timer <= 0)
                {
                    // reset state when bubble ends
                    if (_bubbleStates.TryGetValue(ch, out var st))
                    {
                        st.LastText = null; st.LastTimer = -1; st.StableTicks = 0; st.Played = false;
                    }
                    continue;
                }

                // wait until the bubble is actually showing (preTimer elapsed and alpha ramped)
                if (pre > 0 || alpha <= 0f)
                    continue;

                // fetch per-char state
                var state = _bubbleStates.GetOrCreateValue(ch);

                // text changed? reset stabilization & play-once gate
                if (!string.Equals(textField, state.LastText, StringComparison.Ordinal))
                {
                    state.LastText = textField;
                    state.LastTimer = timer;
                    state.StableTicks = 0;
                    state.Played = false;
                }
                else
                {
                    // same text; observe stabilization
                    state.StableTicks++;
                }

                // require a few ticks stable like dialogue does
                if (state.StableTicks < Config.TextStabilizeTicks)
                    continue;

                // already played for this bubble instance
                if (state.Played)
                    continue;

                // resolve character & language
                string characterName = ch.Name;
                var pack = GetSelectedVoicePack(characterName);
                if (pack == null)
                    continue; // no pack selected → nothing to play

                // sanitize the visible bubble text to a DisplayPattern-style key
                string patternKey = SanitizeBubbleText(textField);
                if (string.IsNullOrWhiteSpace(patternKey))
                    continue;

                // Prefer V2 pattern map (Format >= 2). Your loader already indexes DisplayPattern into Entries (and EntriesByDisplayPattern).
                string fullPath = null;
                var lang = LocalizedContentManager.CurrentLanguageCode;

                if (pack.FormatMajor >= 2)
                {
                    // try EntriesByDisplayPattern first (strict canonical)
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
                    // V1 fallback (text keys)
                    TryToPlayVoice(characterName, patternKey, lang);
                    state.Played = true;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    if (Config.developerModeOn)
                        Monitor.Log($"[Bubble] {characterName}: \"{textField}\" → pattern \"{patternKey}\" → {fullPath}", LogLevel.Info);

                    PlayVoiceFromFile(fullPath);
                    state.Played = true;
                }
                else if (Config.developerModeOn)
                {
                    Monitor.Log($"[Bubble] No hit in pack for {characterName} pattern \"{patternKey}\".", LogLevel.Trace);
                }
            }
        }
    }
}
