# Handbook and Crafting Performance Fixes

Client-side fixes for a slow handbook and a laggy crafting grid in large packs. Each fix only applies when the mod
it works around is installed. The client log says what was done (`[handbookperf]`).
Client-only: servers do not need it.

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
rebuilds whenever a lang table is replaced or changed. If another mod already patches
either method, this fix stays off. Checked against vanilla on this pack's tables
(31k exact, 4k wildcard and 600 regex keys): identical results on 97k lookups.

## Attribute Rendering Library: handbook stalls on every open

ARL replaces the handbook's `FilterItems` with a copy of vanilla's that also hides pages
for items using its `AttributeRenderingLibrary.Handbook` behaviour. The copy
regex-scans every page's text each time it runs and reaches `CountMatches` through
reflection. Because it returns `false`, it also stops BetterHandbook's cached filter
from running.

When no collectible uses that exclusion, this mod removes ARL's prefix. If any does, it
leaves the prefix alone.

## Vanilla crafting grid: every slot change rescans thousands of recipes

Each change to a crafting grid slot makes `InventoryCraftingGrid.FindMatchingRecipe`
collect every grid recipe that a filled slot's stack is an ingredient of, deduplicating
with `List.AddIfNotPresent`, which is a linear scan. Planks, sticks and tools are
ingredients of thousands of recipes in a large pack (W4RD0's Furniture alone adds about
1,100, one per wood type), so each add scans thousands of entries, and the grid gets
slower with every slot filled.

This mod swaps that call for a hash set check. Recipes compare by reference, as before,
and the candidate list fills in the same order, so the same recipe is found. Lookups of
5 ms or more are logged with their candidate count. `HANDBOOKPERF_NOCRAFTFIX=1` turns the
fix off and leaves the timing on.

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.
