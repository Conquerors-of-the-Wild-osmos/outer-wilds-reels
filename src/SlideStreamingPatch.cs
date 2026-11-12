using HarmonyLib;

namespace CustomReels
{
    [HarmonyPatch(typeof(SlideCollectionContainer), "streamingTexturesAvailable", MethodType.Getter)]
    internal static class SlideStreamingPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool WaitForCustomCollection(SlideCollectionContainer __instance, ref bool __result)
        {
            // NH adds its component before replacing the inherited vanilla
            // collection. Its streaming prefix casts that temporary collection
            // to NHSlideCollection without checking the type first.
            // Only defer our reels during this initialization window.
            if (__instance.gameObject.name != "CustomReels_AspectRatioTest"
                || __instance.GetType().FullName != "NewHorizons.Components.EOTE.NHSlideCollectionContainer")
                return true;

            var collection = __instance._slideCollection;
            if (collection != null
                && collection.GetType().FullName == "NewHorizons.Components.EOTE.NHSlideCollection")
                return true;

            __result = false;
            // Skips the later NH prefix (it returns bool and has a ref argument).
            return false;
        }
    }
}
