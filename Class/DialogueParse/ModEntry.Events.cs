using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // ──────────────────────────────────────────────────────────────────────────
        // V2 parser + V1-ish source discovery: enumerate every likely event sheet,
        // load it in the requested language (when possible), then sanitize with DialogueUtil.
        // ──────────────────────────────────────────────────────────────────────────

        // Compares speakers to the target NPC name (case-insensitive).
        private static bool IsCharacterMatch_Event(string speaker, string targetCharacterName) =>
            !string.IsNullOrWhiteSpace(speaker)
            && !string.IsNullOrWhiteSpace(targetCharacterName)
            && speaker.Equals(targetCharacterName, StringComparison.OrdinalIgnoreCase);

        // Remove trailing condition tails from event IDs (e.g., "100162/t 600 2600" -> "100162")
        private static string CleanEventId(string rawEventKey)
        {
            if (string.IsNullOrWhiteSpace(rawEventKey))
                return string.Empty;
            int slash = rawEventKey.IndexOf('/');
            return (slash > 0) ? rawEventKey.Substring(0, slash) : rawEventKey.Trim();
        }

        // Build: Events/<map>:<cleanId>:s<N>
        private static string BuildEventsTranslationKey(string map, string eventIdRaw, int speakSerial) =>
            $"Events/{map}:{CleanEventId(eventIdRaw)}:s{speakSerial}";

        // Common sheets that frequently exist even if the location isn't instantiated yet.


        // Loads Data/Events/<map> for the specified language (tries <map>.<lang> then <map>).
        // Helper: load Data/Events/<map> with language fallback
        // Helper: load Data/Events/<map> with language fallback
        private static Dictionary<string, string> LoadEventsForMapName(string mapName, string languageCode, IGameContentHelper content)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return null;

            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            Dictionary<string, string> dict = null;

            // Try target language first
            try
            {
                dict = content.Load<Dictionary<string, string>>($"Data/Events/{mapName}{langSuffix}");
                if (dict != null && dict.Count > 0)
                    return dict;
            }
            catch { /* fall through */ }

            // Fallback to English
            try
            {
                dict = content.Load<Dictionary<string, string>>($"Data/Events/{mapName}");
                if (dict != null && dict.Count > 0)
                    return dict;
            }
            catch { /* ignore */ }

            return null;
        }




        // Enumerate candidate event sources from multiple places (deduped).
        // V1-style source enumeration with V2 language fallback + sanitizer feed
        private IEnumerable<(string MapName, Dictionary<string, string> Dict)>
            EnumerateEventSources(string languageCode, IGameContentHelper content)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // local iterator that never uses try/catch around a yield
            IEnumerable<(string MapName, Dictionary<string, string> Dict)> TryYieldLocal(string map)
            {
                if (string.IsNullOrWhiteSpace(map) || yielded.Contains(map))
                    yield break;

                var dict = LoadEventsForMapName(map, languageCode, content);
                if (dict != null && dict.Count > 0)
                {
                    yielded.Add(map);
                    yield return (map, dict);
                }
            }

            // 1) Live per-location dictionaries (captures patched/modded events)
            foreach (var loc in Game1.locations)
            {
                if (TryGetLocationEvents(loc, out _, out var liveDict) && liveDict != null && liveDict.Count > 0)
                {
                    var map = loc?.NameOrUniqueName ?? loc?.Name ?? "Unknown";
                    if (!yielded.Contains(map))
                    {
                        yielded.Add(map);
                        yield return (map, liveDict);
                    }
                }

                // Also attempt explicit loads with language fallback for this location name(s)
                var n1 = loc?.NameOrUniqueName;
                var n2 = loc?.Name;
                foreach (var item in TryYieldLocal(n1)) yield return item;
                if (!string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase))
                    foreach (var item in TryYieldLocal(n2)) yield return item;
            }

            // 2) Keys from Data/Locations (maps that may not exist in the current session)
            Dictionary<string, StardewValley.GameData.Locations.LocationData> locData = null;
            try
            {
                locData = Game1.content.Load<Dictionary<string, StardewValley.GameData.Locations.LocationData>>("Data/Locations");
            }
            catch { /* ignore */ }

            if (locData != null)
            {
                foreach (var key in locData.Keys)
                    foreach (var item in TryYieldLocal(key)) yield return item;
            }

            // 3) Known common event file names (your static list)
            foreach (var map in CommonEventFileNames)
                foreach (var item in TryYieldLocal(map)) yield return item;

            // 4) Locationless events: Temp (with language fallback)
            Dictionary<string, string> temp = null;
            string langSuffix = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? "" : $".{languageCode}";
            try { temp = content.Load<Dictionary<string, string>>($"Data/Events/Temp{langSuffix}"); }
            catch
            {
                try { temp = content.Load<Dictionary<string, string>>("Data/Events/Temp"); }
                catch { /* ignore */ }
            }

            if (temp != null && temp.Count > 0 && !yielded.Contains("Temp"))
            {
                yielded.Add("Temp");
                yield return ("Temp", temp);
            }
        }

        // Loads Data/Events for a location by trying NameOrUniqueName then Name (current game language).
        private bool TryGetLocationEvents(GameLocation location, out string assetName, out Dictionary<string, string> eventData)
        {
            eventData = null;
            assetName = null;
            if (location == null) return false;

            var candidates = new List<string>();
            string n1 = location.NameOrUniqueName;
            string n2 = location.Name;

            if (!string.IsNullOrWhiteSpace(n1))
                candidates.Add($"Data/Events/{n1}");
            if (!string.IsNullOrWhiteSpace(n2) &&
                !string.Equals(n2, n1, StringComparison.OrdinalIgnoreCase))
                candidates.Add($"Data/Events/{n2}");

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var dict = Game1.content.Load<Dictionary<string, string>>(candidate);
                    if (dict != null && dict.Count > 0)
                    {
                        assetName = candidate;
                        eventData = dict;
                        return true;
                    }
                }
                catch
                {
                    // asset may not exist; ignore and try the next candidate
                }
            }

            return false;
        }


        // ── Main: emit manifest entries from events, using DialogueUtil for parsing ──
        private IEnumerable<VoiceEntryTemplate> BuildFromEvents(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext
        )
        {
            var outList = new List<VoiceEntryTemplate>();
            if (string.IsNullOrWhiteSpace(characterName))
                return outList;

            int en = entryNumber;

            // tolerant patterns (don’t anchor to end so “speak X "..." extra” still matches)
            var speakCommandRegex = new Regex(@"^\s*speak\s+(\w+)\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var namedQuoteRegex = new Regex(@"\b(?:textAboveHead|drawDialogue|message|showText)\s*(\w*)\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var genericQuoteRegex = new Regex(@"""((?:[^""\\]|\\.){4,})""", RegexOptions.Compiled);
            var quickQuestionPrefix = new Regex(@"^\s*quickQuestion\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (var (mapName, eventData) in EnumerateEventSources(languageCode, content))
            {
                foreach (var (eventIdRaw, eventScript) in eventData)
                {
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                    string cleanId = CleanEventId(eventIdRaw);
                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;
                    int speakSerialForTarget = -1; // per-event serial

                    void EmitFromOneEventText(string rawText)
                    {
                        if (string.IsNullOrWhiteSpace(rawText))
                            return;

                        bool trace = this.Config?.developerModeOn == true;

                        // raw captured is still escaped (contains \" and \\)
                        if (trace) this.Monitor?.Log(
                            $"[EVENT/TRACE] capture raw(escaped) [{mapName}/{cleanId}] -> \"{rawText}\"",
                            LogLevel.Info
                        );

                        // unescape like the game does (\" and \\)
                        string unescaped = Regex.Unescape(rawText);
                        if (trace) this.Monitor?.Log(
                            $"[EVENT/TRACE] capture raw(unescaped) [{mapName}/{cleanId}] -> \"{unescaped}\"",
                            LogLevel.Info
                        );

                        // split $b as pages for events (what the sanitizer sees)
                        var segs = DialogueUtil.SplitAndSanitize(unescaped, splitBAsPage: true);

                        if (trace) this.Monitor?.Log(
                            $"[EVENT/TRACE] SplitAndSanitize -> {(segs?.Count ?? 0)} seg(s) for [{mapName}/{cleanId}]",
                            LogLevel.Info
                        );

                        if (segs == null || segs.Count == 0)
                            return;

                        foreach (var seg in segs)
                        {
                            speakSerialForTarget++;

                            string genderTail = string.IsNullOrEmpty(seg.Gender) ? "" : $"_{seg.Gender}";
                            string fileName = $"{en}{genderTail}.{ext}";
                            string audioPath = Path.Combine("assets", languageCode, characterName, fileName).Replace('\\', '/');

                            if (trace)
                            {
                                this.Monitor?.Log(
                                    $"[EVENT/TRACE]   seg p={seg.PageIndex} g={seg.Gender ?? "na"}\n" +
                                    $"                 Actor=[{seg.Actor}]\n" +
                                    $"                 Display=[{seg.Display}]",
                                    LogLevel.Info
                                );
                            }

                            outList.Add(new VoiceEntryTemplate
                            {
                                DialogueFrom = $"Event/{mapName}/{cleanId}:s{speakSerialForTarget}",
                                DialogueText = seg.Actor, // includes {Portrait:*}
                                AudioPath = audioPath,
                                TranslationKey = BuildEventsTranslationKey(mapName, cleanId, speakSerialForTarget),
                                PageIndex = seg.PageIndex,       // keep true page index from util
                                DisplayPattern = seg.Display,    // portraits/tokens removed
                                GenderVariant = seg.Gender
                            });

                            if (trace)
                                this.Monitor?.Log(
                                    $"[EVENT/TRACE] +EMIT Event/{mapName}/{cleanId}:s{speakSerialForTarget} -> {audioPath}",
                                    LogLevel.Info
                                );

                            en++;
                        }
                    }

                    void ProcessOneCommand(string cmdRaw)
                    {
                        string command = (cmdRaw ?? "").Trim();
                        if (command.Length == 0) return;

                        bool trace = this.Config?.developerModeOn == true;

                        // Expand quickQuestion reply blocks after (break)
                        if (quickQuestionPrefix.IsMatch(command) && command.IndexOf("(break)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (trace)
                                this.Monitor?.Log($"[EVENT/TRACE] quickQuestion with (break) [{mapName}/{cleanId}] -> expanding sub-blocks", LogLevel.Info);

                            var pieces = command.Split(new[] { "(break)" }, StringSplitOptions.None);
                            for (int i = 1; i < pieces.Length; i++)
                            {
                                string normalized = (pieces[i] ?? "").Replace('\\', '/');
                                foreach (var sub in normalized.Split('/'))
                                    ProcessOneCommand(sub);
                            }
                            return;
                        }

                        // Case 1: speak <NPC> "..."
                        var mSpeak = speakCommandRegex.Match(command);
                        if (mSpeak.Success)
                        {
                            string speaker = mSpeak.Groups[1].Value;
                            string captured = mSpeak.Groups[2].Value; // still escaped
                            if (trace)
                                this.Monitor?.Log($"[EVENT/TRACE] match: speak {speaker} -> \"{captured}\" [{mapName}/{cleanId}]", LogLevel.Info);

                            lastSpeaker = speaker;
                            if (IsCharacterMatch_Event(speaker, characterName))
                                EmitFromOneEventText(captured);

                            return;
                        }

                        // Case 2: drawDialogue/showText/textAboveHead/message <maybeSpeaker> "..."
                        var mNamed = namedQuoteRegex.Match(command);
                        if (mNamed.Success)
                        {
                            string maybeSpeaker = mNamed.Groups[1].Value;
                            string captured = mNamed.Groups[2].Value; // still escaped
                            if (trace)
                                this.Monitor?.Log($"[EVENT/TRACE] match: named {maybeSpeaker} -> \"{captured}\" [{mapName}/{cleanId}]", LogLevel.Info);

                            if (!string.IsNullOrWhiteSpace(maybeSpeaker))
                                lastSpeaker = maybeSpeaker;

                            if (!string.IsNullOrWhiteSpace(lastSpeaker) && IsCharacterMatch_Event(lastSpeaker, characterName))
                                EmitFromOneEventText(captured);

                            return;
                        }

                        // Case 3: generic quoted strings when context says our NPC is talking
                        if (!string.IsNullOrWhiteSpace(lastSpeaker) && IsCharacterMatch_Event(lastSpeaker, characterName))
                        {
                            if (trace)
                                this.Monitor?.Log($"[EVENT/TRACE] context speaker='{lastSpeaker}' -> scanning generic quotes [{mapName}/{cleanId}]", LogLevel.Info);

                            foreach (Match gm in genericQuoteRegex.Matches(command))
                            {
                                string captured = gm.Groups[1].Value; // still escaped when present
                                string chunk = captured.Trim();
                                if (chunk.Length > 3 && !chunk.StartsWith("..."))
                                {
                                    if (trace)
                                        this.Monitor?.Log($"[EVENT/TRACE] match: generic -> \"{captured}\" [{mapName}/{cleanId}]", LogLevel.Info);

                                    EmitFromOneEventText(captured);
                                }
                            }
                        }
                    }


                    foreach (var raw in commands)
                        ProcessOneCommand(raw);
                }
            }

            entryNumber = en;
            return outList;
        }
    }
}
