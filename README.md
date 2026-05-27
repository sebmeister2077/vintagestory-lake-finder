# Vintage Story Lake Finder

A small server-side mod that scans already-generated map chunks around you, identifies contiguous bodies of water (lakes / "oceans"), and writes their approximate world coordinates to a file.

## Why this approach
Replicating Vintage Story's worldgen in standalone code (Python/C#) would require porting climate, landform and lake generators — they're tightly coupled to the engine and change between versions. Instead, this mod lets the game generate the world itself (cheap, deterministic from your seed + config) and just reads the result.

## Build
Requires .NET 10 SDK (matches VS 1.22.x).

```powershell
Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue   # if you ever set this
dotnet build -c Release
```

The build picks up `%APPDATA%\Vintagestory\*.dll` automatically via `$(VsDir)` in the csproj.

Copy `bin/Release/VintageStoryLakeFinder.dll` and `modinfo.json` into a folder next to each other, zip it, and drop the zip into `%APPDATA%\Vintagestory\Mods\`. Or symlink the build output folder into `Mods\` for dev.

## Use

### `/findoceans` — no generation needed (recommended)
Queries VS's internal ocean noise layer directly. The layer is a pure function of your world seed + config, so the entire world can be sampled in seconds without generating a single chunk or region.

```
/findoceans <halfSizeBlocks=50000> <minSizeBlocks=1000> <threshold=1> <writePng=true>
```

- `halfSizeBlocks`: scan a square of `2*halfSize` × `2*halfSize` blocks centered on you. 50000 = a 100k × 100k area.
- `minSizeBlocks`: ignore bodies whose approximate side length is smaller than this.
- `threshold`: ocean-noise value at/above which a pixel is considered ocean. Start with `1`; if results look wrong, look at the printed `[min, max]` range and the heatmap PNG to recalibrate.
- `writePng`: writes a heatmap PNG (blue = ocean, green = land) for visual verification.

Output: `%APPDATA%\Vintagestory\ModData\lakefinder\oceans_<timestamp>.{txt,json,png}` — ranked list of ocean bodies with centroid (world coords), bounding box, and approximate side length in blocks.

The first run, eyeball the PNG against `/wgen genmap ocean` in a known area to confirm the threshold is correct.

### `/findlakes` — block-accurate, requires pregen
The original command. Scans already-generated map chunks for surface water and flood-fills at block resolution. Use this to verify findings from `/findoceans` or to catch lakes that aren't represented in the ocean noise layer.

```
/wgen pregen rad 64
/findlakes <radiusChunks=64> <minSizeColumns=2000>
```

- `radiusChunks`: scan area in chunks. Must be ≤ pregen radius for complete coverage.
- `minSizeColumns`: minimum surface-water columns to count as a body. 2000 ≈ a 45×45 square.

## Workflow with `/wgen genmap ocean`
Once you have the centroid list, teleport to each one (`/tp =X =Z`) and run `/wgen genmap ocean` to map it out manually.
