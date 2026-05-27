using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageStoryLakeFinder
{
    public class LakeFinderModSystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;

        // Chunks are 32 wide.
        private const int ChunkSize = 32;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            api.ChatCommands.Create("findlakes")
                .WithDescription("Scan generated map chunks around you for large water bodies (lakes/oceans).")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(
                    api.ChatCommands.Parsers.OptionalInt("radius-chunks", 64),
                    api.ChatCommands.Parsers.OptionalInt("min-size-columns", 2000))
                .HandleWith(OnFindLakes);

            new FastLakeFinder(api).Register();
        }

        private TextCommandResult OnFindLakes(TextCommandCallingArgs args)
        {
            int radiusChunks = (int)args[0];
            int minSize = (int)args[1];

            var caller = args.Caller.Player as IServerPlayer;
            if (caller == null) return TextCommandResult.Error("Run this as a player.");

            var center = caller.Entity.Pos.AsBlockPos;
            int ccx = center.X / ChunkSize;
            int ccz = center.Z / ChunkSize;

            int minCx = ccx - radiusChunks;
            int maxCx = ccx + radiusChunks;
            int minCz = ccz - radiusChunks;
            int maxCz = ccz + radiusChunks;

            int width = (maxCx - minCx + 1) * ChunkSize;
            int height = (maxCz - minCz + 1) * ChunkSize;
            int worldOriginX = minCx * ChunkSize;
            int worldOriginZ = minCz * ChunkSize;

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[lakefinder] Scanning {width}x{height} blocks around ({center.X},{center.Z})...",
                EnumChatType.Notification);

            // Bit grid: 1 = water surface column, 0 = not water / not generated.
            var water = new bool[width * height];
            int waterCols = 0;
            int missingChunks = 0;
            int scannedChunks = 0;

            var blockAccessor = sapi.World.BlockAccessor;
            int seaLevel = sapi.World.SeaLevel;

            for (int cx = minCx; cx <= maxCx; cx++)
            {
                for (int cz = minCz; cz <= maxCz; cz++)
                {
                    var mapChunk = sapi.WorldManager.GetMapChunk(cx, cz);
                    if (mapChunk == null) { missingChunks++; continue; }
                    scannedChunks++;

                    var heightMap = mapChunk.RainHeightMap; // ushort[1024]
                    int baseX = cx * ChunkSize;
                    int baseZ = cz * ChunkSize;

                    for (int lz = 0; lz < ChunkSize; lz++)
                    {
                        for (int lx = 0; lx < ChunkSize; lx++)
                        {
                            int y = heightMap[lz * ChunkSize + lx];
                            // Only candidate if at/below sea level (cheap filter)
                            if (y > seaLevel + 1) continue;

                            int wx = baseX + lx;
                            int wz = baseZ + lz;
                            var block = blockAccessor.GetBlock(new BlockPos(wx, y, wz, 0), BlockLayersAccess.Fluid);
                            if (block == null || block.Id == 0) continue;
                            if (block.LiquidCode != "water") continue;

                            int gx = wx - worldOriginX;
                            int gz = wz - worldOriginZ;
                            water[gz * width + gx] = true;
                            waterCols++;
                        }
                    }
                }
            }

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[lakefinder] Generated chunks scanned: {scannedChunks}, missing/ungenerated: {missingChunks}, water columns: {waterCols}. Flood-filling...",
                EnumChatType.Notification);

            var bodies = FloodFill(water, width, height, minSize);

            // Sort largest first
            bodies.Sort((a, b) => b.Size.CompareTo(a.Size));

            string folder = Path.Combine(sapi.GetOrCreateDataPath("ModData"), "lakefinder");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string txtPath = Path.Combine(folder, $"lakes_{stamp}.txt");
            string jsonPath = Path.Combine(folder, $"lakes_{stamp}.json");

            using (var sw = new StreamWriter(txtPath))
            {
                sw.WriteLine($"# Lake Finder results");
                sw.WriteLine($"# Center: {center.X},{center.Z}  Radius (chunks): {radiusChunks}  MinSize: {minSize}");
                sw.WriteLine($"# Scanned chunks: {scannedChunks}  Missing: {missingChunks}");
                sw.WriteLine($"# Found {bodies.Count} water bodies >= {minSize} columns");
                sw.WriteLine($"# rank\tsize\tcentroidX\tcentroidZ\tminX\tminZ\tmaxX\tmaxZ");
                int rank = 1;
                foreach (var b in bodies)
                {
                    int cxw = (int)(b.SumX / b.Size) + worldOriginX;
                    int czw = (int)(b.SumZ / b.Size) + worldOriginZ;
                    int minXw = b.MinX + worldOriginX;
                    int minZw = b.MinZ + worldOriginZ;
                    int maxXw = b.MaxX + worldOriginX;
                    int maxZw = b.MaxZ + worldOriginZ;
                    sw.WriteLine($"{rank++}\t{b.Size}\t{cxw}\t{czw}\t{minXw}\t{minZw}\t{maxXw}\t{maxZw}");
                }
            }

            var jsonOut = new
            {
                center = new { x = center.X, z = center.Z },
                radiusChunks,
                minSize,
                scannedChunks,
                missingChunks,
                bodies = bodies.Select((b, i) => new
                {
                    rank = i + 1,
                    size = b.Size,
                    centroidX = (int)(b.SumX / b.Size) + worldOriginX,
                    centroidZ = (int)(b.SumZ / b.Size) + worldOriginZ,
                    minX = b.MinX + worldOriginX,
                    minZ = b.MinZ + worldOriginZ,
                    maxX = b.MaxX + worldOriginX,
                    maxZ = b.MaxZ + worldOriginZ
                }).ToArray()
            };
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(jsonOut, Formatting.Indented));

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[lakefinder] Found {bodies.Count} bodies. Top 10:",
                EnumChatType.Notification);

            int show = Math.Min(10, bodies.Count);
            for (int i = 0; i < show; i++)
            {
                var b = bodies[i];
                int cxw = (int)(b.SumX / b.Size) + worldOriginX;
                int czw = (int)(b.SumZ / b.Size) + worldOriginZ;
                caller.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"  #{i + 1}  size={b.Size}  centroid=({cxw}, {czw})  bbox=({b.MinX + worldOriginX},{b.MinZ + worldOriginZ})..({b.MaxX + worldOriginX},{b.MaxZ + worldOriginZ})",
                    EnumChatType.Notification);
            }

            return TextCommandResult.Success($"Wrote results to {txtPath}");
        }

        private class WaterBody
        {
            public int Size;
            public long SumX, SumZ;
            public int MinX = int.MaxValue, MinZ = int.MaxValue;
            public int MaxX = int.MinValue, MaxZ = int.MinValue;
        }

        // 4-connected flood fill on the grid; returns bodies whose size >= minSize.
        private List<WaterBody> FloodFill(bool[] water, int width, int height, int minSize)
        {
            var result = new List<WaterBody>();
            var visited = new bool[water.Length];
            var stack = new Stack<int>();

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = z * width + x;
                    if (!water[idx] || visited[idx]) continue;

                    var body = new WaterBody();
                    stack.Push(idx);
                    visited[idx] = true;

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        int cx = cur % width;
                        int cz = cur / width;

                        body.Size++;
                        body.SumX += cx;
                        body.SumZ += cz;
                        if (cx < body.MinX) body.MinX = cx;
                        if (cz < body.MinZ) body.MinZ = cz;
                        if (cx > body.MaxX) body.MaxX = cx;
                        if (cz > body.MaxZ) body.MaxZ = cz;

                        TryPush(stack, water, visited, width, height, cx - 1, cz);
                        TryPush(stack, water, visited, width, height, cx + 1, cz);
                        TryPush(stack, water, visited, width, height, cx, cz - 1);
                        TryPush(stack, water, visited, width, height, cx, cz + 1);
                    }

                    if (body.Size >= minSize) result.Add(body);
                }
            }

            return result;
        }

        private static void TryPush(Stack<int> stack, bool[] water, bool[] visited, int w, int h, int x, int z)
        {
            if (x < 0 || z < 0 || x >= w || z >= h) return;
            int i = z * w + x;
            if (!water[i] || visited[i]) return;
            visited[i] = true;
            stack.Push(i);
        }
    }
}
