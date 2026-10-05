using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace handbookperf;

/// <summary>
/// Attribute Rendering Library replaces GuiDialogHandbook.FilterItems with a copy of
/// vanilla's that also honours IHandbookARL.ExcludeFromHandbookSearch. The copy
/// regex-scans every page's full text on each call, reaching CountMatches through
/// reflection, and because it returns false it also shadows BetterHandbook's cached
/// filter. Opening the handbook in a large pack then stalls the client for most of a
/// second.
///
/// The exclusion is the only thing the patch adds, so when no collectible implements
/// IHandbookARL the patch does nothing but cost time, and it is removed here.
/// </summary>
internal static class ArlFilterFix
{
    private const string ArlModId = "attributerenderinglibrary";
    private const string ArlHarmonyId = ArlModId;   // ARL patches as new Harmony(Mod.Info.ModID)

    public static void Apply(ICoreClientAPI capi, Mod mod)
    {
        if (!capi.ModLoader.IsModEnabled(ArlModId)) return;

        MethodInfo filterItems = AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems");
        var arlPrefixes = Harmony.GetPatchInfo(filterItems)?.Prefixes.Count(p => p.owner == ArlHarmonyId) ?? 0;
        if (arlPrefixes == 0)
        {
            mod.Logger.Notification("No {0} prefix on GuiDialogHandbook.FilterItems; nothing to do", ArlHarmonyId);
            return;
        }

        int excluding = CountCollectiblesUsingArlHandbook(capi);
        if (excluding != 0)
        {
            // -1: cannot tell, so err on the side of keeping ARL's behaviour
            mod.Logger.Notification("Leaving {0}'s handbook filter in place: {1}", ArlModId,
                excluding < 0 ? "could not resolve IHandbookARL" : $"{excluding} collectibles use its handbook exclusion");
            return;
        }

        new Harmony(mod.Info.ModID).Unpatch(filterItems, HarmonyPatchType.Prefix, ArlHarmonyId);
        mod.Logger.Notification("Removed {0}'s GuiDialogHandbook.FilterItems prefix ({1}); no collectible uses its handbook exclusion",
            ArlModId, arlPrefixes);
    }

    private static int CountCollectiblesUsingArlHandbook(ICoreClientAPI capi)
    {
        Type iface = AccessTools.TypeByName("AttributeRenderingLibrary.IHandbookARL");
        if (iface == null) return -1;

        // The same lookup ARL's prefix makes per page
        MethodInfo getInterface = typeof(CollectibleObject)
            .GetMethod(nameof(CollectibleObject.GetCollectibleInterface))!
            .MakeGenericMethod(iface);

        return capi.World.Collectibles.Count(c => c?.Code != null && getInterface.Invoke(c, null) != null);
    }
}
