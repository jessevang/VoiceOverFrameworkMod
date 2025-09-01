
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Generic;
namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        // small per-speaker cache of the most recent sheet key we saw
        // e.g. "Characters/Dialogue/Abigail:fall_Thu"
        private static readonly Dictionary<string, (string Key, long SeenTicks)> _lastSheetKeyBySpeaker =
            new(StringComparer.OrdinalIgnoreCase);

        // window for considering a cached key "fresh" (~2 seconds @ 60fps)
        private const long SheetKeyFreshTicks = 120;

        private static readonly Regex RxSheetRef = new(
            @"^(?<prefix>Characters\\Dialogue|Strings\\StringsFromCSFiles)[:\\](?<rest>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        internal static void ProbeRecordSheetKey(string speaker, string raw)
        {
            if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(raw))
                return;

            var m = RxSheetRef.Match(raw);
            if (!m.Success)
                return;

            string prefix = m.Groups["prefix"].Value;
            string rest = m.Groups["rest"].Value.Replace('\\', '/');

            string normalized = prefix.StartsWith("Characters", StringComparison.OrdinalIgnoreCase)
                ? "Characters/Dialogue/" + rest          // e.g. Characters/Dialogue/Abigail:fall_Thu
                : "Strings/StringsFromCSFiles:" + rest;  // e.g. Strings/StringsFromCSFiles:NPC.cs.4192

            _lastSheetKeyBySpeaker[speaker] = (normalized, Game1.ticks);
        }

        internal static bool TryGetRecentSheetKey(string speaker, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(speaker))
                return false;

            if (!_lastSheetKeyBySpeaker.TryGetValue(speaker, out var hit))
                return false;

            if (Game1.ticks - hit.SeenTicks > SheetKeyFreshTicks)
                return false;

            key = hit.Key;
            return true;
        }

        /// <summary>
        /// Call this once in your Entry / ApplyHarmonyPatches(harmony) to install the probe.
        /// </summary>
        private void InstallRawDialogueProbe(Harmony harmony)
        {
            try
            {
                RawDialogueProbePatches.Monitor = this.Monitor;

                int patched = RawDialogueProbePatches.PatchAllDialogueCtors(harmony);
                this.Monitor.Log($"[Probe] Installed raw Dialogue ctor probe on {patched} constructor(s).", LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"[Probe] Failed to install patches: {ex}", LogLevel.Error);
            }
        }

        /// <summary>
        /// Small helper to shorten long strings for logs.
        /// </summary>
        private static string Trunc(string s, int max = 160)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\n", "\\n");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    internal static class RawDialogueProbePatches
    {
        public static IMonitor Monitor;

        /// <summary>
        /// Dynamically find and patch all Dialogue constructors, regardless of overload order.
        /// </summary>
        public static int PatchAllDialogueCtors(Harmony harmony)
        {
            var type = typeof(StardewValley.Dialogue);
            var ctors = AccessTools.GetDeclaredConstructors(type)
                                   ?.Where(c => !c.IsStatic)
                                   ?.ToArray() ?? Array.Empty<ConstructorInfo>();

            int count = 0;
            foreach (var ctor in ctors)
            {
                try
                {
                    var prefix = new HarmonyMethod(typeof(RawDialogueProbePatches).GetMethod(
                        nameof(CtorPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic
                    ));
                    harmony.Patch(ctor, prefix: prefix);
                    count++;
                }
                catch (Exception ex)
                {
                    Monitor?.Log($"[Probe] Skipped ctor {ctor}: {ex.Message}", LogLevel.Trace);
                }
            }

            return count;
        }

        /// <summary>
        /// Prefix that inspects ctor args and logs the raw dialogue string (before tokens are expanded).
        /// Also records sheet keys so later text-only ctor can reuse them.
        /// </summary>
        private static void CtorPrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                // find the first string argument (the raw dialogue source or sheet key)
                string raw = __args?.FirstOrDefault(a => a is string) as string;

                // find an NPC among the args (speaker)
                var speaker = __args?.FirstOrDefault(a => a is NPC) as NPC;
                string who = speaker?.displayName ?? speaker?.Name ?? "(unknown)";

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // NEW: record sheet key if this ctor used one (e.g. "Characters\Dialogue\Abigail:fall_Thu")
                    ModEntry.ProbeRecordSheetKey(who, raw);

                    // log (keep at Trace to avoid chatty logs unless dev mode)
                    Monitor?.Log($"[Probe] Dialogue ctor: speaker='{who}' raw=\"{SanitizeForLog(raw)}\"", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"[Probe] CtorPrefix error: {ex}", LogLevel.Trace);
            }
        }

        private static string SanitizeForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", "").Replace("\n", "\\n");
            if (s.Length > 240) s = s.Substring(0, 240) + "…";
            return s;
        }
    }
}
