using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace handbookperf;

/// <summary>
/// Logs how long the handbook took to build its pages, and how much of that went to
/// matching meal-recipe ingredients, so a change can be measured without a profiler
/// attached (dotnet-trace's sampler stops the process often enough to swamp the
/// difference).
/// </summary>
internal static class BuildTiming
{
    private static ILogger logger;
    private static long mealMatchingTicks;

    public static void Apply(Harmony harmony, ILogger log)
    {
        logger = log;
        harmony.Patch(AccessTools.Method(typeof(GuiDialogHandbook), "LoadPages_Async"),
            prefix: new HarmonyMethod(typeof(BuildTiming), nameof(BuildStart)),
            postfix: new HarmonyMethod(typeof(BuildTiming), nameof(BuildEnd)));

        var mealMatching = new HarmonyMethod(typeof(BuildTiming), nameof(MatchStart));
        var mealMatchingEnd = new HarmonyMethod(typeof(BuildTiming), nameof(MatchEnd));
        harmony.Patch(AccessTools.Method(typeof(ModSystemSurvivalHandbook), "CreateCachedMealRecipeStacks"),
            prefix: mealMatching, postfix: mealMatchingEnd);
        if (AccessTools.TypeByName("ACulinaryArtillery.Util.HandbookInfoExtensions") is Type aca
            && AccessTools.Method(aca, "CreateCachedMealRecipeStacks") is { } acaMatching)
        {
            harmony.Patch(acaMatching, prefix: mealMatching, postfix: mealMatchingEnd);
        }
    }

    private static void BuildStart(out long __state)
    {
        Interlocked.Exchange(ref mealMatchingTicks, 0);
        __state = Stopwatch.GetTimestamp();
    }

    private static void BuildEnd(GuiDialogHandbook __instance, long __state)
    {
        double total = Stopwatch.GetElapsedTime(__state).TotalSeconds;
        if (total < 1) return;   // the command handbook; nothing to report
        double matching = TimeSpan.FromTicks(Interlocked.Read(ref mealMatchingTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency).TotalSeconds;
        logger.Notification("{0} built its pages in {1:F1} s, {2:F1} s of it matching meal recipe ingredients ({3})",
            __instance.GetType().Name, total, matching, AcaMatchingStackFix.Mode);
    }

    private static void MatchStart(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void MatchEnd(long __state) =>
        Interlocked.Add(ref mealMatchingTicks, Stopwatch.GetTimestamp() - __state);
}
