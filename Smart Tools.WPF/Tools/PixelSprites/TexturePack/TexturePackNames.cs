using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The Minecraft Java Edition versions we can write a pack.mcmeta for (numbers from the Minecraft Wiki, "Pack format"),
    /// and a starter list of vanilla texture names so a dropped "stone.png" lands in block/stone.
    /// The list is not every texture in the game: any "folder/name" can still be typed in by hand.
    /// </summary>
    public static partial class TexturePackManager
    {
        // Label, resource pack format, UsesMinMax (1.21.9 and newer write min_format/max_format instead of pack_format).
        public static readonly IReadOnlyList<PackVersion> Versions = new PackVersion[]
        {
            new("1.21.11", 75, true),
            new("1.21.9 - 1.21.10", 69, true),
            new("1.21.7 - 1.21.8", 64, false),
            new("1.21.6", 63, false),
            new("1.21.5", 55, false),
            new("1.21.4", 46, false),
            new("1.21.2 - 1.21.3", 42, false),
            new("1.21 - 1.21.1", 34, false),
            new("1.20.5 - 1.20.6", 32, false),
            new("1.20.3 - 1.20.4", 22, false),
            new("1.20.2", 18, false),
            new("1.20 - 1.20.1", 15, false),
            new("1.19.4", 13, false),
            new("1.19.3", 12, false),
            new("1.19 - 1.19.2", 9, false),
            new("1.18 - 1.18.2", 8, false),
            new("1.17 - 1.17.1", 7, false),
            new("1.16.2 - 1.16.5", 6, false),
            new("1.15 - 1.16.1", 5, false),
            new("1.13 - 1.14.4", 4, false),
        };

        /// <summary>Folders under assets/minecraft/textures that a dropped file's parent folder or "block_" prefix can name.</summary>
        public static readonly IReadOnlySet<string> TextureFolders = new HashSet<string>(StringComparer.Ordinal)
        {
            "block", "item", "entity", "gui", "environment", "particle", "painting", "misc", "colormap", "mob_effect", "models",
        };

        private static readonly string[] Colors =
        {
            "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
            "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black",
        };

        private static readonly string[] ToolMaterials = { "wooden", "stone", "iron", "golden", "diamond", "netherite" };
        private static readonly string[] ArmorMaterials = { "leather", "chainmail", "iron", "golden", "diamond", "netherite" };
        private static readonly string[] Woods = { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry" };

        private const string BlockNames =
            "stone granite polished_granite diorite polished_diorite andesite polished_andesite cobblestone mossy_cobblestone " +
            "dirt coarse_dirt podzol_top podzol_side grass_block_top grass_block_side grass_block_snow mycelium_top mycelium_side " +
            "sand red_sand gravel bedrock clay snow ice packed_ice blue_ice sponge wet_sponge " +
            "sandstone sandstone_top sandstone_bottom red_sandstone red_sandstone_top red_sandstone_bottom glass " +
            "coal_ore iron_ore gold_ore copper_ore diamond_ore emerald_ore lapis_ore redstone_ore nether_gold_ore nether_quartz_ore " +
            "deepslate deepslate_coal_ore deepslate_iron_ore deepslate_gold_ore deepslate_copper_ore deepslate_diamond_ore deepslate_emerald_ore deepslate_lapis_ore deepslate_redstone_ore " +
            "coal_block iron_block gold_block diamond_block emerald_block lapis_block redstone_block copper_block netherite_block quartz_block_side " +
            "netherrack nether_bricks red_nether_bricks soul_sand soul_soil glowstone obsidian crying_obsidian basalt_side basalt_top blackstone " +
            "end_stone end_stone_bricks purpur_block bricks stone_bricks mossy_stone_bricks cracked_stone_bricks chiseled_stone_bricks " +
            "bookshelf tnt_side tnt_top tnt_bottom crafting_table_top crafting_table_side crafting_table_front " +
            "furnace_front furnace_front_on furnace_side furnace_top smoker_front blast_furnace_front " +
            "pumpkin_side pumpkin_top carved_pumpkin jack_o_lantern melon_side melon_top hay_block_side hay_block_top " +
            "cactus_side cactus_top cactus_bottom bamboo_stalk sugar_cane oak_sapling spruce_sapling birch_sapling dandelion poppy " +
            "torch redstone_torch lantern ladder rail rail_corner powered_rail activator_rail detector_rail " +
            "water_still water_flow lava_still lava_flow fire_0 fire_1 farmland farmland_moist dirt_path_top dirt_path_side " +
            "oak_door_top oak_door_bottom iron_door_top iron_door_bottom oak_trapdoor iron_trapdoor " +
            "white_wool glass_pane_top tinted_glass amethyst_block moss_block mud mud_bricks calcite tuff dripstone_block";

        private const string ItemNames =
            "apple golden_apple enchanted_golden_apple bread cooked_beef beef porkchop cooked_porkchop chicken cooked_chicken mutton cooked_mutton " +
            "rabbit cooked_rabbit cod cooked_cod salmon cooked_salmon carrot golden_carrot potato baked_potato poisonous_potato beetroot beetroot_soup " +
            "wheat wheat_seeds melon_slice cookie pumpkin_pie cake mushroom_stew rabbit_stew sweet_berries honey_bottle " +
            "diamond emerald iron_ingot gold_ingot copper_ingot netherite_ingot netherite_scrap coal charcoal stick string feather flint bone bone_meal " +
            "arrow spectral_arrow bow crossbow_standby fishing_rod carrot_on_a_stick trident spyglass shears flint_and_steel brush " +
            "bucket water_bucket lava_bucket milk_bucket powder_snow_bucket compass clock map filled_map book writable_book enchanted_book paper name_tag lead saddle " +
            "ender_pearl ender_eye blaze_rod blaze_powder gunpowder redstone glowstone_dust lapis_lazuli quartz slime_ball snowball egg bowl glass_bottle " +
            "potion splash_potion experience_bottle totem_of_undying elytra nether_star firework_rocket fire_charge minecart chest_minecart furnace_minecart " +
            "oak_boat oak_sign oak_door iron_door bed repeater comparator hopper brewing_stand cauldron flower_pot armor_stand item_frame painting";

        private static readonly Dictionary<string, string> Known = BuildKnown();

        private static Dictionary<string, string> BuildKnown()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string folder, string name) => map.TryAdd(name, folder + "/" + name);   // block wins when a name is in both

            foreach (string n in BlockNames.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Add("block", n);
            foreach (string c in Colors)
                foreach (string kind in new[] { "wool", "concrete", "concrete_powder", "terracotta", "stained_glass", "glazed_terracotta" })
                    Add("block", c + "_" + kind);
            foreach (string w in Woods)
                foreach (string suffix in new[] { "planks", "log", "log_top", "leaves", "door_top", "door_bottom", "trapdoor" })
                    Add("block", w + "_" + suffix);

            foreach (string n in ItemNames.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Add("item", n);
            foreach (string m in ToolMaterials)
                foreach (string t in new[] { "sword", "pickaxe", "axe", "shovel", "hoe" })
                    Add("item", m + "_" + t);
            foreach (string m in ArmorMaterials)
                foreach (string p in new[] { "helmet", "chestplate", "leggings", "boots" })
                    Add("item", m + "_" + p);
            Add("item", "turtle_helmet");
            foreach (string c in Colors) Add("item", c + "_dye");
            foreach (string w in Woods) { Add("item", w + "_boat"); Add("item", w + "_sign"); Add("item", w + "_door"); }
            return map;
        }
    }
}
