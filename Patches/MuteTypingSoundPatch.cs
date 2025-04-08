using HarmonyLib;
using StardewValley;

namespace VoiceOverFrameworkMod
{
    public static class MuteTypingSoundPatch
    {
        public static void ApplyPatch(Harmony harmony, StardewModdingAPI.IMonitor monitor)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.playSound), new[] { typeof(string), typeof(int) }),
                prefix: new HarmonyMethod(typeof(MuteTypingSoundPatch), nameof(PlaySound_Prefix))
            );
        }

        public static bool PlaySound_Prefix(string cueName)
        {
            if (ModEntry.Instance?.Config?.turnoffdialoguetypingsound == true &&
                cueName == "dialogueCharacter")
            {
                return false; // block the typing sound
            }

            return true; // allow all other sounds
        }
    }
}
