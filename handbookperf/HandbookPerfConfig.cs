namespace handbookperf;

/// <summary>
/// ModConfig/handbookperf.json. Each fix can be turned off on its own; a change takes
/// effect the next time a world loads.
/// </summary>
public class HandbookPerfConfig
{
    public const string FileName = "handbookperf.json";

    /// <summary>
    /// Answer lang lookups that miss an exact key from an index of the wildcard keys,
    /// rather than testing every one in turn. Client.
    /// </summary>
    public bool LangIndex { get; set; } = true;

    /// <summary>
    /// With A Culinary Artillery installed, replace its meal-ingredient matcher with an
    /// indexed copy that gives the same results, and match each handbook meal ingredient
    /// only against the stacks that could satisfy it. Client.
    /// </summary>
    public bool AcaMatcher { get; set; } = true;

    /// <summary>
    /// Find crafting grid recipes without rescanning the candidate list for every
    /// recipe added to it, and without testing shaped recipes of the wrong size. On
    /// whichever side has the mod.
    /// </summary>
    public bool CraftingGrid { get; set; } = true;

    /// <summary>
    /// With OptiTime installed, run this mod's crafting grid lookup in place of OptiTime's,
    /// which can pick a different recipe from vanilla's. Off leaves the lookup to OptiTime
    /// on the client. Client.
    /// </summary>
    public bool CraftingGridOverOptiTime { get; set; } = true;

    /// <summary>
    /// List the handbook's pages directly when its search box is empty, rather than
    /// counting regex matches in every page. Client.
    /// </summary>
    public bool EmptyHandbookSearch { get; set; } = true;

    /// <summary>
    /// With Attribute Rendering Library 3.2.x installed and no item using its handbook
    /// exclusion, remove its handbook search patch, which then only costs time. Client.
    /// </summary>
    public bool RemoveArlFilter { get; set; } = true;

    /// <summary>
    /// Log how long the handbook took to build its pages, and any crafting grid lookup
    /// taking 5 ms or more.
    /// </summary>
    public bool LogTimings { get; set; } = true;
}
