using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using SkiaSharp;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VintageStoryLakeFinder
{
    /// <summary>
    /// "Fast" lake/ocean finder: queries VS's ocean noise layer directly without
    /// generating any chunks or map regions. The ocean layer is a pure function
    /// of the world seed + config, so we can sample arbitrarily large areas in seconds.
    /// </summary>
    public class FastLakeFinder
    {
        private readonly ICoreServerAPI sapi;

        public FastLakeFinder(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public void Register()
        {
            sapi.ChatCommands.Create("findoceans")
                .WithDescription("Locate oceans using VS's ocean noise layer (no chunk/region generation needed).")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(
                    sapi.ChatCommands.Parsers.OptionalInt("half-size-blocks", 50000),
                    sapi.ChatCommands.Parsers.OptionalInt("min-size-blocks", 1000),
                    sapi.ChatCommands.Parsers.OptionalInt("threshold", 1),
                    sapi.ChatCommands.Parsers.OptionalBool("write-png"))
                .HandleWith(Handle);
        }

        private TextCommandResult Handle(TextCommandCallingArgs args)
        {
            int halfSizeBlocks = (int)args[0];
            int minSizeBlocks = (int)args[1];
            int threshold = (int)args[2];
            bool writePng = args[3] == null ? true : (bool)args[3];

            var caller = args.Caller.Player as IServerPlayer;
            if (caller == null) return TextCommandResult.Error("Run this as a player.");

            var genMaps = sapi.ModLoader.GetModSystem<GenMaps>();
            if (genMaps == null) return TextCommandResult.Error("GenMaps mod system not loaded.");
            if (genMaps.oceanGen == null) return TextCommandResult.Error("GenMaps.oceanGen is null — world not yet initialized?");

            // RegionSize = blocks per region edge. noiseSizeOcean = ocean-noise pixels per region edge.
            int regionSize = sapi.WorldManager.RegionSize;
            int noiseSizeOcean = genMaps.noiseSizeOcean;
            if (noiseSizeOcean <= 0) return TextCommandResult.Error($"noiseSizeOcean = {noiseSizeOcean}");
            int blocksPerPixel = regionSize / noiseSizeOcean;

            var center = caller.Entity.Pos.AsBlockPos;

            int minBx = center.X - halfSizeBlocks;
            int minBz = center.Z - halfSizeBlocks;
            int maxBx = center.X + halfSizeBlocks;
            int maxBz = center.Z + halfSizeBlocks;

            // Convert to noise-pixel coords. Use floor division for negatives.
            int minPx = FloorDiv(minBx, blocksPerPixel);
            int minPz = FloorDiv(minBz, blocksPerPixel);
            int maxPx = FloorDiv(maxBx, blocksPerPixel);
            int maxPz = FloorDiv(maxBz, blocksPerPixel);
            int sizePx = maxPx - minPx + 1;
            int sizePz = maxPz - minPz + 1;

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[findoceans] regionSize={regionSize}, noiseSizeOcean={noiseSizeOcean}, blocksPerPixel={blocksPerPixel}",
                EnumChatType.Notification);
            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[findoceans] Sampling {sizePx}x{sizePz} ocean-noise pixels (~{sizePx * (long)sizePz} total) covering blocks ({minBx},{minBz})..({maxBx},{maxBz}).",
                EnumChatType.Notification);

            int[] map;
            try
            {
                map = genMaps.oceanGen.GenLayer(minPx, minPz, sizePx, sizePz);
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error($"GenLayer threw: {ex.Message}");
            }
            if (map == null || map.Length != sizePx * sizePz)
                return TextCommandResult.Error($"Unexpected map size: got {(map?.Length ?? -1)}, expected {sizePx * sizePz}");

            // Stats + threshold
            int waterPixels = 0;
            int minVal = int.MaxValue, maxVal = int.MinValue;
            var water = new bool[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                int v = map[i];
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
                if (v >= threshold) { water[i] = true; waterPixels++; }
            }

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[findoceans] Pixel value range: [{minVal}, {maxVal}]. Pixels >= threshold({threshold}): {waterPixels} of {map.Length}.",
                EnumChatType.Notification);

            // Convert min-size from blocks to pixels (area)
            double pixelArea = (double)blocksPerPixel * blocksPerPixel;
            int minPixelCount = Math.Max(1, (int)((double)minSizeBlocks * minSizeBlocks / pixelArea));

            var bodies = FloodFill(water, sizePx, sizePz, minPixelCount);
            bodies.Sort((a, b) => b.Size.CompareTo(a.Size));

            string folder = Path.Combine(sapi.GetOrCreateDataPath("ModData"), "lakefinder");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string txtPath = Path.Combine(folder, $"oceans_{stamp}.txt");
            string jsonPath = Path.Combine(folder, $"oceans_{stamp}.json");
            string pngPath = Path.Combine(folder, $"oceans_{stamp}.png");

            using (var sw = new StreamWriter(txtPath))
            {
                sw.WriteLine($"# Find Oceans (fast / noise-layer)");
                sw.WriteLine($"# Center: {center.X},{center.Z}  halfSize: {halfSizeBlocks}  minSize: {minSizeBlocks}  threshold: {threshold}");
                sw.WriteLine($"# regionSize={regionSize} noiseSizeOcean={noiseSizeOcean} blocksPerPixel={blocksPerPixel}");
                sw.WriteLine($"# Pixel range scanned: ({minPx},{minPz})..({maxPx},{maxPz})  size {sizePx}x{sizePz}");
                sw.WriteLine($"# Pixel value range: [{minVal}, {maxVal}]");
                sw.WriteLine($"# Found {bodies.Count} bodies >= {minPixelCount} pixels (~{minSizeBlocks}x{minSizeBlocks} blocks)");
                sw.WriteLine($"# rank\tsizePx\tsizeBlocksApprox\tcentroidX\tcentroidZ\tminX\tminZ\tmaxX\tmaxZ");
                for (int i = 0; i < bodies.Count; i++)
                {
                    var b = bodies[i];
                    PixelToWorld(b, minPx, minPz, blocksPerPixel,
                        out int cxw, out int czw,
                        out int minXw, out int minZw, out int maxXw, out int maxZw);
                    long approxArea = (long)b.Size * (long)pixelArea;
                    int approxSide = (int)Math.Sqrt(approxArea);
                    sw.WriteLine($"{i + 1}\t{b.Size}\t{approxSide}\t{cxw}\t{czw}\t{minXw}\t{minZw}\t{maxXw}\t{maxZw}");
                }
            }

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new
            {
                center = new { x = center.X, z = center.Z },
                halfSizeBlocks,
                minSizeBlocks,
                threshold,
                regionSize,
                noiseSizeOcean,
                blocksPerPixel,
                pixelRange = new { minPx, minPz, maxPx, maxPz, sizePx, sizePz },
                pixelValueRange = new { min = minVal, max = maxVal },
                bodies = bodies.Select((b, i) =>
                {
                    PixelToWorld(b, minPx, minPz, blocksPerPixel,
                        out int cxw, out int czw,
                        out int minXw, out int minZw, out int maxXw, out int maxZw);
                    long approxArea = (long)b.Size * (long)pixelArea;
                    return new
                    {
                        rank = i + 1,
                        sizePx = b.Size,
                        approxAreaBlocks = approxArea,
                        approxSideBlocks = (int)Math.Sqrt(approxArea),
                        centroidX = cxw,
                        centroidZ = czw,
                        minX = minXw,
                        minZ = minZw,
                        maxX = maxXw,
                        maxZ = maxZw
                    };
                }).ToArray()
            }, Formatting.Indented));

            if (writePng)
            {
                try { WriteHeatmap(map, sizePx, sizePz, threshold, pngPath); }
                catch (Exception ex)
                {
                    caller.SendMessage(GlobalConstants.GeneralChatGroup,
                        $"[findoceans] PNG write failed: {ex.Message}", EnumChatType.Notification);
                }
            }

            caller.SendMessage(GlobalConstants.GeneralChatGroup,
                $"[findoceans] Wrote {txtPath}. Found {bodies.Count} bodies. Top 10:",
                EnumChatType.Notification);

            int show = Math.Min(10, bodies.Count);
            for (int i = 0; i < show; i++)
            {
                var b = bodies[i];
                PixelToWorld(b, minPx, minPz, blocksPerPixel,
                    out int cxw, out int czw,
                    out int minXw, out int minZw, out int maxXw, out int maxZw);
                long approxArea = (long)b.Size * (long)pixelArea;
                int approxSide = (int)Math.Sqrt(approxArea);
                caller.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"  #{i + 1}  ~{approxSide}x{approxSide}b  centroid=({cxw}, {czw})  bbox=({minXw},{minZw})..({maxXw},{maxZw})",
                    EnumChatType.Notification);
            }

            return TextCommandResult.Success($"Wrote {bodies.Count} ocean bodies.");
        }

        private static int FloorDiv(int a, int b) => (a >= 0 || a % b == 0) ? a / b : a / b - 1;

        private static void PixelToWorld(WaterBody b, int minPx, int minPz, int blocksPerPixel,
            out int centroidX, out int centroidZ,
            out int minXw, out int minZw, out int maxXw, out int maxZw)
        {
            // Centroid in pixel coords -> block center of that pixel
            double cxPx = (double)b.SumX / b.Size;
            double czPx = (double)b.SumZ / b.Size;
            centroidX = (int)Math.Round((minPx + cxPx + 0.5) * blocksPerPixel);
            centroidZ = (int)Math.Round((minPz + czPx + 0.5) * blocksPerPixel);
            minXw = (minPx + b.MinX) * blocksPerPixel;
            minZw = (minPz + b.MinZ) * blocksPerPixel;
            maxXw = (minPx + b.MaxX + 1) * blocksPerPixel - 1;
            maxZw = (minPz + b.MaxZ + 1) * blocksPerPixel - 1;
        }

        private class WaterBody
        {
            public int Size;
            public long SumX, SumZ;
            public int MinX = int.MaxValue, MinZ = int.MaxValue;
            public int MaxX = int.MinValue, MaxZ = int.MinValue;
        }

        private static List<WaterBody> FloodFill(bool[] water, int width, int height, int minSize)
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

        private static void WriteHeatmap(int[] map, int w, int h, int threshold, string path)
        {
            // Find range
            int minV = int.MaxValue, maxV = int.MinValue;
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < minV) minV = map[i];
                if (map[i] > maxV) maxV = map[i];
            }
            int range = Math.Max(1, maxV - minV);

            byte[] pixels = new byte[w * h * 4]; // BGRA
            for (int i = 0; i < map.Length; i++)
            {
                int v = map[i];
                byte b, g, r, a;
                if (v >= threshold)
                {
                    // Ocean: blue, intensity by value above threshold
                    double t = (double)(v - threshold) / Math.Max(1, maxV - threshold);
                    r = 0;
                    g = (byte)(60 + 100 * t);
                    b = (byte)(160 + 90 * t);
                    a = 255;
                }
                else
                {
                    // Land: green/brown, intensity by value below threshold
                    double t = (double)(v - minV) / range;
                    r = (byte)(80 + 120 * t);
                    g = (byte)(100 + 120 * t);
                    b = (byte)(40 + 60 * t);
                    a = 0;
                }
                int p = i * 4;
                pixels[p + 0] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = a;
            }

            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bmp = new SKBitmap(info);
            Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);
        }
    }
}
