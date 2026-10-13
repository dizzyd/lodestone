# Lodestone

Optimizations for large modpacks. Lodestone speeds up the handbook, lang lookups and the
crafting grid, which all get slower as a pack grows, and every fix gives the same results
as vanilla. The fixes for other mods only apply when the mod they work around is
installed. The log says what was done (`[lodestone]`).

It is optional on both sides: players can join a server without it, and a server can run
it without players having it. The handbook fixes run on the client. The crafting grid fix
runs on whichever side has the mod, because the server repeats the same recipe lookup for
every player's crafting grid.

## Settings

`ModConfig/lodestone.json` turns each fix off on its own. Every setting is on by
default, and a change takes effect the next time a world loads.

| Setting | What it turns off |
|---|---|
| `LangIndex` | the lang lookup index |
| `AcaMatcher` | the indexed copy of ACA's matcher, and the narrowed meal matching with it |
| `CraftingGrid` | the crafting grid fix, on whichever side reads the file |
| `CraftingGridOverOptiTime` | running the crafting grid lookup in place of OptiTime's; off leaves it to OptiTime on the client |
| `EmptyHandbookSearch` | listing the handbook's pages directly when the search box is empty |
| `RemoveArlFilter` | removing ARL's handbook search patch |
| `LogTimings` | the handbook build time and slow crafting lookups in the log |

## A Culinary Artillery: handbook takes minutes to fill

ACA replaces `CookingRecipeIngredient.GetMatchingStack` with a prefix that allocates a
fresh ignored-attributes array on every call and tests every valid stack. While the handbook builds its pages, it
caches meal recipes by calling that method for every recipe, ingredient and handbook
stack. In a large pack that is enough garbage to keep the page-loading thread parked
for collections for most of a minute, and the handbook list stays empty until it is
done.

This mod swaps ACA's prefix for a copy with the same results that builds the array once
and indexes each ingredient's valid stacks by item. Items whose class overrides
`Equals` take the original full scan.

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
[lodestone] GuiDialogSurvivalHandbook built its pages in 5.2 s, 4.4 s of it matching meal recipe ingredients (indexed)
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

For the player's 3x3 grid this mod finds the recipe without building that list, on the
client and on the server. Vanilla's answer is the first recipe in its list, shaped ones
first, that matches the grid, so any recipe that cannot match may be left out as long as
the rest are tried in vanilla's order:

- Each slot's stack is tested only against the ingredient keys it could satisfy: those
  of the same item, and wildcard keys whose fixed prefix its code starts with.
- A shaped recipe matches only a grid filled exactly where its pattern has ingredients,
  so shaped recipes are looked up by the shape of the filled cells. Where it is safe to,
  a recipe is also dropped unless every filled slot's stack is one of its ingredients.
- The remaining recipes are tried in the order vanilla's list would hold them.

Shapeless recipes, and recipe or ingredient classes from other mods, are tried without
those filters, in the same order. Other grid sizes, and a slot holding an empty stack, go
through vanilla's own loop, which this mod still speeds up: a hash set instead of
`AddIfNotPresent`, and no `Matches` call on a shaped recipe whose ingredient count differs
from the filled slots. Lookups of 5 ms or more are logged.

Measured in-game on a pack of 24,870 grid recipes, filling recipes one ingredient at a
time, the mean lookup is 6-8 ms in vanilla and 0.12-0.19 ms with this mod, and the
slowest grid tested fell from 41 ms to 1.5 ms. Every one of 18,618 completed recipe
grids gives the same recipe as vanilla (`tests/CraftingGridBench.cs`, run with
vstestkit's client tier).

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

With OptiTime installed, the mean lookup measured 3.4 ms with OptiTime's and 0.12 ms
with this mod's in its place.

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.
