// Harmony patches go here. Example shape (rename the class/method to something real from the game):
//
// [HarmonyPatch(typeof(SomeGameClass), "SomeMethod")]
// internal static class SomeGameClass_SomeMethod_Patch
// {
//     // Runs before the original; return false to skip it.
//     private static bool Prefix(SomeGameClass __instance)
//     {
//         return true;
//     }
//
//     // Runs after the original; __result is the return value.
//     private static void Postfix(SomeGameClass __instance)
//     {
//         Plugin.Log.LogInfo("SomeMethod ran");
//     }
// }

namespace StarterMod
{
}
