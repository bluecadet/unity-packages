---
title: Multiple content models
description: How to carry several record types in one version without giving up granular diffing or atomic swaps.
---

`IContentMapper<T>` is not limited to one model. When a version carries several
distinct record types — exhibits, sponsors, floor maps — the pattern is still
**one** `ContentManager<T>`, with `T` a shared base type, and one mapper whose
`Map` emits a single mixed, flat list of `ContentItem<Record>` covering every
model.

## Dispatch per file, not per parse call

Feeding every file into one `ContentJsonFiles.ParseItems` call merges their
elements into one token stream with nothing left to say which model a given token
came from. The mapper has to look at each file — by name or path — and route it to
a per-model helper before parsing it.

```csharp
abstract class Record { }
sealed class Exhibit : Record { public string title; public string imagePath; }
sealed class Sponsor : Record { public string name; }

class RecordMapper : IContentMapper<Record>
{
    public IReadOnlyList<ContentItem<Record>> Map(ContentSourceContext context)
    {
        var items = new List<ContentItem<Record>>();
        foreach (var file in context.Sources[0].Files)
        {
            if (file.EndsWith("exhibits.json", StringComparison.OrdinalIgnoreCase))
                items.AddRange(MapArray<Exhibit>(file, "exhibit"));
            else if (file.EndsWith("sponsors.json", StringComparison.OrdinalIgnoreCase))
                items.AddRange(MapArray<Sponsor>(file, "sponsor"));
        }
        return items;
    }

    static IEnumerable<ContentItem<Record>> MapArray<TModel>(string file, string idPrefix)
        where TModel : Record
    {
        foreach (JToken token in ContentJsonFiles.ParseItems(new[] { file }))
        {
            yield return new ContentItem<Record>
            {
                Id = $"{idPrefix}:{token["id"]}",
                ContentHash = ContentHashing.Hash(token),
                Data = token.ToObject<TModel>()
            };
        }
    }
}
```

## Namespace your ids

`ContentDiffer` throws on a duplicate or empty `Id` anywhere in the list, and that
list now spans every model. If the CMS hands out ids per collection — an exhibit
and a sponsor can both be `"9"` — prefix them per collection (`"exhibit:9"`,
`"sponsor:9"`) so they cannot collide once merged, as `MapArray` does above.

## Why a flat list of records, not one "bundle" item

`ContentDiffer` compares by `Id` plus `ContentHash`, one item at a time.
Per-record items give that comparison something to work with: this exhibit
changed, that sponsor was added, order held steady elsewhere — and unchanged
records land in `Diff.Unchanged` instead of being re-processed.

A single bundle item (`Id = "content"`, `Data` a wrapper object holding every
collection) collapses all of that into one verdict — "the one item changed" — on
every publish that touches anything at all. That forces a full rebuild and throws
away the diff machinery you are depending on.

The grouped, typed shape the app actually wants is easy to get back at the
consumer edge, in `OnVersionApplied`, *after* the diff has already run against the
flat list:

```csharp
manager.OnVersionApplied += version =>
{
    var exhibits = version.Items.Select(i => i.Data).OfType<Exhibit>();
    var sponsors = version.Items.Select(i => i.Data).OfType<Sponsor>();
};
```

That gets you both: granular change information out of `version.Diff`, and
ergonomic typed lists for views.

> [!WARNING]
> An app that always rebuilds everything on any change loses nothing with a bundle
> *today* — but it bakes that ceiling in. There is no way back to granular diffing
> later without changing the content shape.

## Singleton models

A global-config file is a JSON object, not an array, so
`ContentJsonFiles.ParseItems` deliberately skips it — there is no `"data"` array to
find elements in. Read it directly and emit exactly one item with a well-known,
fixed id:

```csharp
JObject config = JObject.Parse(File.ReadAllText(configFile));
items.Add(new ContentItem<Record>
{
    Id = "config:global",
    ContentHash = ContentHashing.Hash(config, "exportedAt"),
    Data = config.ToObject<GlobalConfig>()
});
```

A singleton is a collection of size one with a stable id. A hash change makes the
differ report it modified like any other record, and it rides the same atomic
version swap as everything else, so global config can never be out of sync with
the records it describes.

`ContentHashing.Hash(JToken, params string[] excludeTopLevelFields)` is what drops
volatile fields like `exportedAt` from the hash, so a re-export with no real change
does not read as changed.

## The atomicity payoff of one manager

Every model arrives as part of the same version, so there is one diff, one
swap-gate decision, one ack. A cross-model reference — an exhibit's sponsor id —
can never observe a mixed publish, because there is no window where one model has
swapped to the new version and another has not.

## When to use one manager per model instead

Only when the content streams are genuinely independent, on independent publish
schedules, and cross-model consistency does not matter.

Each `ContentManager<T>` then needs its own `IVersionFeed` — its own
`LaunchpadClient`, its own channel — because each one acks and gates its swap
independently. You give up the atomicity guarantee above in exchange for that
independence.
