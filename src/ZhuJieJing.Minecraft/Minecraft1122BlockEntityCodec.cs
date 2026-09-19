using System.Text.Json;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Target-aware codecs. Unsupported valuable data blocks export instead of becoming an empty container.</summary>
internal static class Minecraft1122BlockEntityCodec
{
    internal static bool SupportsBlockEntity(ushort block) => TargetType(block) is not null;

    internal static LegacyBlockEntityRewriteResult Rewrite(NormalizedMinecraftBlockEntity entity,
        LegacyBlockEncoding targetBlock, IMinecraftBlockDowngradeRules rules, int? sourceDataVersion = null)
    {
        entity = entity with { TypeId = NormalizeType(entity.TypeId) };
        string targetType = TargetType(targetBlock.NumericId) ?? throw new InvalidDataException(
            $"方块实体 {entity.TypeId} 位于 {entity.Position}，其目标方块 {targetBlock.NumericId}:{targetBlock.Metadata} 不支持方块实体；不能安全保留，已阻止导出。");
        bool sourceContainer = entity.TypeId is "minecraft:barrel" or "minecraft:chest" or "minecraft:trapped_chest" or
            "minecraft:hopper" or "minecraft:dispenser" or "minecraft:dropper" or "minecraft:furnace" or
            "minecraft:smoker" or "minecraft:blast_furnace" or "minecraft:brewing_stand" or "minecraft:shulker_box";
        if(entity.TypeId != targetType && !(sourceContainer && ContainerSlots(targetBlock.NumericId) > 0) &&
           !(targetType == "minecraft:sign" && entity.TypeId == "minecraft:hanging_sign"))
            throw new InvalidDataException($"方块实体 {entity.TypeId} 尚没有到 {targetType} 的语义转码器，已阻止导出：{entity.Position}。");
        LegacyBlockEntityRewriteResult rewritten = LegacyBlockEntityPayloadRewriter.Rewrite(entity with { TypeId = targetType });
        if(!rewritten.PreservedUnknownData && !entity.OriginalNbt.Bytes.Span.SequenceEqual(new byte[] { 0 }))
            throw new InvalidDataException($"方块实体 {entity.TypeId} 的完整数据不能安全读取，已阻止导出：{rewritten.ReductionReason}");
        if(!rewritten.PreservedUnknownData) rewritten = rewritten with { PreservedUnknownData = true, ReductionReason = null };
        Dictionary<string, NbtField> fields = NbtCompoundFields.Read(rewritten.CompoundPayload);
        bool modernItems = sourceDataVersion > 1343 || entity.TypeId is "minecraft:barrel" or "minecraft:smoker" or "minecraft:blast_furnace";
        if(modernItems && fields.TryGetValue("CustomName", out NbtField? customName))
            fields["CustomName"] = NbtCompoundFields.String(PlainText(customName.StringValue()));
        if(fields.TryGetValue("Items", out NbtField? items))
        {
            int slots = ContainerSlots(targetBlock.NumericId);
            if(slots == 0) throw new InvalidDataException($"目标 {targetType} 不能保留物品容器，已阻止导出：{entity.Position}。");
            HashSet<int> occupied = [];
            List<NbtField> converted = [];
            foreach(NbtField item in NbtCompoundFields.List(items))
            {
                Dictionary<string, NbtField> itemFields = item.Compound();
                int slot = Required(itemFields, "Slot").IntegerValue();
                if(slot < 0 || slot >= slots || !occupied.Add(slot))
                    throw new InvalidDataException($"容器 {entity.Position} 的物品槽 {slot} 不能放入目标 {slots} 格容器。");
                converted.Add(NbtCompoundFields.Compound(ConvertItem(itemFields, rules, entity.Position, modernItems)));
            }
            fields["Items"] = NbtCompoundFields.List(10, converted);
        }
        if(fields.ContainsKey("LootTable") && (modernItems || entity.TypeId != targetType))
            throw new InvalidDataException($"{entity.Position} 的战利品表需要跨版本转码，不能将其直接用于 {targetType}。");
        if(targetType == "minecraft:sign" && fields.TryGetValue("front_text", out NbtField? front))
        {
            Dictionary<string, NbtField> text = front.Compound();
            IReadOnlyList<NbtField> messages = NbtCompoundFields.List(Required(text, "messages"));
            if(messages.Count != 4) throw new InvalidDataException($"告示牌 {entity.Position} 必须包含四行文字。");
            for(int i = 0; i < 4; i++) fields[$"Text{i + 1}"] = NbtCompoundFields.String(messages[i].StringValue());
            if(fields.TryGetValue("back_text", out NbtField? back))
            {
                Dictionary<string, NbtField> backFields = back.Compound();
                if(backFields.TryGetValue("messages", out NbtField? backMessages) &&
                   NbtCompoundFields.List(backMessages).Any(message => PlainText(message.StringValue(), formatting: false).Length > 0))
                    throw new InvalidDataException($"告示牌 {entity.Position} 背面有文字；1.12.2 仅支持单面，已阻止静默丢失。");
            }
            fields.Remove("front_text");
            fields.Remove("back_text");
            fields.Remove("is_waxed");
        }
        return rewritten with { CompoundPayload = NbtCompoundFields.Write(fields) };
    }

    private static Dictionary<string, NbtField> ConvertItem(Dictionary<string, NbtField> fields,
        IMinecraftBlockDowngradeRules rules, BlockPosition position, bool modernItems)
    {
        string sourceId = Required(fields, "id").StringValue();
        string id = sourceId;
        int damage = fields.TryGetValue("Damage", out NbtField? existingDamage) ? existingDamage.IntegerValue() : 0;
        if(modernItems && TryMapFlattenedItem(id, out string? flattenedId, out int flattenedDamage))
        {
            id = flattenedId;
            damage = flattenedDamage;
        }
        else if(!Minecraft1122RuntimeRegistry.HasItem(id))
        {
            MinecraftBlockMapping mapping = rules.Resolve(new BlockState(id), MinecraftTargetProfile.Java1122);
            // Block material heuristics are not item codecs: e.g. a netherite sword must never
            // become an iron block merely because its name contains "netherite".
            if(mapping.Quality == MinecraftBlockMappingQuality.Exact &&
               mapping.LegacyEncoding is { } encoding)
            {
                string? legacyName = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(encoding.NumericId, encoding.Metadata).RegisteredName;
                string candidate = $"minecraft:{legacyName}";
                if(legacyName is not null && Minecraft1122RuntimeRegistry.HasItem(candidate))
                {
                    id = candidate;
                    damage = ItemVariantDamage(encoding);
                }
            }
            if(!Minecraft1122RuntimeRegistry.HasItem(id))
                throw new InvalidDataException($"容器 {position} 含无法转为 1.12.2 的物品 {sourceId}；已阻止导出，原物品保持不变。");
        }
        int count = fields.TryGetValue("count", out NbtField? modernCount)
            ? modernCount.IntegerValue() : Required(fields, "Count").IntegerValue();
        if(count is < 1 or > 127) throw new InvalidDataException($"物品 {sourceId} 数量 {count} 不能编码为 1.12.2 Count。");
        fields["id"] = NbtCompoundFields.String(id);
        fields["Count"] = NbtCompoundFields.Integer(count, 1);
        fields.Remove("count");
        Dictionary<string, NbtField> tag = fields.TryGetValue("tag", out NbtField? legacyTag)
            ? legacyTag.Compound() : new(StringComparer.Ordinal);
        if(tag.Remove("Enchantments", out NbtField? modernEnchantments))
            tag["ench"] = ConvertLegacyNamedEnchantments(modernEnchantments);
        if(tag.TryGetValue("StoredEnchantments", out NbtField? storedEnchantments))
            tag["StoredEnchantments"] = ConvertLegacyNamedEnchantments(storedEnchantments);
        if(modernItems)
        {
            if(tag.Remove("Damage", out NbtField? tagDamage)) damage = tagDamage.IntegerValue();
            if(tag.TryGetValue("display", out NbtField? displayTag))
            {
                Dictionary<string, NbtField> display = displayTag.Compound();
                if(display.TryGetValue("Name", out NbtField? name)) display["Name"] = NbtCompoundFields.String(PlainText(name.StringValue()));
                if(display.TryGetValue("Lore", out NbtField? lore)) display["Lore"] = NbtCompoundFields.List(8,
                    NbtCompoundFields.List(lore).Select(line => NbtCompoundFields.String(PlainText(line.StringValue()))).ToArray());
                tag["display"] = NbtCompoundFields.Compound(display);
            }
            if(tag.ContainsKey("BlockEntityTag") || tag.ContainsKey("EntityTag"))
                throw new InvalidDataException($"容器 {position} 的物品 {sourceId} 带有嵌套实体数据，尚不能保证跨版本语义，已阻止静默丢失。");
        }
        if(fields.TryGetValue("components", out NbtField? components))
        {
            foreach((string key, NbtField component) in components.Compound())
            {
                switch(key)
                {
                    case "minecraft:damage": damage = component.IntegerValue(); break;
                    case "minecraft:custom_name":
                    case "minecraft:item_name":
                        SetDisplay(tag, "Name", NbtCompoundFields.String(PlainText(component.StringValue())));
                        break;
                    case "minecraft:lore":
                        SetDisplay(tag, "Lore", NbtCompoundFields.List(8, NbtCompoundFields.List(component)
                            .Select(line => NbtCompoundFields.String(PlainText(line.StringValue()))).ToArray()));
                        break;
                    case "minecraft:unbreakable":
                        tag["Unbreakable"] = NbtCompoundFields.Integer(component.Type == 1 ? component.IntegerValue() : 1, 1);
                        ApplyTooltipVisibility(tag, component, 4);
                        break;
                    case "minecraft:enchantments":
                    case "minecraft:stored_enchantments":
                        tag[key == "minecraft:enchantments" ? "ench" : "StoredEnchantments"] = ConvertEnchantments(component);
                        ApplyTooltipVisibility(tag, component, key == "minecraft:enchantments" ? 1 : 32);
                        break;
                    case "minecraft:custom_data":
                        foreach((string name, NbtField value) in component.Compound())
                        {
                            if(!tag.TryAdd(name, value)) throw new InvalidDataException($"物品 {sourceId} 自定义数据与已转码字段 {name} 冲突。");
                        }
                        break;
                    default:
                        throw new InvalidDataException($"容器 {position} 的物品 {sourceId} 使用尚不能安全降级的组件 {key}；已阻止丢失组件的导出。");
                }
            }
            fields.Remove("components");
        }
        if(damage is < 0 or > short.MaxValue) throw new InvalidDataException($"物品 {sourceId} 的 Damage={damage} 超出旧版范围。");
        fields["Damage"] = NbtCompoundFields.Integer(damage, 2);
        if(tag.Count > 0) fields["tag"] = NbtCompoundFields.Compound(tag);
        return fields;
    }

    private static void SetDisplay(Dictionary<string, NbtField> tag, string name, NbtField value)
    {
        Dictionary<string, NbtField> display = tag.TryGetValue("display", out NbtField? existing)
            ? existing.Compound() : new(StringComparer.Ordinal);
        display[name] = value;
        tag["display"] = NbtCompoundFields.Compound(display);
    }

    private static void ApplyTooltipVisibility(Dictionary<string, NbtField> tag, NbtField component, int flag)
    {
        if(component.Type != 10) return;
        Dictionary<string, NbtField> fields = component.Compound();
        if(fields.TryGetValue("show_in_tooltip", out NbtField? show) && show.IntegerValue() == 0)
        {
            int existing = tag.TryGetValue("HideFlags", out NbtField? hide) ? hide.IntegerValue() : 0;
            tag["HideFlags"] = NbtCompoundFields.Integer(existing | flag);
        }
    }

    private static bool TryMapFlattenedItem(string source, out string id, out int damage)
    {
        if(FlattenedItems.TryGetValue(source, out var mapping))
        {
            (id, damage) = mapping;
            return true;
        }
        string[] colors = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
        for(int color = 0; color < colors.Length; color++)
        {
            if(source == $"minecraft:{colors[color]}_bed") { id = "minecraft:bed"; damage = color; return true; }
            if(source == $"minecraft:{colors[color]}_banner") { id = "minecraft:banner"; damage = 15 - color; return true; }
            if(source == $"minecraft:{colors[color]}_dye") { id = "minecraft:dye"; damage = 15 - color; return true; }
        }
        id = source;
        damage = 0;
        return false;
    }

    private static int ItemVariantDamage(LegacyBlockEncoding block) => block.NumericId switch
    {
        17 or 18 => block.Metadata & 3,
        161 or 162 => block.Metadata & 1,
        44 or 126 or 182 => block.Metadata & 7,
        1 or 3 or 5 or 6 or 12 or 19 or 24 or 31 or 35 or 38 or 95 or 97 or 98 or 139 or 155 or
            159 or 160 or 168 or 171 or 175 or 179 or 251 or 252 => block.Metadata,
        _ => 0, // Facing/open/power bits belong to placed blocks, not their item stacks.
    };

    private static readonly Dictionary<string, (string Id, int Damage)> FlattenedItems = new(StringComparer.Ordinal)
    {
        ["minecraft:melon"] = ("minecraft:melon_block", 0), ["minecraft:melon_slice"] = ("minecraft:melon", 0),
        ["minecraft:glistering_melon_slice"] = ("minecraft:speckled_melon", 0),
        ["minecraft:nether_brick"] = ("minecraft:netherbrick", 0),
        ["minecraft:nether_bricks"] = ("minecraft:nether_brick", 0),
        ["minecraft:sugar_cane"] = ("minecraft:reeds", 0), ["minecraft:popped_chorus_fruit"] = ("minecraft:chorus_fruit_popped", 0),
        ["minecraft:firework_rocket"] = ("minecraft:fireworks", 0), ["minecraft:firework_star"] = ("minecraft:firework_charge", 0),
        ["minecraft:oak_boat"] = ("minecraft:boat", 0), ["minecraft:oak_door"] = ("minecraft:wooden_door", 0),
        ["minecraft:oak_sign"] = ("minecraft:sign", 0),
        ["minecraft:cod"] = ("minecraft:fish", 0), ["minecraft:salmon"] = ("minecraft:fish", 1),
        ["minecraft:tropical_fish"] = ("minecraft:fish", 2), ["minecraft:pufferfish"] = ("minecraft:fish", 3),
        ["minecraft:cooked_cod"] = ("minecraft:cooked_fish", 0), ["minecraft:cooked_salmon"] = ("minecraft:cooked_fish", 1),
        ["minecraft:ink_sac"] = ("minecraft:dye", 0), ["minecraft:cocoa_beans"] = ("minecraft:dye", 3),
        ["minecraft:lapis_lazuli"] = ("minecraft:dye", 4), ["minecraft:bone_meal"] = ("minecraft:dye", 15),
        ["minecraft:skeleton_skull"] = ("minecraft:skull", 0), ["minecraft:wither_skeleton_skull"] = ("minecraft:skull", 1),
        ["minecraft:zombie_head"] = ("minecraft:skull", 2), ["minecraft:player_head"] = ("minecraft:skull", 3),
        ["minecraft:creeper_head"] = ("minecraft:skull", 4), ["minecraft:dragon_head"] = ("minecraft:skull", 5),
    };

    private static NbtField ConvertEnchantments(NbtField component)
    {
        Dictionary<string, NbtField> fields = component.Compound();
        Dictionary<string, NbtField> levels = fields.TryGetValue("levels", out NbtField? nested) ? nested.Compound() : fields;
        List<NbtField> entries = [];
        foreach((string name, NbtField value) in levels)
        {
            if(name == "show_in_tooltip") continue;
            if(!EnchantmentIds.TryGetValue(name, out int id)) throw new InvalidDataException($"附魔 {name} 不存在于 1.12.2，已阻止静默丢失。");
            int level = value.IntegerValue();
            if(level is < 1 or > short.MaxValue) throw new InvalidDataException($"附魔 {name} 等级无效。");
            entries.Add(NbtCompoundFields.Compound(new Dictionary<string, NbtField>
            {
                ["id"] = NbtCompoundFields.Integer(id, 2),
                ["lvl"] = NbtCompoundFields.Integer(level, 2),
            }));
        }
        return NbtCompoundFields.List(10, entries);
    }

    private static NbtField ConvertLegacyNamedEnchantments(NbtField enchantments)
    {
        List<NbtField> entries = [];
        foreach(NbtField entry in NbtCompoundFields.List(enchantments))
        {
            Dictionary<string, NbtField> fields = entry.Compound();
            NbtField id = Required(fields, "id");
            if(id.Type == 8)
            {
                string name = id.StringValue();
                if(!EnchantmentIds.TryGetValue(name, out int numeric))
                    throw new InvalidDataException($"附魔 {name} 不存在于 1.12.2，已阻止静默丢失。");
                fields["id"] = NbtCompoundFields.Integer(numeric, 2);
            }
            int level = Required(fields, "lvl").IntegerValue();
            if(level is < 1 or > short.MaxValue) throw new InvalidDataException("附魔等级超出 1.12.2 范围。");
            fields["lvl"] = NbtCompoundFields.Integer(level, 2);
            entries.Add(NbtCompoundFields.Compound(fields));
        }
        return NbtCompoundFields.List(10, entries);
    }

    private static string PlainText(string text, bool formatting = true)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return Read(document.RootElement, string.Empty);
        }
        catch(JsonException) { return text; }

        string Read(JsonElement element, string inheritedStyle)
        {
            if(element.ValueKind == JsonValueKind.String) return Decorate(element.GetString() ?? string.Empty, inheritedStyle);
            if(element.ValueKind == JsonValueKind.Array)
            {
                JsonElement[] children = element.EnumerateArray().ToArray();
                if(children.Length == 0) return string.Empty;
                string siblingStyle = StyleFor(children[0], inheritedStyle);
                return Read(children[0], inheritedStyle) + string.Concat(children.Skip(1).Select(child => Read(child, siblingStyle)));
            }
            if(element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("无法将物品文字组件转换为旧版名称。");
            foreach(JsonProperty property in element.EnumerateObject())
                if(property.Name is not ("text" or "extra" or "color" or "bold" or "italic" or "underlined" or "strikethrough" or "obfuscated"))
                    throw new InvalidDataException($"文字组件 {property.Name} 不能无损转换为旧版名称。");
            string style = StyleFor(element, inheritedStyle);
            string value = element.TryGetProperty("text", out JsonElement body) ? body.GetString() ?? string.Empty : string.Empty;
            value = Decorate(value, style);
            if(element.TryGetProperty("extra", out JsonElement extra)) value += Read(extra, style);
            return value;
        }

        string StyleFor(JsonElement element, string inheritedStyle)
        {
            if(element.ValueKind == JsonValueKind.Array)
                return element.GetArrayLength() > 0 ? StyleFor(element[0], inheritedStyle) : inheritedStyle;
            if(element.ValueKind != JsonValueKind.Object) return inheritedStyle;
            string style = inheritedStyle;
            if(element.TryGetProperty("color", out JsonElement color))
            {
                const string names = "black,dark_blue,dark_green,dark_aqua,dark_red,dark_purple,gold,gray,dark_gray,blue,green,aqua,red,light_purple,yellow,white";
                int index = Array.IndexOf(names.Split(','), color.GetString());
                if(color.GetString() == "reset") style = string.Empty;
                else if(index >= 0) style = "§" + "0123456789abcdef"[index] + string.Concat(style.Chunk(2).Where(pair => pair.Length == 2 && pair[1] >= 'k').Select(pair => new string(pair)));
                else throw new InvalidDataException("文字使用 1.12.2 无法保留的颜色。");
            }
            foreach((string name, char code) in new[] { ("bold", 'l'), ("italic", 'o'), ("underlined", 'n'), ("strikethrough", 'm'), ("obfuscated", 'k') })
                if(element.TryGetProperty(name, out JsonElement enabled))
                {
                    style = style.Replace("§" + code, string.Empty, StringComparison.Ordinal);
                    if(enabled.GetBoolean()) style += "§" + code;
                }
            return style;
        }

        string Decorate(string value, string style) => formatting && value.Length > 0 && style.Length > 0
            ? style + value + "§r" : value;
    }

    private static string NormalizeType(string type) => type switch
    {
        "Chest" => "minecraft:chest", "Furnace" => "minecraft:furnace", "Trap" => "minecraft:dispenser",
        "Dropper" => "minecraft:dropper", "Hopper" => "minecraft:hopper", "Cauldron" => "minecraft:brewing_stand",
        "Sign" => "minecraft:sign", "Music" => "minecraft:noteblock", "RecordPlayer" => "minecraft:jukebox",
        "MobSpawner" => "minecraft:mob_spawner", "EnchantTable" => "minecraft:enchanting_table",
        "EnderChest" => "minecraft:ender_chest", "Control" => "minecraft:command_block", "Beacon" => "minecraft:beacon",
        "Skull" => "minecraft:skull", "FlowerPot" => "minecraft:flower_pot", "Banner" => "minecraft:banner",
        "Piston" => "minecraft:piston", "Comparator" => "minecraft:comparator", "DLDetector" => "minecraft:daylight_detector",
        "Airportal" => "minecraft:end_portal", "EndGateway" => "minecraft:end_gateway", _ => type,
    };

    private static NbtField Required(IReadOnlyDictionary<string, NbtField> fields, string name) =>
        fields.TryGetValue(name, out NbtField? field) ? field : throw new InvalidDataException($"NBT 缺少必需字段 {name}。");

    private static int ContainerSlots(ushort block) => block switch
    {
        54 or 146 or >= 219 and <= 234 => 27,
        23 or 158 => 9,
        154 or 117 => 5,
        61 or 62 => 3,
        _ => 0,
    };

    private static string? TargetType(ushort block) => block switch
    {
        23 => "minecraft:dispenser", 25 => "minecraft:noteblock", 26 => "minecraft:bed",
        36 => "minecraft:piston", 52 => "minecraft:mob_spawner", 54 or 146 => "minecraft:chest",
        61 or 62 => "minecraft:furnace", 63 or 68 => "minecraft:sign", 84 => "minecraft:jukebox",
        116 => "minecraft:enchanting_table", 117 => "minecraft:brewing_stand", 119 => "minecraft:end_portal",
        130 => "minecraft:ender_chest", 137 or 210 or 211 => "minecraft:command_block", 138 => "minecraft:beacon",
        140 => "minecraft:flower_pot", 144 => "minecraft:skull", 149 or 150 => "minecraft:comparator",
        151 or 178 => "minecraft:daylight_detector", 154 => "minecraft:hopper", 158 => "minecraft:dropper",
        176 or 177 => "minecraft:banner", 209 => "minecraft:end_gateway", >= 219 and <= 234 => "minecraft:shulker_box",
        255 => "minecraft:structure_block", _ => null,
    };

    private static readonly Dictionary<string, int> EnchantmentIds = new(StringComparer.Ordinal)
    {
        ["minecraft:protection"] = 0, ["minecraft:fire_protection"] = 1, ["minecraft:feather_falling"] = 2,
        ["minecraft:blast_protection"] = 3, ["minecraft:projectile_protection"] = 4, ["minecraft:respiration"] = 5,
        ["minecraft:aqua_affinity"] = 6, ["minecraft:thorns"] = 7, ["minecraft:depth_strider"] = 8,
        ["minecraft:frost_walker"] = 9, ["minecraft:binding_curse"] = 10, ["minecraft:sharpness"] = 16,
        ["minecraft:smite"] = 17, ["minecraft:bane_of_arthropods"] = 18, ["minecraft:knockback"] = 19,
        ["minecraft:fire_aspect"] = 20, ["minecraft:looting"] = 21, ["minecraft:sweeping_edge"] = 22,
        ["minecraft:efficiency"] = 32, ["minecraft:silk_touch"] = 33, ["minecraft:unbreaking"] = 34,
        ["minecraft:fortune"] = 35, ["minecraft:power"] = 48, ["minecraft:punch"] = 49,
        ["minecraft:flame"] = 50, ["minecraft:infinity"] = 51, ["minecraft:luck_of_the_sea"] = 61,
        ["minecraft:lure"] = 62, ["minecraft:mending"] = 70, ["minecraft:vanishing_curse"] = 71,
    };

}
