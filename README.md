# Active Maps Framework

RimWorld 1.6 mod / framework (`crystallize.activemaps`) by **0Crystallize**.

Keeps world maps loaded without a scout pawn — for long-range fire (Rimatomics soft bridge), outpost presence, and strike aftermath (salvage, virtual garrison, structure sketch).

## Install

Copy this folder into `RimWorld/Mods/` (or symlink). Requires Harmony. Soft Rimatomics bridge — load **before** Rimatomics.

## Build

Needs a RimWorld install + Harmony mod (`brrainz.harmony`). No machine-specific paths in the csproj.

```bat
dotnet build Source\CrystallizeActiveMaps.csproj -c Release
```

If Steam is not in the default location:

```bat
dotnet build Source\CrystallizeActiveMaps.csproj -c Release -p:RimWorldDir="D:\Games\RimWorld"
```

Or copy `Source\Directory.Build.props.user.example` → `Source\Directory.Build.props.user` and set `RimWorldDir` there (gitignored).

Output: `1.6/Assemblies/CrystallizeActiveMaps.dll`

## API (mod authors)

See `Source/ActiveMapsApi.cs` — pin / unpin / EnsureLoadedMap / TryUnload, stash hooks, raid sketch providers, quest denylist, StrikeMapClass.

## Version

`1.0.0`
