# Adventure Static Data

`adventures-spawn.json` controls generated adventure tappables:

- `minCount` / `maxCount`: target number of adventure tappables to keep active.
- `minSpawnDelayMs` / `maxSpawnDelayMs`: random delay window before a replacement adventure can spawn.
- `minDurationMs` / `maxDurationMs`: random lifetime window for each spawned adventure.
- `chancePerSpawnCycle`: percentage chance, from `0` to `100`, that a spawn cycle creates an adventure.
- `crystalTypes[].folder`: folder containing the buildplate pool for that rarity.
- `crystalTypes[].icon`: catalog item id used as the map icon/crystal item.
- `crystalTypes[].rarity`: client-facing rarity value.
- `crystalTypes[].pickWeight`: non-negative relative weight used when selecting a crystal type.

Each rarity folder contains a `*-buildplates.json` file. Its `pickWeight` values are also non-negative relative weights used when choosing one buildplate inside that rarity.
