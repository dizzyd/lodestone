# Lodestone

Lodestone makes large modpacks faster. It speeds up the handbook, lang lookups and the
crafting grid, which all get slower as a pack grows, and every fix gives the same results
as the unmodified game. It adds no blocks, items or recipes. The fixes for other mods
apply only when the mod they work around is installed. The log, under `[lodestone]`,
says what was done.

A server that runs Lodestone requires it of its players, and the game offers it to them
when they join. Most of the fixes are in the handbook and lang lookups, which run only on
the client. A player can still use Lodestone on a server without it. Because every fix
gives vanilla's results, a client and server that differ in having Lodestone still agree
on every result. The crafting grid fix runs on whichever side has the mod, because the
server repeats the recipe lookup for every player's crafting grid.

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

## A Culinary Artillery meal matching

ACA replaces `CookingRecipeIngredient.GetMatchingStack` with a prefix that allocates a
new ignored-attributes array on every call and tests every valid stack. While the
handbook builds its pages, it caches meal recipes by calling that method for every
recipe, ingredient and handbook stack. In a large pack the resulting garbage collection
holds up the page-loading thread for most of a minute, and the handbook list stays empty
until it finishes.

Lodestone replaces ACA's prefix with a copy that gives the same results, builds the array
once, and indexes each ingredient's valid stacks by item. Items whose class overrides
`Equals` use the original full scan.

The handbook still calls the matcher for every recipe, ingredient and handbook stack. In
a pack with 866 meal recipes and 20,000 handbook stacks that is about 52 million calls,
and nearly all of them find nothing. Vanilla's caching runs first, and ACA's runs for the
recipes vanilla does not know. While Lodestone's copy is the only patch on the matcher,
both loops test each ingredient only against the stacks that could match it: stacks of
the same item as one of its valid stacks, stacks whose code one of its wildcards could
match, and stacks whose class overrides `Equals`. The stacks are tested in the handbook's
order, so the same stacks are found in the same order. In that pack the handbook's first
page build after joining went from 13 s to 5 s. Without Lodestone it took 58 s.

## Timing

Each world join logs how long the handbook took to build its pages, and how much of that
time was meal-recipe matching:

```
[lodestone] GuiDialogSurvivalHandbook built its pages in 5.2 s, 4.4 s of it matching meal recipe ingredients (indexed)
```

## Lang lookups

When a lang key has no exact entry, `TranslationService` tests it against every wildcard
key, and then against every multi-wildcard key as a regex, in table order. Most item
description keys have no exact entry, and `Lang.GetMatching` does the search twice for
each of them. A large pack has thousands of wildcard keys, and the handbook looks up a
description for each of its 20,000 or so pages.

Lodestone answers `HasTranslation` and `GetMatchingIfExists` from an index. Wildcard keys
are indexed by prefix, and regex keys are grouped by the literal text a match must start
with. Each key keeps its table position, so the first match in table order still wins.
The index is rebuilt whenever a lang table is replaced or changed.

If another mod already rewrites one of the two methods with a prefix or transpiler,
Lodestone leaves that method alone. OptiTime's transpiler on `HasTranslation` is the one
exception. It makes the set of missing keys safe to add to from the handbook's thread,
and Lodestone's version does the same.

On a pack with 31,000 exact, 4,000 wildcard and 600 regex keys, the index gave the same
result as vanilla for all of 97,000 lookups. In a test pack with 21,085 handbook pages,
looking up the name, description and handbook text of every item, 95,880 keys in all,
took 47 to 55 s in vanilla and 1.1 to 1.2 s with Lodestone, with the same answer for
every key.

## Handbook search

The handbook runs its search each time it opens or changes tab. With the search box
empty, vanilla's search pattern is an empty group, which matches at every character. It
counts those matches in every page's title and text, and then shows every page. With
21,000 pages this took 720 to 850 ms each time the handbook opened.

An empty search shows every non-duplicate page of the current tab, in order, so Lodestone
builds that list directly, in 3 to 4 ms. Searches with text, or with only spaces, run as
before.

## Attribute Rendering Library

ARL replaces the handbook's `FilterItems` with a copy of vanilla's that also hides pages
for items using its `AttributeRenderingLibrary.Handbook` behaviour. The copy looks that
behaviour up for every page and calls `CountMatches` through reflection, which adds 25 to
30 ms per keystroke with 21,000 pages. Because it returns `false`, it also stops
BetterHandbook's cached filter from running.

When no collectible uses that exclusion, Lodestone removes ARL's prefix. If any does, it
leaves the prefix alone. Lodestone does this only for ARL 3.2.x, whose prefix has been
checked to add nothing else. For any other version it leaves the prefix in place and
says so in the log.

While ARL's prefix is in place, the empty-search listing above runs ahead of it and hides
the same pages ARL would. Opening the handbook then takes 17 ms instead of 740 ms.

## Crafting grid

Each change to a crafting grid slot makes `InventoryCraftingGrid.FindMatchingRecipe`
collect every grid recipe that uses a filled slot's stack as an ingredient. It
deduplicates them with `List.AddIfNotPresent`, which is a linear scan. In a large pack,
planks, sticks and tools are ingredients of thousands of recipes, and W4RD0's Furniture
alone adds about 1,100, one per wood type. Each add then scans thousands of entries, and
the lookup gets slower with every slot filled.

For the player's 3x3 grid, Lodestone finds the recipe without building that list, on both
the client and the server. Vanilla's answer is the first recipe in its list, with shaped
recipes tried before shapeless ones, that matches the grid. Recipes that cannot match can
therefore be skipped, as long as the rest are tried in vanilla's order.

- Each slot's stack is tested only against the ingredient keys it could satisfy: keys for
  the same item, and wildcard keys whose fixed prefix its code starts with.
- A shaped recipe matches only a grid that is filled exactly where its pattern has
  ingredients, so shaped recipes are looked up by the shape of the filled cells. Where it
  is safe, a recipe is also skipped unless every filled slot's stack is one of its
  ingredients.
- The remaining recipes are tried in the order vanilla's list would hold them.

Shapeless recipes, and recipe or ingredient classes from other mods, are tried in the
same order without those filters. Other grid sizes, and a slot holding an empty stack, go
through vanilla's own loop. Lodestone speeds that loop up too, with a hash set in place of
`AddIfNotPresent`, and by not calling `Matches` on a shaped recipe whose ingredient count
differs from the number of filled slots. Lookups that take 5 ms or more are logged.

In-game, on a pack of 24,870 grid recipes, filling recipes one ingredient at a time, the
mean lookup took 6 to 8 ms in vanilla and 0.12 to 0.19 ms with Lodestone. The slowest grid
tested went from 41 ms to 1.5 ms. All 18,618 completed recipe grids gave the same recipe
as vanilla. The measurements come from `tests/CraftingGridBench.cs`, run with vstestkit's
client tier.

### With OptiTime

OptiTime replaces this lookup on the client with its own. Its version orders recipes
differently from vanilla's, and it reuses the grid's last recipe before trying shaped
ones. For example, a saw and a log make planks, and adding a second log below them still
gives planks, where vanilla gives a bowstave. A server without OptiTime crafts the
bowstave, so the player sees one item and receives another.

When OptiTime has patched the lookup, Lodestone's lookup runs in its place, and the log
says so. OptiTime's other optimizations, including its recipe matching, are unaffected.
Lodestone patches after OptiTime does, because OptiTime saves its recipe lookup as off in
the player's config when it finds another mod's patch already in place. On the client
this happens when the world finishes loading. A singleplayer server shares the client's
patches.

With OptiTime installed, the mean lookup took 3.4 ms with OptiTime's version and 0.12 ms
with Lodestone's in its place.

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip is written to `Releases/`.

## License

MIT. See [LICENSE](LICENSE).
