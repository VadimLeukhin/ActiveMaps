# Active Maps Framework

RimWorld 1.6 mod / framework (`crystallize.activemaps`) by **0Crystallize**.

Keeps world maps loaded without a scout pawn — for long-range fire (Rimatomics soft bridge), outpost presence, and strike aftermath (salvage, virtual garrison, structure sketch).

## Install

Copy this folder into `RimWorld/Mods/` (or symlink). Requires Harmony. Soft Rimatomics bridge — load **before** Rimatomics.

## Build

```bat
dotnet build Source\CrystallizeActiveMaps.csproj -c Release
```

Output: `1.6/Assemblies/CrystallizeActiveMaps.dll`

Hint paths in the csproj point at a local RimWorld install + Harmony. Adjust if your paths differ.

## API (mod authors)

See `Source/ActiveMapsApi.cs` — pin / unpin / EnsureLoadedMap / TryUnload, stash hooks, raid sketch providers, quest denylist, StrikeMapClass.

## Version

`1.0.0`
