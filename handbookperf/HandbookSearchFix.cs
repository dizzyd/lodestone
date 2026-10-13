using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace handbookperf;

/// <summary>
/// The handbook runs GuiDialogHandbook.FilterItems each time it opens or changes tab.
/// With the search box empty, vanilla's search regex is an empty group, which matches
/// at every character, so it counts that many matches in every page's title and text
/// only to show every page: most of a second with 20,000 pages. Every title counts at
/// least one match and an empty search sorts nothing, so the result is every
/// non-duplicate page of the category, in order, and this builds that list directly.
///
/// Attribute Rendering Library's prefix replaces FilterItems with a copy that also hides
/// pages its IHandbookARL items ask to be hidden. While that prefix is there, this runs
/// ahead of it, hides the same pages, and skips it for the empty search only.
/// </summary>
internal static class HandbookSearchFix
{
    private const string ArlHarmonyId = "attributerenderinglibrary";

    private static readonly AccessTools.FieldRef<GuiDialogHandbook, List<IFlatListItem>> Shown =
        AccessTools.FieldRefAccess<GuiDialogHandbook, List<IFlatListItem>>("shownHandbookPages");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, List<GuiHandbookPage>> All =
        AccessTools.FieldRefAccess<GuiDialogHandbook, List<GuiHandbookPage>>("allHandbookPages");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, string> SearchText =
        AccessTools.FieldRefAccess<GuiDialogHandbook, string>("currentSearchText");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, string> Category =
        AccessTools.FieldRefAccess<GuiDialogHandbook, string>("currentCatgoryCode");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, bool> Loading =
        AccessTools.FieldRefAccess<GuiDialogHandbook, bool>("loadingPagesAsync");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, GuiComposer> Overview =
        AccessTools.FieldRefAccess<GuiDialogHandbook, GuiComposer>("overviewGui");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, double> ListHeight =
        AccessTools.FieldRefAccess<GuiDialogHandbook, double>("listHeight");

    private static MethodInfo filterItems;
    private static MethodInfo getArlInterface;   // CollectibleObject.GetCollectibleInterface<IHandbookARL>
    private static MethodInfo excludeFromSearch;  // IHandbookARL.ExcludeFromHandbookSearch

    public static void Apply(Harmony harmony, Mod mod)
    {
        filterItems = AccessTools.Method(typeof(GuiDialogHandbook), nameof(GuiDialogHandbook.FilterItems));
        if (Harmony.GetPatchInfo(filterItems)?.Owners.Contains(harmony.Id) == true) return;

        // Another mod's rewrite of the search would be bypassed for the empty search
        Patches info = Harmony.GetPatchInfo(filterItems);
        string[] others = (info == null ? [] : info.Prefixes.Concat(info.Transpilers))
            .Select(p => p.owner).Where(owner => owner != harmony.Id && owner != ArlHarmonyId).Distinct().ToArray();
        if (others.Length > 0)
        {
            mod.Logger.Notification("Leaving the handbook's empty search alone: GuiDialogHandbook.FilterItems is already rewritten by {0}", string.Join(", ", others));
            return;
        }

        if (AccessTools.TypeByName("AttributeRenderingLibrary.IHandbookARL") is Type arl)
        {
            getArlInterface = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetCollectibleInterface)).MakeGenericMethod(arl);
            excludeFromSearch = AccessTools.Method(arl, "ExcludeFromHandbookSearch");
        }

        var prefix = new HarmonyMethod(typeof(HandbookSearchFix), nameof(Prefix)) { priority = Priority.First, before = [ArlHarmonyId] };
        harmony.Patch(filterItems, prefix: prefix);
        mod.Logger.Notification("Listing the handbook's pages directly when the search box is empty");
    }

    private static bool Prefix(GuiDialogHandbook __instance)
    {
        GuiComposer overview = Overview(__instance);
        if (overview == null || Loading(__instance) || (SearchText(__instance) ?? "").Length > 0) return true;

        bool arlFilters = excludeFromSearch != null
            && Harmony.GetPatchInfo(filterItems)?.Prefixes.Any(p => p.owner == ArlHarmonyId) == true;
        string category = Category(__instance);
        List<IFlatListItem> shown = Shown(__instance);
        shown.Clear();
        foreach (GuiHandbookPage page in All(__instance))
        {
            if (arlFilters && ExcludedByArl(page)) continue;
            if (category != null && page.CategoryCode != category || page.IsDuplicate) continue;
            shown.Add(page);
        }

        GuiElementFlatList stacklist = overview.GetFlatList("stacklist");
        stacklist.CalcTotalHeight();
        overview.GetScrollbar("scrollbar").SetHeights((float)ListHeight(__instance), (float)stacklist.insideBounds.fixedHeight);
        return false;
    }

    // ARL's test, as its prefix makes it
    private static bool ExcludedByArl(GuiHandbookPage page)
    {
        if (page is not GuiHandbookItemStackPage { Stack: { } stack }) return false;
        object arl = stack.Collectible == null ? null : getArlInterface.Invoke(stack.Collectible, null);
        return arl != null && (bool)excludeFromSearch.Invoke(arl, [stack]);
    }
}
