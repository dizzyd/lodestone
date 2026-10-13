# Handbook and Crafting Performance Fixes

Fixes for a slow handbook and a laggy crafting grid in large packs. The fixes for other
mods only apply when the mod they work around is installed. The log says what was done
(`[handbookperf]`).

It is optional on both sides: players can join a server without it, and a server can run
it without players having it. The handbook fixes run on the client. The crafting grid fix
runs on whichever side has the mod, because the server repeats the same recipe lookup for
every player's crafting grid.

## A Culinary Artillery: handbook takes minutes to fill

ACA replaces `CookingRecipeIngredient.GetMatchingStack` with a prefix that allocates a
fresh ignored-attributes array on every call and tests every valid stack. While the handbook builds its pages, it
caches meal recipes by calling that method for every recipe, ingredient and handbook
stack. In a large pack that is enough garbage to keep the page-loading thread parked
for collections for most of a minute, and the handbook list stays empty until it is
done.

This mod swaps ACA's prefix for a copy with the same results that builds the array once
and indexes each ingredient's valid stacks by item. Items whose class overrides
`Equals` take the original full scan. `HANDBOOKPERF_NOINDEX=1` turns the index off.

That still leaves the handbook calling the matcher for every recipe, ingredient and
handbook stack: about 52 million calls in a pack with 866 meal recipes and 20,000
handbook stacks, nearly all of which find nothing. Vanilla's caching runs first, and
ACA's for the recipes vanilla does not know. While this mod's copy is the only patch on
the matcher, both loops walk only the stacks that could match each ingredient: those of
the same item as one of its valid stacks, those whose code a wildcard could match, and
those whose class overrides `Equals`. They are walked in the handbook's order, so the
same stacks are found in the same order. In that pack the handbook's first page build
after joining fell from 13 s to 5 s (58 s without this mod).

## Timing

Each world join logs how long the handbook took to build its pages and how much of that
was meal-recipe matching:

```
[handbookperf] GuiDialogSurvivalHandbook built its pages in 5.2 s, 4.4 s of it matching meal recipe ingredients (indexed)
```

## Vanilla lang lookups: every page build scans every wildcard key

A lang key with no exact entry, which describes most item description keys, makes
`TranslationService` test it against every wildcard key and then every multi-wildcard
key as a regex, in table order. `Lang.GetMatching` does that twice per miss. A large
pack has thousands of wildcard keys, and the handbook looks up a description for every
one of its ~20k pages.

This mod answers `HasTranslation` and `GetMatchingIfExists` from an index: wildcard keys
by prefix, and regex keys grouped by the literal text a match must begin with. Each key
keeps its table position, so the first match in table order still wins. The index
rebuilds whenever a lang table is replaced or changed. If another mod already rewrites
one of the two methods with a prefix or transpiler, that method is left to it. OptiTime's
transpiler on `HasTranslation` is the exception: it only makes the set of missing keys
safe to add to from the handbook's thread, and this mod's version does that itself.
Checked against vanilla on this pack's tables
(31k exact, 4k wildcard and 600 regex keys): identical results on 97k lookups.

## Vanilla handbook search: every open counts matches in every page

The handbook runs its search each time it opens or changes tab. With the search box
empty, vanilla's search pattern is an empty group, which matches at every character, so
it counts a match per character in every page's title and text only to show every page.
With 21,000 pages that took 0.7-0.8 s per open. An empty search shows every non-duplicate
page of the tab, in order, so this mod lists them directly: 3 ms. Searches with text, or
only spaces, run as before.

## Attribute Rendering Library: slower handbook search

ARL replaces the handbook's `FilterItems` with a copy of vanilla's that also hides pages
for items using its `AttributeRenderingLibrary.Handbook` behaviour. The copy looks that
behaviour up for every page and reaches `CountMatches` through reflection, which adds
25-30 ms per keystroke with 21,000 pages. Because it returns `false`, it also stops
BetterHandbook's cached filter from running.

When no collectible uses that exclusion, this mod removes ARL's prefix. If any does, it
leaves the prefix alone. It only does this for ARL 3.2.x, whose prefix has been checked
to add nothing else; for any other version it leaves the prefix and says so in the log.

While ARL's prefix is there, the empty-search listing above runs ahead of it and hides
the same pages ARL would: 17 ms per open instead of 740 ms.

## Vanilla crafting grid: every slot change rescans thousands of recipes

Each change to a crafting grid slot makes `InventoryCraftingGrid.FindMatchingRecipe`
collect every grid recipe that a filled slot's stack is an ingredient of, deduplicating
with `List.AddIfNotPresent`, which is a linear scan. Planks, sticks and tools are
ingredients of thousands of recipes in a large pack (W4RD0's Furniture alone adds about
1,100, one per wood type), so each add scans thousands of entries, and the grid gets
slower with every slot filled.

This mod swaps that call for a hash set check, on the client and on the server. Recipes compare by reference, as before,
and the candidate list fills in the same order, so the same recipe is found. It also
skips a shaped recipe without testing it when its ingredient count differs from the
number of filled slots, since a shaped recipe fills exactly the slots its pattern names.
Recipe classes from other mods, and shapeless recipes, are always tested. Lookups of
5 ms or more are logged with their candidate count. `HANDBOOKPERF_NOCRAFTFIX=1` turns the
fix off and leaves the timing on.

### With OptiTime

OptiTime replaces this lookup on the client with its own. Its version is ordered
differently from vanilla's, and it reuses the grid's last recipe before trying shaped
ones. So saw and log make planks, and adding a second log below them still gives planks
where vanilla gives a bowstave. On a server without OptiTime the server crafts the
bowstave, so the player sees one item and gets another.

When OptiTime has patched the lookup, this mod's lookup runs in its place and the log
says so. OptiTime's other optimizations, including its recipe matching, carry on. It
patches after OptiTime does, because OptiTime saves its recipe lookup as off in the
player's config when it finds another mod's patch already there. On the client that is
when the world finishes loading. A singleplayer server shares the client's patches.

Measured in-game on a pack of 24,870 grid recipes, filling recipes one ingredient at a
time, the mean lookup is 6.7 ms in vanilla, 3.5 ms with OptiTime and 1.8 ms with this
mod in OptiTime's place. Every one of 18,618 completed recipe grids picks the same
recipe as vanilla (`tests/CraftingGridBench.cs`, run with vstestkit's client tier).

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.
