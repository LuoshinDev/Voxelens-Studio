using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZhuJieJing.App.BlockReplacement;

/// <summary>Picker-only similarity. These hints never decide which block states the replacement writes.</summary>
internal sealed record ReplacementBlockRelevance(string Kind, string Material, string Dye, string[] Words)
{
    private static readonly string[] Dyes = ["light_blue", "light_gray", "white", "orange", "magenta", "yellow", "lime", "pink", "gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly string[] Shapes = ["wall_hanging_sign", "hanging_sign", "wall_sign", "fence_gate", "pressure_plate", "glazed_terracotta", "concrete_powder", "stained_glass_pane", "stained_glass", "wall_banner", "wall_skull", "wall_head", "wall_torch", "trapdoor", "door", "stairs", "slab", "fence", "wall", "button", "leaves", "sapling", "planks", "log", "wood", "hyphae", "wool", "carpet", "concrete", "terracotta", "glass_pane", "glass", "banner", "bed", "skull", "head", "torch", "sign", "rail", "ore", "flower", "coral_fan", "coral", "shulker_box"];
    private static readonly string[] Materials = ["dark_oak", "pale_oak", "acacia", "birch", "spruce", "jungle", "mangrove", "cherry", "bamboo", "oak", "crimson", "warped", "red_sandstone", "sandstone", "deepslate", "blackstone", "end_stone", "nether_brick", "netherite", "quartz", "purpur", "prismarine", "granite", "diorite", "andesite", "tuff", "calcite", "amethyst", "obsidian", "basalt", "copper", "iron", "gold", "diamond", "emerald", "lapis", "redstone", "coal", "cobblestone", "stone", "brick", "bricks", "sand", "dirt", "snow", "ice"];

    internal static ReplacementBlockRelevance Describe(string id)
    {
        string path = id[(id.IndexOf(':') + 1)..];
        string dye = Dyes.FirstOrDefault(color => path.StartsWith(color + "_", StringComparison.Ordinal)) ?? "";
        // Red sandstone/redstone are materials, not dyed blocks.
        string kind = Shapes.FirstOrDefault(shape => path == shape || path.EndsWith("_" + shape, StringComparison.Ordinal)) ?? "";
        if(kind is not ("wool" or "carpet" or "concrete" or "concrete_powder" or "terracotta" or "glazed_terracotta" or "stained_glass" or "stained_glass_pane" or "banner" or "wall_banner" or "bed" or "shulker_box")) dye = "";
        string material = Materials.FirstOrDefault(value => HasWord(path, value)) ?? "";
        kind = kind switch
        {
            "stained_glass" => "glass", "glass_pane" or "stained_glass_pane" => "pane",
            "wood" or "hyphae" => "log", "wall_head" => "wall_skull", "head" => "skull",
            _ => kind,
        };
        if(kind.Length == 0)
        {
            kind = path switch
            {
                "grass_block" or "dirt" or "coarse_dirt" or "podzol" or "mycelium" or "rooted_dirt" or "mud" => "soil",
                "sand" or "red_sand" or "gravel" => "loose_ground",
                "water" or "lava" => "fluid",
                "ice" or "packed_ice" or "blue_ice" or "frosted_ice" => "ice",
                "poppy" or "dandelion" or "blue_orchid" or "allium" or "azure_bluet" or "oxeye_daisy" or "cornflower" or "lily_of_the_valley" => "flower",
                _ when path.EndsWith("_tulip", StringComparison.Ordinal) => "flower",
                _ when path.EndsWith("_block", StringComparison.Ordinal) && material is "iron" or "gold" or "diamond" or "emerald" or "lapis" or "redstone" or "coal" or "netherite" or "copper" => "mineral_block",
                _ when material is "stone" or "cobblestone" or "brick" or "bricks" or "sandstone" or "red_sandstone" or "deepslate" or "blackstone" or "end_stone" or "nether_brick" or "quartz" or "purpur" or "prismarine" or "granite" or "diorite" or "andesite" or "tuff" or "calcite" or "basalt" or "obsidian" => "masonry",
                _ => "",
            };
        }
        return new(kind, material, dye, path.Split('_').Where(w => w is not ("block" or "light" or "dark" or "wall" or "stripped" or "waxed" or "polished" or "cut" or "smooth")).ToArray());
    }

    internal double Score(ReplacementBlockRelevance other, Color? color, Color? otherColor)
    {
        double score = Kind.Length > 0 && Kind == other.Kind ? 120 : 0;
        if(Material.Length > 0 && Material == other.Material) score += 35;
        if(Dye.Length > 0 && Dye == other.Dye) score += 55;
        score += Math.Min(12, Words.Intersect(other.Words, StringComparer.Ordinal).Count() * 4);
        if(color is { } a && otherColor is { } b)
        {
            // Weighted RGB distance gives nearby texture colors a modest tie-break without outranking shape.
            double distance = Math.Sqrt(0.3 * Math.Pow(a.R - b.R, 2) + 0.59 * Math.Pow(a.G - b.G, 2) + 0.11 * Math.Pow(a.B - b.B, 2));
            score += 30 * Math.Max(0, 1 - distance / 140);
        }
        return score;
    }

    internal static Color? TextureColor(ImageSource image)
    {
        if(image is not BitmapSource bitmap) return null;
        BitmapSource pixels = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        byte[] data = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(data, stride, 0);
        double red = 0, green = 0, blue = 0, weight = 0;
        for(int i = 0; i < data.Length; i += 4)
        {
            double alpha = data[i + 3] / 255d;
            if(alpha < 0.1) continue;
            blue += data[i] * alpha;
            green += data[i + 1] * alpha;
            red += data[i + 2] * alpha;
            weight += alpha;
        }
        return weight > 0 ? Color.FromRgb((byte)(red / weight), (byte)(green / weight), (byte)(blue / weight)) : null;
    }

    private static bool HasWord(string path, string word) => ("_" + path + "_").Contains("_" + word + "_", StringComparison.Ordinal);
}
