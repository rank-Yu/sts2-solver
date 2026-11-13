// Healing-source classifications from CombatSolver a62f1850 FlashBoundBridge, updated for
// 0.113.0 against b2a5c474 coverage/evidence/sts2-0.113-healing-audit.json.
// MIT, Copyright (c) 2026 Torch1230 and contributors.
// Native names resolve to this registry's IDs; content additions cannot shift existing entries.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Import;

/// <summary>Audited healing-bound classification tables, indexed by Sts2Solver content id.</summary>
internal static class HealingBoundTables
{
    private static readonly int[] UnsafeCards = [Id("Card", "ABUNDANCE"), Id("Card", "ALCHEMIZE"), Id("Card", "APOTHEOSIS"), Id("Card", "APPARITION"), Id("Card", "BRIGHTEST_FLAME"), Id("Card", "BYRDONIS_EGG"), Id("Card", "BYRD_SWOOP"), Id("Card", "CALTROPS"), Id("Card", "CLASH"), Id("Card", "DISINTEGRATION"), Id("Card", "DISTRACTION"), Id("Card", "DOWSING"), Id("Card", "DUAL_WIELD"), Id("Card", "EIDOLON"), Id("Card", "ENLIGHTENMENT"), Id("Card", "ENTRENCH"), Id("Card", "EXTERMINATE"), Id("Card", "FEED"), Id("Card", "FEEDING_FRENZY"), Id("Card", "FORBIDDEN_GRIMOIRE"), Id("Card", "FRANTIC_ESCAPE"), Id("Card", "HAND_OF_GREED"), Id("Card", "HELLO_WORLD"), Id("Card", "HIDDEN_GEM"), Id("Card", "LANTERN_KEY"), Id("Card", "LUMINESCE"), Id("Card", "MAD_SCIENCE"), Id("Card", "MAUL"), Id("Card", "METAMORPHOSIS"), Id("Card", "MIND_ROT"), Id("Card", "NEOWS_FURY"), Id("Card", "NIGHTMARE"), Id("Card", "NOT_YET"), Id("Card", "OUTMANEUVER"), Id("Card", "PECK"), Id("Card", "REBOUND"), Id("Card", "RELAX"), Id("Card", "RIP_AND_TEAR"), Id("Card", "ROYALTIES"), Id("Card", "SLOTH"), Id("Card", "SOOT"), Id("Card", "SPOILS_MAP"), Id("Card", "SQUASH"), Id("Card", "STACK"), Id("Card", "THE_HUNT"), Id("Card", "TORIC_TOUGHNESS"), Id("Card", "TRANSFIGURE"), Id("Card", "WASTE_AWAY"), Id("Card", "WHISTLE"), Id("Card", "WISH")];

    private static readonly int[] UnsafePowers = [Id("Power", "ADAPTABLE_POWER"), Id("Power", "ASLEEP_POWER"), Id("Power", "BACK_ATTACK_LEFT_POWER"), Id("Power", "BACK_ATTACK_RIGHT_POWER"), Id("Power", "BATTLEWORN_DUMMY_TIME_LIMIT_POWER"), Id("Power", "BURROWED_POWER"), Id("Power", "CHAINS_OF_BINDING_POWER"), Id("Power", "CONFUSED_POWER"), Id("Power", "CONSTRICT_POWER"), Id("Power", "CRAB_RAGE_POWER"), Id("Power", "CURIOUS_POWER"), Id("Power", "DAMPEN_POWER"), Id("Power", "DISINTEGRATION_POWER"), Id("Power", "ENRAGE_POWER"), Id("Power", "ESCAPE_ARTIST_POWER"), Id("Power", "FEEDING_FRENZY_POWER"), Id("Power", "FLUTTER_POWER"), Id("Power", "GALVANIC_POWER"), Id("Power", "HARDENED_SHELL_POWER"), Id("Power", "HARD_TO_KILL_POWER"), Id("Power", "HATCH_POWER"), Id("Power", "HEIST_POWER"), Id("Power", "HELICAL_DART_POWER"), Id("Power", "HELLO_WORLD_POWER"), Id("Power", "HEX_POWER"), Id("Power", "HIGH_VOLTAGE_POWER"), Id("Power", "ILLUSION_POWER"), Id("Power", "IMBALANCED_POWER"), Id("Power", "IMPROVEMENT_POWER"), Id("Power", "INFESTED_POWER"), Id("Power", "MIND_ROT_POWER"), Id("Power", "MINION_POWER"), Id("Power", "NEMESIS_POWER"), Id("Power", "NIGHTMARE_POWER"), Id("Power", "PAINFUL_STABS_POWER"), Id("Power", "PAPER_CUTS_POWER"), Id("Power", "PERSONAL_HIVE_POWER"), Id("Power", "PLOW_POWER"), Id("Power", "POSSESS_SPEED_POWER"), Id("Power", "POSSESS_STRENGTH_POWER"), Id("Power", "RAMPART_POWER"), Id("Power", "RAVENOUS_POWER"), Id("Power", "REBOUND_POWER"), Id("Power", "REPTILE_TRINKET_POWER"), Id("Power", "RINGING_POWER"), Id("Power", "ROYALTIES_POWER"), Id("Power", "SANDPIT_POWER"), Id("Power", "SELF_FORMING_CLAY_POWER"), Id("Power", "SHRIEK_POWER"), Id("Power", "SKITTISH_POWER"), Id("Power", "SLIPPERY_POWER"), Id("Power", "SLOTH_POWER"), Id("Power", "SLOW_POWER"), Id("Power", "SLUMBER_POWER"), Id("Power", "SMOGGY_POWER"), Id("Power", "SOAR_POWER"), Id("Power", "STEAM_ERUPTION_POWER"), Id("Power", "STOCK_POWER"), Id("Power", "SUCK_POWER"), Id("Power", "SURPRISE_POWER"), Id("Power", "SURROUNDED_POWER"), Id("Power", "SWIPE_POWER"), Id("Power", "TANGLED_POWER"), Id("Power", "TENDER_POWER"), Id("Power", "TERRITORIAL_POWER"), Id("Power", "THE_HUNT_POWER"), Id("Power", "THIEVERY_POWER"), Id("Power", "TORIC_TOUGHNESS_POWER"), Id("Power", "WASTE_AWAY_POWER"), Id("Power", "WITHERING_PRESENCE_POWER")];

    private static readonly int[] UnsafeRelics = [Id("Relic", "AKABEKO"), Id("Relic", "ALCHEMICAL_COFFER"), Id("Relic", "AMETHYST_AUBERGINE"), Id("Relic", "ANCHOR"), Id("Relic", "ARCANE_SCROLL"), Id("Relic", "ARCHAIC_TOOTH"), Id("Relic", "ART_OF_WAR"), Id("Relic", "ASTROLABE"), Id("Relic", "BAG_OF_MARBLES"), Id("Relic", "BAG_OF_PREPARATION"), Id("Relic", "BEATING_REMNANT"), Id("Relic", "BEAUTIFUL_BRACELET"), Id("Relic", "BELLOWS"), Id("Relic", "BELT_BUCKLE"), Id("Relic", "BIG_HAT"), Id("Relic", "BIG_MUSHROOM"), Id("Relic", "BIIIG_HUG"), Id("Relic", "BING_BONG"), Id("Relic", "BLACK_BLOOD"), Id("Relic", "BLACK_STAR"), Id("Relic", "BLESSED_ANTLER"), Id("Relic", "BLOOD_SOAKED_ROSE"), Id("Relic", "BLOOD_VIAL"), Id("Relic", "BONE_FLUTE"), Id("Relic", "BONE_TEA"), Id("Relic", "BOOKMARK"), Id("Relic", "BOOK_OF_FIVE_RINGS"), Id("Relic", "BOOK_REPAIR_KNIFE"), Id("Relic", "BOOMING_CONCH"), Id("Relic", "BOUND_PHYLACTERY"), Id("Relic", "BOWLER_HAT"), Id("Relic", "BREAD"), Id("Relic", "BRILLIANT_SCARF"), Id("Relic", "BRONZE_SCALES"), Id("Relic", "BURNING_STICKS"), Id("Relic", "CALLING_BELL"), Id("Relic", "CANDELABRA"), Id("Relic", "CAPTAINS_WHEEL"), Id("Relic", "CENTENNIAL_PUZZLE"), Id("Relic", "CHANDELIER"), Id("Relic", "CHARONS_ASHES"), Id("Relic", "CHEMICAL_X"), Id("Relic", "CHOICES_PARADOX"), Id("Relic", "CHOSEN_CHEESE"), Id("Relic", "CIRCLET"), Id("Relic", "CLAWS"), Id("Relic", "CLOAK_CLASP"), Id("Relic", "CRACKED_CORE"), Id("Relic", "CROSSBOW"), Id("Relic", "CURSED_PEARL"), Id("Relic", "DARKSTONE_PERIAPT"), Id("Relic", "DATA_DISK"), Id("Relic", "DAUGHTER_OF_THE_WIND"), Id("Relic", "DELICATE_FROND"), Id("Relic", "DEMON_TONGUE"), Id("Relic", "DIAMOND_DIADEM"), Id("Relic", "DINGY_RUG"), Id("Relic", "DISTINGUISHED_CAPE"), Id("Relic", "DIVINE_DESTINY"), Id("Relic", "DOLLYS_MIRROR"), Id("Relic", "DOWSING_ROD"), Id("Relic", "DRAGON_FRUIT"), Id("Relic", "DREAM_CATCHER"), Id("Relic", "DRIFTWOOD"), Id("Relic", "DUSTY_TOME"), Id("Relic", "ECTOPLASM"), Id("Relic", "ELECTRIC_SHRYMP"), Id("Relic", "EMBER_TEA"), Id("Relic", "EMOTION_CHIP"), Id("Relic", "EMPTY_CAGE"), Id("Relic", "FAKE_ANCHOR"), Id("Relic", "FAKE_BLOOD_VIAL"), Id("Relic", "FAKE_LEES_WAFFLE"), Id("Relic", "FAKE_MANGO"), Id("Relic", "FAKE_MERCHANTS_RUG"), Id("Relic", "FAKE_ORICHALCUM"), Id("Relic", "FAKE_SNECKO_EYE"), Id("Relic", "FAKE_STRIKE_DUMMY"), Id("Relic", "FAKE_VENERABLE_TEA_SET"), Id("Relic", "FENCING_MANUAL"), Id("Relic", "FIDDLE"), Id("Relic", "FISHING_ROD"), Id("Relic", "FORGOTTEN_SOUL"), Id("Relic", "FRAGRANT_MUSHROOM"), Id("Relic", "FRESNEL_LENS"), Id("Relic", "FROZEN_EGG"), Id("Relic", "FUNERARY_MASK"), Id("Relic", "FUR_COAT"), Id("Relic", "GALACTIC_DUST"), Id("Relic", "GAMBLING_CHIP"), Id("Relic", "GAME_PIECE"), Id("Relic", "GLASS_EYE"), Id("Relic", "GLITTER"), Id("Relic", "GNARLED_HAMMER"), Id("Relic", "GOLDEN_COMPASS"), Id("Relic", "GOLDEN_PEARL"), Id("Relic", "GOLD_PLATED_CABLES"), Id("Relic", "GORGET"), Id("Relic", "GREMLIN_HORN"), Id("Relic", "HAND_DRILL"), Id("Relic", "HAPPY_FLOWER"), Id("Relic", "HEFTY_TABLET"), Id("Relic", "HELICAL_DART"), Id("Relic", "HISTORY_COURSE"), Id("Relic", "HORN_CLEAT"), Id("Relic", "INFUSED_CORE"), Id("Relic", "INTIMIDATING_HELMET"), Id("Relic", "IRON_CLUB"), Id("Relic", "IVORY_TILE"), Id("Relic", "JEWELED_MASK"), Id("Relic", "JEWELRY_BOX"), Id("Relic", "JOSS_PAPER"), Id("Relic", "JUZU_BRACELET"), Id("Relic", "KIFUDA"), Id("Relic", "KUSARIGAMA"), Id("Relic", "LANTERN"), Id("Relic", "LARGE_CAPSULE"), Id("Relic", "LASTING_CANDY"), Id("Relic", "LAVA_LAMP"), Id("Relic", "LAVA_ROCK"), Id("Relic", "LEAD_PAPERWEIGHT"), Id("Relic", "LEAFY_POULTICE"), Id("Relic", "LEES_WAFFLE"), Id("Relic", "LETTER_OPENER"), Id("Relic", "LIZARD_TAIL"), Id("Relic", "LOOMING_FRUIT"), Id("Relic", "LORDS_PARASOL"), Id("Relic", "LOST_COFFER"), Id("Relic", "LOST_WISP"), Id("Relic", "LUCKY_FYSH"), Id("Relic", "LUNAR_PASTRY"), Id("Relic", "MANGO"), Id("Relic", "MASSIVE_SCROLL"), Id("Relic", "MAW_BANK"), Id("Relic", "MEAL_TICKET"), Id("Relic", "MEAT_CLEAVER"), Id("Relic", "MEAT_ON_THE_BONE"), Id("Relic", "MEMBERSHIP_CARD"), Id("Relic", "MERCURY_HOURGLASS"), Id("Relic", "METRONOME"), Id("Relic", "MINIATURE_CANNON"), Id("Relic", "MINIATURE_TENT"), Id("Relic", "MINI_REGENT"), Id("Relic", "MOLTEN_EGG"), Id("Relic", "MR_STRUGGLES"), Id("Relic", "MUMMIFIED_HAND"), Id("Relic", "MUSIC_BOX"), Id("Relic", "MYSTIC_LIGHTER"), Id("Relic", "NEOWS_BONES"), Id("Relic", "NEOWS_SACRIFICE"), Id("Relic", "NEOWS_TALISMAN"), Id("Relic", "NEOWS_TORMENT"), Id("Relic", "NEW_LEAF"), Id("Relic", "NINJA_SCROLL"), Id("Relic", "NUNCHAKU"), Id("Relic", "NUTRITIOUS_OYSTER"), Id("Relic", "NUTRITIOUS_SOUP"), Id("Relic", "ODDLY_SMOOTH_STONE"), Id("Relic", "ORANGE_DOUGH"), Id("Relic", "ORICHALCUM"), Id("Relic", "ORRERY"), Id("Relic", "PAELS_BLOOD"), Id("Relic", "PAELS_CLAW"), Id("Relic", "PAELS_EYE"), Id("Relic", "PAELS_FLESH"), Id("Relic", "PAELS_GROWTH"), Id("Relic", "PAELS_HORN"), Id("Relic", "PAELS_LEGION"), Id("Relic", "PAELS_TEARS"), Id("Relic", "PAELS_TOOTH"), Id("Relic", "PAELS_WING"), Id("Relic", "PANDORAS_BOX"), Id("Relic", "PANTOGRAPH"), Id("Relic", "PAPER_KRANE"), Id("Relic", "PAPER_PHROG"), Id("Relic", "PARRYING_SHIELD"), Id("Relic", "PEAR"), Id("Relic", "PENDULUM"), Id("Relic", "PEN_NIB"), Id("Relic", "PETRIFIED_TOAD"), Id("Relic", "PHIAL_HOLSTER"), Id("Relic", "PHILOSOPHERS_STONE"), Id("Relic", "PHYLACTERY_UNBOUND"), Id("Relic", "PLANISPHERE"), Id("Relic", "POCKETWATCH"), Id("Relic", "POLLINOUS_CORE"), Id("Relic", "POMANDER"), Id("Relic", "POWER_CELL"), Id("Relic", "PRAYER_WHEEL"), Id("Relic", "PRECARIOUS_SHEARS"), Id("Relic", "PRECISE_SCISSORS"), Id("Relic", "PRESERVED_FOG"), Id("Relic", "PRISMATIC_GEM"), Id("Relic", "PUMPKIN_CANDLE"), Id("Relic", "PUNCH_DAGGER"), Id("Relic", "RADIANT_PEARL"), Id("Relic", "RAINBOW_RING"), Id("Relic", "RAZOR_TOOTH"), Id("Relic", "RED_MASK"), Id("Relic", "RED_SKULL"), Id("Relic", "REGALITE"), Id("Relic", "REGAL_PILLOW"), Id("Relic", "REPTILE_TRINKET"), Id("Relic", "RINGING_TRIANGLE"), Id("Relic", "RING_OF_THE_DRAKE"), Id("Relic", "ROYAL_POISON"), Id("Relic", "ROYAL_STAMP"), Id("Relic", "RUINED_HELMET"), Id("Relic", "RUNIC_CAPACITOR"), Id("Relic", "RUNIC_PYRAMID"), Id("Relic", "SAI"), Id("Relic", "SAND_CASTLE"), Id("Relic", "SCREAMING_FLAGON"), Id("Relic", "SCROLL_BOXES"), Id("Relic", "SEAL_OF_GOLD"), Id("Relic", "SEA_GLASS"), Id("Relic", "SELF_FORMING_CLAY"), Id("Relic", "SERE_TALON"), Id("Relic", "SHURIKEN"), Id("Relic", "SIGNET_RING"), Id("Relic", "SILKEN_TRESS"), Id("Relic", "SILVER_CRUCIBLE"), Id("Relic", "SLING_OF_COURAGE"), Id("Relic", "SMALL_CAPSULE"), Id("Relic", "SNECKO_EYE"), Id("Relic", "SOZU"), Id("Relic", "SPARKLING_ROUGE"), Id("Relic", "SPIKED_GAUNTLETS"), Id("Relic", "STONE_CALENDAR"), Id("Relic", "STONE_CRACKER"), Id("Relic", "STONE_HUMIDIFIER"), Id("Relic", "STORYBOOK"), Id("Relic", "STRAWBERRY"), Id("Relic", "STURDY_CLAMP"), Id("Relic", "SWORD_OF_JADE"), Id("Relic", "SWORD_OF_STONE"), Id("Relic", "SYMBIOTIC_VIRUS"), Id("Relic", "TANXS_WHISTLE"), Id("Relic", "TEA_OF_DISCOURTESY"), Id("Relic", "THE_ABACUS"), Id("Relic", "THE_BOOT"), Id("Relic", "THE_COURIER"), Id("Relic", "THROWING_AXE"), Id("Relic", "THUMBSCREW"), Id("Relic", "TINGSHA"), Id("Relic", "TINY_MAILBOX"), Id("Relic", "TOASTY_MITTENS"), Id("Relic", "TOOLBOX"), Id("Relic", "TOUCH_OF_OROBAS"), Id("Relic", "TOUGH_BANDAGES"), Id("Relic", "TOXIC_EGG"), Id("Relic", "TOY_BOX"), Id("Relic", "TWISTED_FUNNEL"), Id("Relic", "UNCEASING_TOP"), Id("Relic", "UNDYING_SIGIL"), Id("Relic", "UNSETTLING_LAMP"), Id("Relic", "VAMBRACE"), Id("Relic", "VELVET_CHOKER"), Id("Relic", "VENERABLE_TEA_SET"), Id("Relic", "VEXING_PUZZLEBOX"), Id("Relic", "WAR_HAMMER"), Id("Relic", "WAR_PAINT"), Id("Relic", "WHISPERING_EARRING"), Id("Relic", "WHITE_BEAST_STATUE"), Id("Relic", "WHITE_STAR"), Id("Relic", "WINGED_BOOTS"), Id("Relic", "WING_CHARM"), Id("Relic", "WONGOS_MYSTERY_TICKET"), Id("Relic", "WONGO_CUSTOMER_APPRECIATION_BADGE"), Id("Relic", "YUMMY_COOKIE")];

    private static readonly int[] UnsafeMonsters = [Id("Monster", "AEONGLASS"), Id("Monster", "ARCHITECT"), Id("Monster", "ASSASSIN_RUBY_RAIDER"), Id("Monster", "AXEBOT"), Id("Monster", "AXE_RUBY_RAIDER"), Id("Monster", "BATTLE_FRIEND_V1"), Id("Monster", "BATTLE_FRIEND_V2"), Id("Monster", "BATTLE_FRIEND_V3"), Id("Monster", "BOWLBUG_EGG"), Id("Monster", "BOWLBUG_NECTAR"), Id("Monster", "BOWLBUG_ROCK"), Id("Monster", "BOWLBUG_SILK"), Id("Monster", "BRUTE_RUBY_RAIDER"), Id("Monster", "BYGONE_EFFIGY"), Id("Monster", "BYRDONIS"), Id("Monster", "CALCIFIED_CULTIST"), Id("Monster", "CEREMONIAL_BEAST"), Id("Monster", "CHOMPER"), Id("Monster", "CORPSE_SLUG"), Id("Monster", "CROSSBOW_RUBY_RAIDER"), Id("Monster", "CRUSHER"), Id("Monster", "CUBEX_CONSTRUCT"), Id("Monster", "DAMP_CULTIST"), Id("Monster", "DEVOTED_SCULPTOR"), Id("Monster", "ENTOMANCER"), Id("Monster", "EXOSKELETON"), Id("Monster", "EYE_WITH_TEETH"), Id("Monster", "FABRICATOR"), Id("Monster", "FAKE_MERCHANT_MONSTER"), Id("Monster", "FAT_GREMLIN"), Id("Monster", "FLAIL_KNIGHT"), Id("Monster", "FLYCONID"), Id("Monster", "FOGMOG"), Id("Monster", "FOSSIL_STALKER"), Id("Monster", "FROG_KNIGHT"), Id("Monster", "GAS_BOMB"), Id("Monster", "GLOBE_HEAD"), Id("Monster", "GREMLIN_MERC"), Id("Monster", "GUARDBOT"), Id("Monster", "HAUNTED_SHIP"), Id("Monster", "HUNTER_KILLER"), Id("Monster", "INKLET"), Id("Monster", "KIN_FOLLOWER"), Id("Monster", "KIN_PRIEST"), Id("Monster", "KNOWLEDGE_DEMON"), Id("Monster", "LAGAVULIN_MATRIARCH"), Id("Monster", "LEAF_SLIME_M"), Id("Monster", "LEAF_SLIME_S"), Id("Monster", "LIVING_FOG"), Id("Monster", "LIVING_SHIELD"), Id("Monster", "MAGI_KNIGHT"), Id("Monster", "MAWLER"), Id("Monster", "MECHA_KNIGHT"), Id("Monster", "MYSTERIOUS_KNIGHT"), Id("Monster", "MYTE"), Id("Monster", "NOISEBOT"), Id("Monster", "OVICOPTER"), Id("Monster", "OWL_MAGISTRATE"), Id("Monster", "PARAFRIGHT"), Id("Monster", "PHANTASMAL_GARDENER"), Id("Monster", "PHROG_PARASITE"), Id("Monster", "PUNCH_CONSTRUCT"), Id("Monster", "QUEEN"), Id("Monster", "ROCKET"), Id("Monster", "SCROLL_OF_BITING"), Id("Monster", "SEAPUNK"), Id("Monster", "SEWER_CLAM"), Id("Monster", "SHRINKER_BEETLE"), Id("Monster", "SKULKING_COLONY"), Id("Monster", "SLIMED_BERSERKER"), Id("Monster", "SLITHERING_STRANGLER"), Id("Monster", "SLUDGE_SPINNER"), Id("Monster", "SLUMBERING_BEETLE"), Id("Monster", "SNAPPING_JAXFRUIT"), Id("Monster", "SNEAKY_GREMLIN"), Id("Monster", "SOUL_FYSH"), Id("Monster", "SPECTRAL_KNIGHT"), Id("Monster", "SPINY_TOAD"), Id("Monster", "STABBOT"), Id("Monster", "TERROR_EEL"), Id("Monster", "TEST_SUBJECT"), Id("Monster", "THE_FORGOTTEN"), Id("Monster", "THE_INSATIABLE"), Id("Monster", "THE_LOST"), Id("Monster", "THE_OBSCURA"), Id("Monster", "THIEVING_HOPPER"), Id("Monster", "TOADPOLE"), Id("Monster", "TORCH_HEAD_AMALGAM"), Id("Monster", "TOUGH_EGG"), Id("Monster", "TRACKER_RUBY_RAIDER"), Id("Monster", "TUNNELER"), Id("Monster", "TURRET_OPERATOR"), Id("Monster", "TWIG_SLIME_M"), Id("Monster", "TWIG_SLIME_S"), Id("Monster", "TWO_TAILED_RAT"), Id("Monster", "VANTOM"), Id("Monster", "VINE_SHAMBLER"), Id("Monster", "WATERFALL_GIANT"), Id("Monster", "WRIGGLER"), Id("Monster", "ZAPBOT")];

    private static readonly int[] UnsafePotions = [Id("Potion", "AMBERGRIS"), Id("Potion", "BLOOD_POTION"), Id("Potion", "DEPRECATED_POTION"), Id("Potion", "ENTROPIC_BREW"), Id("Potion", "FAIRY_IN_A_BOTTLE"), Id("Potion", "FRUIT_JUICE")];

    private static readonly int[] SafeEnchantments = [Id("Enchantment", "INKY"), Id("Enchantment", "INSTINCT"), Id("Enchantment", "SLITHER")];

    private static readonly int[] SafeAfflictions = [Id("Affliction", "TAINTED")];

    private static readonly int[] KnownBlockingCards = [Id("Card", "FEED"), Id("Card", "NOT_YET")];

    private static readonly int[] KnownBlockingRelics = [Id("Relic", "BOOK_OF_FIVE_RINGS"), Id("Relic", "BOOK_REPAIR_KNIFE"), Id("Relic", "CHOSEN_CHEESE"), Id("Relic", "DARKSTONE_PERIAPT"), Id("Relic", "DEMON_TONGUE"), Id("Relic", "DRAGON_FRUIT"), Id("Relic", "LIZARD_TAIL")];

    private static readonly int[] RegenPowers = [Id("Power", "REGEN_POWER")];

    private static readonly int[] ReattachPowers = [Id("Power", "REATTACH_POWER")];

    private static readonly int[] SwipePowers = [Id("Power", "SWIPE_POWER")];

    private static readonly int[] RegenPotions = [Id("Potion", "REGEN_POTION")];

    private static readonly int[] DirectHealPotions = [Id("Potion", "AMBERGRIS"), Id("Potion", "BLOOD_POTION")];

    private static readonly int[] FruitJuicePotions = [Id("Potion", "FRUIT_JUICE")];

    private static readonly int[] FairyPotions = [Id("Potion", "FAIRY_IN_A_BOTTLE")];

    private static readonly int[] NativePotions = [Id("Potion", "BOTTLED_SONG"), Id("Potion", "CRYSTALLINE_EUPHORIA"), Id("Potion", "PRECISION_POTION"), Id("Potion", "AMBERGRIS"), Id("Potion", "ASHWATER"), Id("Potion", "ATTACK_POTION"), Id("Potion", "BEETLE_JUICE"), Id("Potion", "BLESSING_OF_THE_FORGE"), Id("Potion", "BLOCK_POTION"), Id("Potion", "BLOOD_POTION"), Id("Potion", "BONE_BREW"), Id("Potion", "BOTTLED_POTENTIAL"), Id("Potion", "CLARITY"), Id("Potion", "COLORLESS_POTION"), Id("Potion", "COSMIC_CONCOCTION"), Id("Potion", "CUNNING_POTION"), Id("Potion", "CURE_ALL"), Id("Potion", "DEPRECATED_POTION"), Id("Potion", "DEXTERITY_POTION"), Id("Potion", "DISTILLED_CHAOS"), Id("Potion", "DROPLET_OF_PRECOGNITION"), Id("Potion", "DUPLICATOR"), Id("Potion", "ENERGY_POTION"), Id("Potion", "ENTROPIC_BREW"), Id("Potion", "ESSENCE_OF_DARKNESS"), Id("Potion", "EXPLOSIVE_AMPOULE"), Id("Potion", "FAIRY_IN_A_BOTTLE"), Id("Potion", "FIRE_POTION"), Id("Potion", "FLEX_POTION"), Id("Potion", "FOCUS_POTION"), Id("Potion", "FORTIFIER"), Id("Potion", "FOUL_POTION"), Id("Potion", "FRUIT_JUICE"), Id("Potion", "FYSH_OIL"), Id("Potion", "GAMBLERS_BREW"), Id("Potion", "GHOST_IN_A_JAR"), Id("Potion", "GIGANTIFICATION_POTION"), Id("Potion", "GLOWWATER_POTION"), Id("Potion", "HEART_OF_IRON"), Id("Potion", "KINGS_COURAGE"), Id("Potion", "LIQUID_BRONZE"), Id("Potion", "LIQUID_MEMORIES"), Id("Potion", "LUCKY_TONIC"), Id("Potion", "MAZALETHS_GIFT"), Id("Potion", "OROBIC_ACID"), Id("Potion", "POISON_POTION"), Id("Potion", "POTION_OF_BINDING"), Id("Potion", "POTION_OF_CAPACITY"), Id("Potion", "POTION_OF_DOOM"), Id("Potion", "POTION_SHAPED_ROCK"), Id("Potion", "POT_OF_GHOULS"), Id("Potion", "POWDERED_DEMISE"), Id("Potion", "POWER_POTION"), Id("Potion", "RADIANT_TINCTURE"), Id("Potion", "REGEN_POTION"), Id("Potion", "SHACKLING_POTION"), Id("Potion", "SHIP_IN_A_BOTTLE"), Id("Potion", "SKILL_POTION"), Id("Potion", "SNECKO_OIL"), Id("Potion", "SOLDIERS_STEW"), Id("Potion", "SPEED_POTION"), Id("Potion", "STABLE_SERUM"), Id("Potion", "STAR_POTION"), Id("Potion", "STRENGTH_POTION"), Id("Potion", "SWIFT_POTION"), Id("Potion", "TOUCH_OF_INSANITY"), Id("Potion", "VULNERABLE_POTION"), Id("Potion", "WEAK_POTION")];

    private static readonly int[] ReattachSafeMonsters = [Id("Monster", "DECIMILLIPEDE_SEGMENT_BACK"), Id("Monster", "DECIMILLIPEDE_SEGMENT_FRONT"), Id("Monster", "DECIMILLIPEDE_SEGMENT_MIDDLE")];

    private static readonly (int Id, int Value)[] RegenPotionAmounts = [(Id("Potion", "REGEN_POTION"), 5)];

    private static readonly (int Id, decimal Value)[] DirectHealPotionPercents = [(Id("Potion", "AMBERGRIS"), 50m), (Id("Potion", "BLOOD_POTION"), 20m)];

    internal static readonly int RegenPowerId = Id("Power", "REGEN_POWER");
    internal static readonly int ReattachPowerId = Id("Power", "REATTACH_POWER");
    internal static int PotionCount => Registry.PotionCount;
    internal const int EnchantmentCount = 256;
    internal const int AfflictionCount = 256;

    internal static bool[] ComponentUnsafeCard() => Expand(UnsafeCards, Registry.CardCount);

    internal static bool[] ComponentUnsafePower() => Expand(UnsafePowers, Registry.PowerCount);

    internal static bool[] ComponentUnsafeRelic() => Expand(UnsafeRelics, Registry.RelicCount);

    internal static bool[] ComponentUnsafeMonster() => Expand(UnsafeMonsters, Registry.MonsterCount);

    internal static bool[] ComponentUnsafePotion() => Expand(UnsafePotions, Registry.PotionCount);

    internal static bool[] SafeEnchantment() => Expand(SafeEnchantments, EnchantmentCount);

    internal static bool[] SafeAffliction() => Expand(SafeAfflictions, AfflictionCount);

    internal static bool[] KnownBlockingCard() => Expand(KnownBlockingCards, Registry.CardCount);

    internal static bool[] KnownBlockingRelic() => Expand(KnownBlockingRelics, Registry.RelicCount);

    internal static bool[] RegenPower() => Expand(RegenPowers, Registry.PowerCount);

    internal static bool[] ReattachPower() => Expand(ReattachPowers, Registry.PowerCount);

    internal static bool[] SwipePower() => Expand(SwipePowers, Registry.PowerCount);

    internal static bool[] RegenPotion() => Expand(RegenPotions, Registry.PotionCount);

    internal static bool[] DirectHealPotion() => Expand(DirectHealPotions, Registry.PotionCount);

    internal static bool[] FruitJuicePotion() => Expand(FruitJuicePotions, Registry.PotionCount);

    internal static bool[] FairyPotion() => Expand(FairyPotions, Registry.PotionCount);

    internal static bool[] NativePotion() => Expand(NativePotions, Registry.PotionCount);

    internal static bool[] ReattachSafeMonster() => Expand(ReattachSafeMonsters, Registry.MonsterCount);

    internal static int[] RegenPotionAmount() => ExpandAmounts(PotionCount);

    internal static bool[] Expand(int[] trueIds, int count) => ExpandCore(trueIds, count);

    internal static int[] ExpandAmounts(int count) => ExpandAmountsCore(RegenPotionAmounts, count);

    internal static decimal[] ExpandPercents() => ExpandPercentsCore(DirectHealPotionPercents, PotionCount);

    private static bool[] ExpandCore(int[] trueIds, int count)
    {
        bool[] result = new bool[count];
        foreach (int id in trueIds)
        {
            if ((uint)id >= (uint)count)
                throw new UnsupportedImportException(ImportReason.Unrepresentable,
                    $"下界安全表 id {id} 超出容量 {count}");
            result[id] = true;
        }
        return result;
    }

    private static int[] ExpandAmountsCore((int Id, int Value)[] values, int count)
    {
        int[] result = new int[count];
        foreach ((int id, int value) in values)
        {
            if ((uint)id >= (uint)count)
                throw new UnsupportedImportException(ImportReason.Unrepresentable,
                    $"再生药水 id {id} 超出容量 {count}");
            result[id] = value;
        }
        return result;
    }

    private static decimal[] ExpandPercentsCore((int Id, decimal Value)[] values, int count)
    {
        decimal[] result = new decimal[count];
        foreach ((int id, decimal value) in values)
        {
            if ((uint)id >= (uint)count)
                throw new UnsupportedImportException(ImportReason.Unrepresentable,
                    $"直接治疗药水 id {id} 超出容量 {count}");
            result[id] = value;
        }
        return result;
    }

    private static int Id(string kind, string name)
    {
        ushort id = 0;
        bool found = kind switch
        {
            "Card" => Registry.TryCard(name, out id),
            "Power" => Registry.TryPower(name, out id),
            "Relic" => Registry.TryRelic(name, out id),
            "Monster" => Registry.TryMonster(name, out id),
            "Potion" => Registry.TryPotion(name, out id),
            "Enchantment" => Registry.TryEnchantment(name, out id),
            "Affliction" => Registry.TryAffliction(name, out id),
            _ => false
        };
        if (!found)
            throw new UnsupportedImportException(ImportReason.Unrepresentable, $"回血表缺少 {kind}/{name}");
        return id;
    }
}
