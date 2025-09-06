using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {




        /// <summary>Loads Characters/Dialogue/rainy for test</summary>
        private Dictionary<string, string> LoadRainyForNpc(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return null;

            try
            {
                var rainy = this.Helper.GameContent.Load<Dictionary<string, string>>("Characters/Dialogue/rainy");
                if (rainy != null && rainy.TryGetValue(npcName, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    return new Dictionary<string, string> { [npcName] = raw };
            }
            catch
            {

            }
            return null;
        }






        /// <summary>
        /// Load Data/EngagementDialogue[.<lang>] entries belonging to this NPC.
        /// Keys are like "Abigail0", "Abigail1", etc. Returns null if none.
        /// </summary>
        private Dictionary<string, string> LoadEngagementForNpc(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return null;

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>("Data/EngagementDialogue");
                if (dict == null || dict.Count == 0) return null;

                // match keys that start with the NPC name, case-insensitive (e.g., "Abigail0", "Abigail1")
                var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    if (kv.Key.StartsWith(npcName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        results[kv.Key] = kv.Value;
                    }
                }
                return results.Count > 0 ? results : null;
            }
            catch
            {
                // file might be missing for the language; that's fine
                return null;
            }
        }













        // Put near your other loaders
        private static string Canon(string s) =>
            Regex.Replace(s ?? "", @"[^A-Za-z0-9]", "").ToLowerInvariant();

        // Special key-token aliases (right side = how it appears in keys)
        private static readonly Dictionary<string, string[]> NpcKeyAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // game NPC name -> possible tokens in keys
                ["Qi"] = new[] { "Qi", "MrQi", "MisterQi" },
                // add more if you hit edge cases:
                // ["Professor Snail"] = new[] { "ProfessorSnail" },
            };

        /// <summary>
        /// Load Data/ExtraDialogue[.<lang>] and return ONLY entries clearly tied to this NPC.
        /// Matching rules:
        ///   • a token equals the NPC name (case/underscore-insensitive), e.g. "SeedShop_Abigail_Drawers"
        ///   • key starts with NPC name + digits, e.g. "Birdie0"
        ///   • alias tokens (e.g., Qi: "MrQi", "MisterQi") also match
        /// </summary>
        private Dictionary<string, string> LoadExtraDialogueForNpc(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return null;

            try
            {
                var dict = this.Helper.GameContent.Load<Dictionary<string, string>>("Data/ExtraDialogue");
                if (dict == null || dict.Count == 0) return null;

                // canonical forms
                string npcCanon = Canon(npcName);
                var aliasCanons = (NpcKeyAliases.TryGetValue(npcName, out var aliases) ? aliases : new[] { npcName })
                    .Select(Canon)
                    .ToHashSet(StringComparer.Ordinal);

                bool KeyMatches(string key)
                {
                    if (string.IsNullOrWhiteSpace(key)) return false;

                    // 1) prefix style: "Abigail0", "Birdie12", etc.
                    var keyCanon = Canon(key);
                    if (keyCanon.StartsWith(npcCanon))
                    {
                        // either exact "Name" or "Name<digits>…" or "Name_…"
                        if (keyCanon.Length == npcCanon.Length) return true;
                        if (char.IsDigit(keyCanon[npcCanon.Length])) return true;
                    }

                    // 2) token style: "Location_Npc_Action"
                    foreach (var tok in key.Split('_'))
                    {
                        var t = Canon(tok);
                        if (t.Length == 0) continue;

                        if (t.Equals(npcCanon, StringComparison.Ordinal)) return true;
                        if (aliasCanons.Contains(t)) return true;
                    }

                    return false;
                }

                var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    if (KeyMatches(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        results[kv.Key] = kv.Value;
                }

                return results.Count > 0 ? results : null;
            }
            catch
            {
                // missing in some languages / packs is fine
                return null;
            }
        }


    }
}
