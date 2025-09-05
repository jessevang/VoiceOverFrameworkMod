using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VoiceOverFrameworkMod.Patches
{
using HarmonyLib;
using StardewValley;

namespace VoiceOverFrameworkMod
{


    // Forces Elliott's %book token to always resolve to the *default* title.
    [HarmonyPatch(typeof(Game1), "get_elliottBookName")]
    public static class Patch_Game1_get_elliottBookName_ForceDefault
    {
        static void Postfix(ref string __result)
        {
            // Always return the default book name so %book is stable
            // (Using Game1.content to mirror the game's usual access)
            __result = Game1.content.LoadString("Strings\\Events:ElliottBook_default");
        }
    }
}

}
