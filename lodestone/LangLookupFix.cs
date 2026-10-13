using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace lodestone;

/// <summary>
/// A lang key with no exact entry makes TranslationService try every wildcard key
/// ("game:item-plank-*") with StartsWith and then every multi-wildcard key as a
/// compiled regex, in order, until one matches. Lang.GetMatching does that twice per
/// miss (HasTranslation, then GetMatchingIfExists), and most item description keys are
/// misses, so building the handbook's ~20k pages runs millions of those comparisons.
///
/// This answers both methods from an index instead: wildcard keys by prefix, regex
/// keys bucketed by their literal prefix, each carrying its position in the table so
/// that the first match in table order still wins. The index is rebuilt whenever a
/// table is replaced or mutated.
/// </summary>
internal static class LangLookupFix
{
    public static void Apply(Harmony harmony, Mod mod)
    {
        MethodInfo hasTranslation = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.HasTranslation),
            [typeof(string), typeof(bool), typeof(bool)]);
        MethodInfo getMatchingIfExists = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.GetMatchingIfExists));

        PatchUnlessRewritten(harmony, mod, hasTranslation, nameof(HasTranslationPrefix));
        PatchUnlessRewritten(harmony, mod, getMatchingIfExists, nameof(GetMatchingIfExistsPrefix));
    }

    // OptiTime's transpiler on HasTranslation only makes the not-found set thread-safe,
    // which HasTranslationPrefix does itself
    private const string OptiTimeNotFoundFix = "OptiTime.TranslationServicePatch";

    /// <summary>
    /// Another mod's prefix or transpiler on a method would be bypassed by this replacing
    /// prefix, so each method is left to it alone. Postfixes still run either way.
    /// </summary>
    private static void PatchUnlessRewritten(Harmony harmony, Mod mod, MethodInfo method, string prefix)
    {
        Patches info = Harmony.GetPatchInfo(method);
        string[] others = (info == null ? [] : info.Prefixes.Concat(info.Transpilers))
            .Where(p => p.owner != harmony.Id && p.PatchMethod.DeclaringType?.FullName != OptiTimeNotFoundFix)
            .Select(p => p.owner).Distinct().ToArray();
        if (others.Length > 0)
        {
            mod.Logger.Notification("Leaving TranslationService.{0} alone: already rewritten by {1}", method.Name, string.Join(", ", others));
            return;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(LangLookupFix), prefix));
        mod.Logger.Notification("Indexed TranslationService.{0} wildcard lookups", method.Name);
    }

    // ---- the two lookups, as vanilla, with the scans answered by the index ----

    private static bool HasTranslationPrefix(TranslationService __instance, string key, bool findWildcarded, bool logErrors, ref bool __result)
    {
        EnsureLoaded(__instance);
        string validKey = KeyWithDomain(key);
        if (EntryCache(__instance).ContainsKey(validKey))
        {
            __result = true;
            return false;
        }
        if (!findWildcarded)
        {
            __result = false;
            return false;
        }

        if (!key.Contains(':')) key = "game:" + key;   // == validKey; vanilla matches wildcards on this one
        LangIndex index = IndexFor(__instance);
        bool result = index.FirstWildcard(key) >= 0 || index.FirstRegex(validKey) >= 0;
        if (!result && logErrors && !key.Contains("desc-") && AddNotFound(__instance, key))
        {
            Logger(__instance).VerboseDebug("Lang key not found: " + key.Replace("{", "{{").Replace("}", "}}"));
        }
        __result = result;
        return false;
    }

    private static bool GetMatchingIfExistsPrefix(TranslationService __instance, string key, object[] args, ref string __result)
    {
        EnsureLoaded(__instance);
        string validKey = KeyWithDomain(key);
        if (EntryCache(__instance).TryGetValue(validKey, out string value))
        {
            __result = Format(__instance, value, args);
            return false;
        }

        LangIndex index = IndexFor(__instance);
        int wildcard = index.FirstWildcard(validKey);
        if (wildcard >= 0)
        {
            __result = Format(__instance, index.WildcardValues[wildcard], args);
            return false;
        }
        int regex = index.FirstRegex(validKey);
        __result = regex >= 0 ? Format(__instance, index.Regexes[regex].Value, args) : null;
        return false;
    }

    // Vanilla adds from whichever thread asks, and the handbook asks from its page-loading
    // thread. This is the only place the set is added to once the prefix is in.
    private static bool AddNotFound(TranslationService service, string key)
    {
        HashSet<string> notFound = NotFound(service);
        lock (notFound) return notFound.Add(key);
    }

    // TranslationService.KeyWithDomain, which is private
    private static string KeyWithDomain(string key) => key.Contains(AssetLocation.LocationSeparator) ? key : "game:" + key;

    // ---- the index ----

    private sealed class LangIndex
    {
        public readonly Dictionary<string, string> WildcardSource;
        public readonly int WildcardVersion;
        public readonly Dictionary<string, KeyValuePair<Regex, string>> RegexSource;
        public readonly int RegexVersion;

        // Wildcard keys are prefixes ("game:item-plank-"); position = table order
        private readonly Dictionary<string, int> wildcardPosition = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> wildcardLookup;
        private readonly int[] wildcardLengths;
        public readonly string[] WildcardValues;

        // Regex keys bucketed by the literal text every match must start with
        private readonly Dictionary<string, int[]> regexesByPrefix = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>> regexLookup;
        private readonly int[] regexPrefixLengths;
        public readonly KeyValuePair<Regex, string>[] Regexes;

        public LangIndex(Dictionary<string, string> wildcards, Dictionary<string, KeyValuePair<Regex, string>> regexes)
        {
            WildcardSource = wildcards;
            WildcardVersion = DictVersion(wildcards);
            RegexSource = regexes;
            RegexVersion = DictVersion(regexes);

            WildcardValues = new string[wildcards.Count];
            int i = 0;
            foreach (var (prefix, value) in wildcards)
            {
                wildcardPosition[prefix] = i;   // keys are unique, so no position is overwritten
                WildcardValues[i++] = value;
            }
            wildcardLengths = wildcardPosition.Keys.Select(k => k.Length).Distinct().Order().ToArray();
            wildcardLookup = wildcardPosition.GetAlternateLookup<ReadOnlySpan<char>>();

            Regexes = new KeyValuePair<Regex, string>[regexes.Count];
            var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            i = 0;
            foreach (var (key, pair) in regexes)
            {
                string literal = LiteralPrefix(key);
                if (!buckets.TryGetValue(literal, out List<int> positions)) buckets[literal] = positions = [];
                positions.Add(i);
                Regexes[i++] = pair;
            }
            foreach (var (literal, positions) in buckets) regexesByPrefix[literal] = positions.ToArray();
            regexPrefixLengths = regexesByPrefix.Keys.Select(k => k.Length).Distinct().Order().ToArray();
            regexLookup = regexesByPrefix.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>Table position of the first wildcard key that prefixes <paramref name="key"/>, or -1.</summary>
        public int FirstWildcard(string key)
        {
            int first = -1;
            foreach (int length in wildcardLengths)
            {
                if (length > key.Length) break;
                if (wildcardLookup.TryGetValue(key.AsSpan(0, length), out int position) && (first < 0 || position < first)) first = position;
            }
            return first;
        }

        /// <summary>Table position of the first regex key matching <paramref name="key"/>, or -1.</summary>
        public int FirstRegex(string key)
        {
            List<int> candidates = null;
            foreach (int length in regexPrefixLengths)
            {
                if (length > key.Length) break;
                if (regexLookup.TryGetValue(key.AsSpan(0, length), out int[] positions)) (candidates ??= []).AddRange(positions);
            }
            if (candidates == null) return -1;

            candidates.Sort();
            foreach (int position in candidates)
            {
                if (Regexes[position].Key.IsMatch(key)) return position;
            }
            return -1;
        }

        /// <summary>
        /// The text any match of this key's regex must begin with. Vanilla builds the
        /// regex as "^" + key with each '*' turned into "(.*)", unescaped, so stop at
        /// the first character with regex meaning, and drop the one before a
        /// quantifier, which then becomes optional. An alternation anywhere can match
        /// without the prefix at all.
        /// </summary>
        private static string LiteralPrefix(string key)
        {
            if (key.Contains('|')) return "";
            int meta = key.AsSpan().IndexOfAny(@"\^$.?*+()[]{}");
            if (meta < 0) return key;
            return key[meta] is '?' or '+' or '{' ? key[..Math.Max(0, meta - 1)] : key[..meta];
        }
    }

    private static readonly ConditionalWeakTable<TranslationService, LangIndex> indexes = new();

    private static LangIndex IndexFor(TranslationService service)
    {
        Dictionary<string, string> wildcards = WildcardCache(service);
        Dictionary<string, KeyValuePair<Regex, string>> regexes = RegexCache(service);
        if (indexes.TryGetValue(service, out LangIndex index)
            && ReferenceEquals(index.WildcardSource, wildcards) && index.WildcardVersion == DictVersion(wildcards)
            && ReferenceEquals(index.RegexSource, regexes) && index.RegexVersion == DictVersion(regexes))
        {
            return index;
        }

        index = new LangIndex(wildcards, regexes);
        indexes.AddOrUpdate(service, index);
        return index;
    }

    // ---- private members of TranslationService and Dictionary ----

    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, string>> EntryCache =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, string>>("entryCache");
    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, string>> WildcardCache =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, string>>("wildcardCache");
    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, KeyValuePair<Regex, string>>> RegexCache =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, KeyValuePair<Regex, string>>>("regexCache");
    private static readonly AccessTools.FieldRef<TranslationService, HashSet<string>> NotFound =
        AccessTools.FieldRefAccess<TranslationService, HashSet<string>>("notFound");
    private static readonly AccessTools.FieldRef<TranslationService, ILogger> Logger =
        AccessTools.FieldRefAccess<TranslationService, ILogger>("logger");

    private static readonly Action<TranslationService> EnsureLoaded =
        AccessTools.MethodDelegate<Action<TranslationService>>(AccessTools.Method(typeof(TranslationService), "EnsureLoaded"));
    private static readonly System.Func<TranslationService, string, object[], string> Format =
        AccessTools.MethodDelegate<System.Func<TranslationService, string, object[], string>>(AccessTools.Method(typeof(TranslationService), "Format"));

    // Dictionary._version moves on every add, overwrite, remove and clear
    private static int DictVersion<TValue>(Dictionary<string, TValue> dictionary) => DictionaryVersion<TValue>.Get(dictionary);

    private static class DictionaryVersion<TValue>
    {
        public static readonly AccessTools.FieldRef<Dictionary<string, TValue>, int> Get =
            AccessTools.FieldRefAccess<Dictionary<string, TValue>, int>("_version");
    }
}
