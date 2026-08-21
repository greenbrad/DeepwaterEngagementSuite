using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Forms;
using DeepwaterEngagementSuite.VoyagePlannerData;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using GameOffsets.Native;
using ImGuiNET;
using ItemFilterLibrary;
using Newtonsoft.Json;
using SharpDX;

namespace DeepwaterEngagementSuite;

public class DeepwaterEngagementSuiteSettings : ISettings
{
    public const MapIconsIndex DefaultOtherChestIcon = MapIconsIndex.HeistSpottedMiniBoss;
    public const MapIconsIndex DefaultBottledItemChestIcon = MapIconsIndex.QuestItem;
    public const MapIconsIndex DefaultGoldTreasureChestIcon = MapIconsIndex.LootFilterSmallYellowCircle;
    public const MapIconsIndex DefaultClamTreasureChestIcon = MapIconsIndex.LootFilterLargeYellowStar;
    public const MapIconsIndex DefaultCurrencyTreasureChestIcon = MapIconsIndex.RewardCurrency;
    public const MapIconsIndex DefaultCurrencyTreasureChestOpulentIcon = MapIconsIndex.LootFilterLargeYellowStar;
    public const MapIconsIndex DefaultCurrencyGemcuttersChestIcon = MapIconsIndex.RewardChestGems;
    public const MapIconsIndex DefaultUniqueWeaponChestIcon = MapIconsIndex.RewardWeapons;
    public const MapIconsIndex DefaultUniqueArmourChestIcon = MapIconsIndex.RewardArmour;
    public const MapIconsIndex DefaultUniqueJewelleryChestIcon = MapIconsIndex.RewardJewellery;
    public static readonly Color UniqueItemTint = new Color(175, 96, 37);
    public const MapIconsIndex DefaultScarabChestIcon = MapIconsIndex.RewardScarabs;
    public const MapIconsIndex DefaultStackedDecksChestIcon = MapIconsIndex.RewardDivinationCards;
    public const MapIconsIndex DefaultMapsChestIcon = MapIconsIndex.RewardMaps;
    public const MapIconsIndex DefaultAllflameEmbersChestIcon = MapIconsIndex.SanctumGoldConvert;
    public const MapIconsIndex DefaultCursedDucatDropIcon = MapIconsIndex.RewardPerandus;
    public const MapIconsIndex DefaultIzaroObjectIcon = MapIconsIndex.RewardLabyrinth;
    public const MapIconsIndex DefaultAltarCrabIcon = MapIconsIndex.RewardBestiary;
    public const MapIconsIndex DefaultAltarOctopusIcon = MapIconsIndex.RewardBreach;
    public const MapIconsIndex DefaultTormentedSpiritEncounterIcon = MapIconsIndex.LootFilterSmallGreenCircle;
    public const MapIconsIndex DefaultLanternReplenishEncounterIcon = MapIconsIndex.BlightPortalFire;

    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Icon Settings")]
    public IconSettings IconSettings { get; set; } = new IconSettings();

    [Menu("Loot Window Settings")]
    public LootWindowSettings LootWindowSettings { get; set; } = new LootWindowSettings();

    public CurrencyReminderSettings CurrencyReminderSettings { get; set; } = new CurrencyReminderSettings();
    public BubbleSettings BubbleSettings { get; set; } = new BubbleSettings();
    public TrailSettings TrailSettings { get; set; } = new TrailSettings();

    [Menu("Bubble planner settings")]
    public PlannerSettings PlannerSettings { get; set; } = new PlannerSettings();
    public VoyageSettings VoyageSettings { get; set; } = new VoyageSettings();

    public static MapIconsIndex GetDefaultIcon(IconPickerIndex index) => index switch
    {
        IconPickerIndex.BottledItemChest => DefaultBottledItemChestIcon,
        IconPickerIndex.GoldTreasureChest => DefaultGoldTreasureChestIcon,
        IconPickerIndex.ClamTreasureChest => DefaultClamTreasureChestIcon,
        IconPickerIndex.CurrencyTreasureChest => DefaultCurrencyTreasureChestIcon,
        IconPickerIndex.CurrencyTreasureChestOpulent => DefaultCurrencyTreasureChestOpulentIcon,
        IconPickerIndex.CurrencyGemcuttersChest => DefaultCurrencyGemcuttersChestIcon,
        IconPickerIndex.UniqueWeaponChest => DefaultUniqueWeaponChestIcon,
        IconPickerIndex.UniqueArmourChest => DefaultUniqueArmourChestIcon,
        IconPickerIndex.UniqueJewelleryChest => DefaultUniqueJewelleryChestIcon,
        IconPickerIndex.ScarabChest => DefaultScarabChestIcon,
        IconPickerIndex.StackedDecksChest => DefaultStackedDecksChestIcon,
        IconPickerIndex.MapsChest => DefaultMapsChestIcon,
        IconPickerIndex.AllflameEmbersChest => DefaultAllflameEmbersChestIcon,
        IconPickerIndex.CursedDucatDrop => DefaultCursedDucatDropIcon,
        IconPickerIndex.RandomDucatChest => DefaultCursedDucatDropIcon,
        IconPickerIndex.HazardBoatChest => DefaultCursedDucatDropIcon,
        IconPickerIndex.IzaroObject => DefaultIzaroObjectIcon,
        IconPickerIndex.AltarCrab => DefaultAltarCrabIcon,
        IconPickerIndex.AltarOctopus => DefaultAltarOctopusIcon,
        IconPickerIndex.TormentedSpiritEncounter => DefaultTormentedSpiritEncounterIcon,
        IconPickerIndex.LanternReplenishEncounter => DefaultLanternReplenishEncounterIcon,
        IconPickerIndex.GoldenLanternEncounter => MapIconsIndex.LabyrinthGoldKey,
        IconPickerIndex.InfusedCoralEncounter => MapIconsIndex.RewardBreach,
        IconPickerIndex.StrongboxDivination => MapIconsIndex.CorpseTypeUndead,
        IconPickerIndex.StrongboxScarab => MapIconsIndex.CorpseTypeEldritch,
        IconPickerIndex.StrongboxArcanist => MapIconsIndex.CorpseTypeBeast,
        IconPickerIndex.PointerTarget => MapIconsIndex.AncestralEnemyTotem,
        _ => DefaultOtherChestIcon,
    };

    public static Color? GetDefaultTint(IconPickerIndex index) => index switch
    {
        IconPickerIndex.UniqueWeaponChest or IconPickerIndex.UniqueArmourChest or IconPickerIndex.UniqueJewelleryChest => UniqueItemTint,
        IconPickerIndex.InfusedCoralEncounter => new Color(255, 90, 180),
        IconPickerIndex.PointerTarget => Color.White,
        _ => null,
    };

    public static float GetDefaultIconSizeScale(IconPickerIndex index) => index switch
    {
        IconPickerIndex.CurrencyTreasureChestOpulent => 2.0f,
        _ => 1f,
    };

}

[Submenu(CollapsedByDefault = true)]
public class LootWindowSettings
{
    [Menu("Show loot window",
        "Summary window of discovered Deepwater targets. Off by default.")]
    public ToggleNode ShowLootWindow { get; set; } = new ToggleNode(false);
}

[Submenu(CollapsedByDefault = true)]
public class IconSettings
{
    public Dictionary<IconPickerIndex, IconDisplaySettings> IconMapping = new();

    public RangeNode<int> WorldIconSize { get; set; } = new RangeNode<int>(50, 25, 200);
    public RangeNode<int> MapIconSize { get; set; } = new RangeNode<int>(30, 15, 200);

    [Menu("Show Bottled Item icons")]
    public ToggleNode ShowBottledItemIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Gold Treasure icons")]
    public ToggleNode ShowGoldTreasureIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Clam Treasure icons")]
    public ToggleNode ShowClamTreasureIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Currency chest icons")]
    public ToggleNode ShowCurrencyChestIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Opulent Currency icons")]
    public ToggleNode ShowOpulentCurrencyIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Gemcutter chest icons")]
    public ToggleNode ShowGemcutterChestIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Unique Weapon icons")]
    public ToggleNode ShowUniqueWeaponIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Unique Armour icons")]
    public ToggleNode ShowUniqueArmourIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Unique Jewellery icons")]
    public ToggleNode ShowUniqueJewelleryIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Scarab chest icons")]
    public ToggleNode ShowScarabChestIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Stacked Deck icons")]
    public ToggleNode ShowStackedDeckIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Maps chest icons",
        "Cartography / map chests. Off by default.")]
    public ToggleNode ShowMapsChestIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Allflame Embers icons")]
    public ToggleNode ShowAllflameEmbersIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Izaro icons")]
    public ToggleNode ShowIzaroIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Altar (Crab) icons")]
    public ToggleNode ShowAltarCrabIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Altar (Octopus) icons")]
    public ToggleNode ShowAltarOctopusIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Lantern Replenish icons")]
    public ToggleNode ShowLanternReplenishIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Infused Coral icons")]
    public ToggleNode ShowInfusedCoralIcons { get; set; } = new ToggleNode(true);

    [Menu("Show other chest icons")]
    public ToggleNode ShowOtherChestIcons { get; set; } = new ToggleNode(true);

    [Menu("Show pointer stand-in icons",
        "Undiscovered pointer targets on the large map.")]
    public ToggleNode ShowPointerTargetIcons { get; set; } = new ToggleNode(true);

    [Menu("Show Ducat icons",
        "Cursed ducat drops, random ducat chests, and hazard boats. Off by default.")]
    public ToggleNode ShowDucatIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Golden Lantern icons",
        "Off by default to reduce clutter.")]
    public ToggleNode ShowGoldenLanternIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Tormented Spirit icons",
        "Off by default to reduce clutter.")]
    public ToggleNode ShowTormentedSpiritIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Arcanist strongbox icons",
        "Off by default to reduce clutter.")]
    public ToggleNode ShowArcanistStrongboxIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Diviner strongbox icons",
        "Off by default to reduce clutter.")]
    public ToggleNode ShowDivinerStrongboxIcons { get; set; } = new ToggleNode(false);

    [Menu("Show Scarab strongbox icons",
        "Off by default to reduce clutter.")]
    public ToggleNode ShowScarabStrongboxIcons { get; set; } = new ToggleNode(false);

    public bool IsIconEnabled(IconPickerIndex index) => index switch
    {
        IconPickerIndex.BottledItemChest => ShowBottledItemIcons.Value,
        IconPickerIndex.GoldTreasureChest => ShowGoldTreasureIcons.Value,
        IconPickerIndex.ClamTreasureChest => ShowClamTreasureIcons.Value,
        IconPickerIndex.CurrencyTreasureChest => ShowCurrencyChestIcons.Value,
        IconPickerIndex.CurrencyTreasureChestOpulent => ShowOpulentCurrencyIcons.Value,
        IconPickerIndex.CurrencyGemcuttersChest => ShowGemcutterChestIcons.Value,
        IconPickerIndex.UniqueWeaponChest => ShowUniqueWeaponIcons.Value,
        IconPickerIndex.UniqueArmourChest => ShowUniqueArmourIcons.Value,
        IconPickerIndex.UniqueJewelleryChest => ShowUniqueJewelleryIcons.Value,
        IconPickerIndex.ScarabChest => ShowScarabChestIcons.Value,
        IconPickerIndex.StackedDecksChest => ShowStackedDeckIcons.Value,
        IconPickerIndex.MapsChest => ShowMapsChestIcons.Value,
        IconPickerIndex.AllflameEmbersChest => ShowAllflameEmbersIcons.Value,
        IconPickerIndex.CursedDucatDrop or
            IconPickerIndex.RandomDucatChest or
            IconPickerIndex.HazardBoatChest => ShowDucatIcons.Value,
        IconPickerIndex.IzaroObject => ShowIzaroIcons.Value,
        IconPickerIndex.AltarCrab => ShowAltarCrabIcons.Value,
        IconPickerIndex.AltarOctopus => ShowAltarOctopusIcons.Value,
        IconPickerIndex.TormentedSpiritEncounter => ShowTormentedSpiritIcons.Value,
        IconPickerIndex.LanternReplenishEncounter => ShowLanternReplenishIcons.Value,
        IconPickerIndex.GoldenLanternEncounter => ShowGoldenLanternIcons.Value,
        IconPickerIndex.InfusedCoralEncounter => ShowInfusedCoralIcons.Value,
        IconPickerIndex.StrongboxDivination => ShowDivinerStrongboxIcons.Value,
        IconPickerIndex.StrongboxScarab => ShowScarabStrongboxIcons.Value,
        IconPickerIndex.StrongboxArcanist => ShowArcanistStrongboxIcons.Value,
        IconPickerIndex.PointerTarget => ShowPointerTargetIcons.Value,
        IconPickerIndex.OtherChests => ShowOtherChestIcons.Value,
        _ => true,
    };
}

[Submenu(CollapsedByDefault = true)]
public class TrailSettings
{
    public ToggleNode Enabled { get; set; } = new ToggleNode(false);
    public ToggleNode DrawOnLargeMap { get; set; } = new ToggleNode(true);
    public ToggleNode DrawInWorld { get; set; } = new ToggleNode(false);
    public ToggleNode OnlyUnreachable { get; set; } = new ToggleNode(false);
    public ToggleNode ShowLabels { get; set; } = new ToggleNode(true);
    public RangeNode<int> MaxDistance { get; set; } = new RangeNode<int>(500, 10, 1000);
    public RangeNode<int> MapLineWidth { get; set; } = new RangeNode<int>(3, 1, 20);
    public RangeNode<int> WorldLineWidth { get; set; } = new RangeNode<int>(5, 1, 20);
    public ColorNode DefaultMapColor { get; set; } = new Color(255, 140, 0, 200);
    public ColorNode DefaultWorldColor { get; set; } = new Color(255, 140, 0, 200);
    public ToggleNode ShowUndiscoveredTargets { get; set; } = new ToggleNode(true);
    public ColorNode UndiscoveredColor { get; set; } = new Color(255, 255, 255, 220);
    public TrailColorSettings Colors { get; set; } = new TrailColorSettings();
}

[Submenu(CollapsedByDefault = true)]
public class TrailColorSettings
{
    public ToggleNode ShowBottledItem { get; set; } = new ToggleNode(true);
    public ColorNode BottledItem { get; set; } = new Color(255, 215, 0, 255);
    public ToggleNode ShowGoldTreasure { get; set; } = new ToggleNode(true);
    public ColorNode GoldTreasure { get; set; } = new Color(255, 215, 0, 255);
    public ToggleNode ShowClamTreasure { get; set; } = new ToggleNode(true);
    public ColorNode ClamTreasure { get; set; } = new Color(255, 255, 100, 255);
    public ToggleNode ShowCurrency { get; set; } = new ToggleNode(true);
    public ColorNode Currency { get; set; } = new Color(255, 255, 255, 255);
    public ToggleNode ShowOpulentCurrency { get; set; } = new ToggleNode(true);
    public ColorNode OpulentCurrency { get; set; } = new Color(255, 170, 0, 255);
    public ToggleNode ShowUniqueWeapon { get; set; } = new ToggleNode(true);
    public ColorNode UniqueWeapon { get; set; } = new Color(175, 96, 37, 255);
    public ToggleNode ShowUniqueArmour { get; set; } = new ToggleNode(true);
    public ColorNode UniqueArmour { get; set; } = new Color(175, 96, 37, 255);
    public ToggleNode ShowUniqueJewellery { get; set; } = new ToggleNode(true);
    public ColorNode UniqueJewellery { get; set; } = new Color(175, 96, 37, 255);
    public ToggleNode ShowScarabs { get; set; } = new ToggleNode(true);
    public ColorNode Scarabs { get; set; } = new Color(200, 150, 255, 255);
    public ToggleNode ShowStackedDecks { get; set; } = new ToggleNode(true);
    public ColorNode StackedDecks { get; set; } = new Color(100, 200, 255, 255);
    public ToggleNode ShowMaps { get; set; } = new ToggleNode(true);
    public ColorNode Maps { get; set; } = new Color(200, 200, 200, 255);
    public ToggleNode ShowAllflameEmbers { get; set; } = new ToggleNode(true);
    public ColorNode AllflameEmbers { get; set; } = new Color(255, 100, 50, 255);
    public ToggleNode ShowCursedDucat { get; set; } = new ToggleNode(true);
    public ColorNode CursedDucat { get; set; } = new Color(255, 200, 50, 255);
    public ToggleNode ShowRandomDucat { get; set; } = new ToggleNode(true);
    public ColorNode RandomDucat { get; set; } = new Color(255, 200, 50, 255);
    public ToggleNode ShowHazardBoat { get; set; } = new ToggleNode(true);
    public ColorNode HazardBoat { get; set; } = new Color(255, 200, 50, 255);
    public ToggleNode ShowIzaro { get; set; } = new ToggleNode(true);
    public ColorNode Izaro { get; set; } = new Color(255, 255, 0, 255);
    public ToggleNode ShowAltarCrab { get; set; } = new ToggleNode(true);
    public ColorNode AltarCrab { get; set; } = new Color(50, 200, 50, 255);
    public ToggleNode ShowAltarOctopus { get; set; } = new ToggleNode(true);
    public ColorNode AltarOctopus { get; set; } = new Color(150, 50, 255, 255);
    public ToggleNode ShowTormentedSpirit { get; set; } = new ToggleNode(true);
    public ColorNode TormentedSpirit { get; set; } = new Color(0, 255, 100, 255);
    public ToggleNode ShowLanternReplenish { get; set; } = new ToggleNode(true);
    public ColorNode LanternReplenish { get; set; } = new Color(100, 200, 255, 255);
    public ToggleNode ShowGoldenLantern { get; set; } = new ToggleNode(true);
    public ColorNode GoldenLantern { get; set; } = new Color(255, 215, 0, 255);
    public ToggleNode ShowInfusedCoral { get; set; } = new ToggleNode(true);
    public ColorNode InfusedCoral { get; set; } = new Color(255, 90, 180, 255);
    public ToggleNode ShowOther { get; set; } = new ToggleNode(true);
    public ColorNode Other { get; set; } = new Color(180, 180, 180, 255);

    public bool IsEnabled(IconPickerIndex type) => type switch
    {
        IconPickerIndex.BottledItemChest => ShowBottledItem.Value,
        IconPickerIndex.GoldTreasureChest => ShowGoldTreasure.Value,
        IconPickerIndex.ClamTreasureChest => ShowClamTreasure.Value,
        IconPickerIndex.CurrencyTreasureChest => ShowCurrency.Value,
        IconPickerIndex.CurrencyTreasureChestOpulent => ShowOpulentCurrency.Value,
        IconPickerIndex.UniqueWeaponChest => ShowUniqueWeapon.Value,
        IconPickerIndex.UniqueArmourChest => ShowUniqueArmour.Value,
        IconPickerIndex.UniqueJewelleryChest => ShowUniqueJewellery.Value,
        IconPickerIndex.ScarabChest => ShowScarabs.Value,
        IconPickerIndex.StackedDecksChest => ShowStackedDecks.Value,
        IconPickerIndex.MapsChest => ShowMaps.Value,
        IconPickerIndex.AllflameEmbersChest => ShowAllflameEmbers.Value,
        IconPickerIndex.CursedDucatDrop => ShowCursedDucat.Value,
        IconPickerIndex.RandomDucatChest => ShowRandomDucat.Value,
        IconPickerIndex.HazardBoatChest => ShowHazardBoat.Value,
        IconPickerIndex.IzaroObject => ShowIzaro.Value,
        IconPickerIndex.AltarCrab => ShowAltarCrab.Value,
        IconPickerIndex.AltarOctopus => ShowAltarOctopus.Value,
        IconPickerIndex.TormentedSpiritEncounter => ShowTormentedSpirit.Value,
        IconPickerIndex.LanternReplenishEncounter => ShowLanternReplenish.Value,
        IconPickerIndex.GoldenLanternEncounter => ShowGoldenLantern.Value,
        IconPickerIndex.InfusedCoralEncounter => ShowInfusedCoral.Value,
        IconPickerIndex.OtherChests => ShowOther.Value,
        _ => true,
    };

    public Color Get(IconPickerIndex type, Color fallback) => type switch
    {
        IconPickerIndex.BottledItemChest => BottledItem.Value,
        IconPickerIndex.GoldTreasureChest => GoldTreasure.Value,
        IconPickerIndex.ClamTreasureChest => ClamTreasure.Value,
        IconPickerIndex.CurrencyTreasureChest => Currency.Value,
        IconPickerIndex.CurrencyTreasureChestOpulent => OpulentCurrency.Value,
        IconPickerIndex.UniqueWeaponChest => UniqueWeapon.Value,
        IconPickerIndex.UniqueArmourChest => UniqueArmour.Value,
        IconPickerIndex.UniqueJewelleryChest => UniqueJewellery.Value,
        IconPickerIndex.ScarabChest => Scarabs.Value,
        IconPickerIndex.StackedDecksChest => StackedDecks.Value,
        IconPickerIndex.MapsChest => Maps.Value,
        IconPickerIndex.AllflameEmbersChest => AllflameEmbers.Value,
        IconPickerIndex.CursedDucatDrop => CursedDucat.Value,
        IconPickerIndex.RandomDucatChest => RandomDucat.Value,
        IconPickerIndex.HazardBoatChest => HazardBoat.Value,
        IconPickerIndex.IzaroObject => Izaro.Value,
        IconPickerIndex.AltarCrab => AltarCrab.Value,
        IconPickerIndex.AltarOctopus => AltarOctopus.Value,
        IconPickerIndex.TormentedSpiritEncounter => TormentedSpirit.Value,
        IconPickerIndex.LanternReplenishEncounter => LanternReplenish.Value,
        IconPickerIndex.GoldenLanternEncounter => GoldenLantern.Value,
        IconPickerIndex.InfusedCoralEncounter => InfusedCoral.Value,
        IconPickerIndex.OtherChests => Other.Value,
        _ => fallback,
    };
}

[Submenu(CollapsedByDefault = true)]
public class CurrencyReminderSettings
{
    public ToggleNode Enabled { get; set; } = new ToggleNode(false);
    public RangeNode<int> RequiredExaltedOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredAlchemyOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredChaosOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredScouringOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> MaxInventoryItems { get; set; } = new RangeNode<int>(30, 0, 60);
}

[Submenu(CollapsedByDefault = true)]
public class PlannerSettings
{
    public Dictionary<IconPickerIndex, ChestSettings> ChestSettingsMap = new()
    {
        [IconPickerIndex.BottledItemChest] = new ChestSettings { Weight = 30 },
        [IconPickerIndex.ClamTreasureChest] = new ChestSettings { Weight = 2 },
        [IconPickerIndex.LanternReplenishEncounter] = new ChestSettings { Weight = 30 },
        [IconPickerIndex.CurrencyTreasureChestOpulent] = new ChestSettings { Weight = 50 },
    };

    public HotkeyNodeV2 StartSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 StopSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 ClearSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 ConfirmEditorPlacementHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning), false)]
    public ButtonNode StartSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning))]
    public ButtonNode StopSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(HasSearchResult))]
    public ButtonNode ClearSearch { get; set; } = new ButtonNode();
    public ToggleNode PlaySoundOnFinish { get; set; } = new ToggleNode(false);
    public ToggleNode DrawPlannedBubblesOnMap { get; set; } = new ToggleNode(true);
    public ToggleNode DrawLinesToLanternsInWorld { get; set; } = new ToggleNode(true);
    public RangeNode<int> ClosestNLanterns { get; set; } = new RangeNode<int>(2, 0, 10);
    public ToggleNode MergePlannedBubbles { get; set; } = new ToggleNode(true);

    [Menu("Color for suggested bubble radius")]
    public ColorNode BubbleColor { get; set; } = new ColorNode(Color.Purple);

    public ColorNode MapLineColor { get; set; } = new ColorNode(Color.Red);
    public ColorNode WorldLineColor { get; set; } = new ColorNode(Color.Orange);

    [Menu("Color for captured entities in world")]
    public ColorNode CapturedEntityWorldFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu("Color for captured entities on map")]
    public ColorNode CapturedEntityMapFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu(null, "Do not show lines/circles for plan segments where a real bubble has already been placed")]
    public ToggleNode RemoveGraphicsForPlacedBubbles { get; set; } = new ToggleNode(false);

    public RangeNode<float> TextMarkerScale { get; set; } = new RangeNode<float>(2, 0, 5);

    public RangeNode<float> MaximumGenerationTimeSeconds { get; set; } = new RangeNode<float>(5, 0, 60);
    public RangeNode<int> SearchThreads { get; set; } = new RangeNode<int>(5, 1, 10);
    public RangeNode<float> NewRandomPathInjectionRate { get; set; } = new RangeNode<float>(1f, 0, 2);
    public RangeNode<int> PathGenerationSize { get; set; } = new RangeNode<int>(100, 1, 1000);
    public RangeNode<int> ValidatedIntermediatePoints { get; set; } = new RangeNode<int>(1, 0, 5);

    public ToggleNode ShowScoreHistory { get; set; } = new ToggleNode(false);
    public ToggleNode ShowScoreHistoryAfterSearchEnds { get; set; } = new ToggleNode(false);

    internal bool HasSearchResult => SearchState != SearchState.Empty;
    internal bool IsSearchRunning => SearchState == SearchState.Searching;

    internal SearchState SearchState = SearchState.Empty;
}

[Submenu(CollapsedByDefault = true)]
public class BubbleSettings
{
    public ToggleNode ShowBubblesOnMap { get; set; } = new ToggleNode(true);
    public ToggleNode ShowBubblesInWorld { get; set; } = new ToggleNode(false);

    [Menu("Color for bubble radius")]
    public ColorNode BubbleColor { get; set; } = new ColorNode(Color.Red);

    public RangeNode<int> BubbleRadiusOverride { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Merge bubble circles for planned bubbles")]
    public ToggleNode EnableBubbleRadiusMerging { get; set; } = new ToggleNode(true);

    [Menu("Hide icons of entities captured by bubbles in world")]
    public ToggleNode HideCapturedEntitiesInWorld { get; set; } = new ToggleNode(false);

    [Menu("Hide icons of entities captured by bubbles on map")]
    public ToggleNode HideCapturedEntitiesOnMap { get; set; } = new ToggleNode(false);

    [Menu("Rectangle Thickness for captured entities in world")]
    public RangeNode<int> CapturedEntityWorldFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);

    [Menu("Rectangle Thickness for captured entities on map")]
    public RangeNode<int> CapturedEntityMapFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);

    public ToggleNode MarkStartingBubble { get; set; } = new ToggleNode(true);
}

[Submenu(CollapsedByDefault = true)]
public class VoyageSettings
{
    public VoyageSettings()
    {
        ClearBorderModifiers = new ButtonNode() { OnPressed = () => { BorderModifiers.Content.Clear(); } };
        ClearChartModifiers = new ButtonNode() { OnPressed = () => { ChartModifiers.Content.Clear(); } };
    }

    [JsonIgnore] [IgnoreMenu] public List<VoyageProfileEntry> Profiles { get; set; } = new();

    public ToggleNode EnableVoyageHandling { get; set; } = new ToggleNode(true);

    [Menu(null, CollapsedByDefault = true)]
    public ContentNode<VoyageExcludedChartSettings> IgnoredCharts { get; set; } = new ContentNode<VoyageExcludedChartSettings>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageExcludedChartSettings(),
        ItemFilter = (o, s) => o.IFL.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };

    [Menu("Show optimizer window")]
    public ToggleNode ShowOptimizerWindow { get; set; } = new ToggleNode(true);

    // Set from the optimizer window's Strategy dropdown; empty = built-in rules.
    [IgnoreMenu]
    public TextNode SelectedStrategyId { get; set; } = new TextNode("");

    [IgnoreMenu]
    public TextNode SelectedStrategyLayoutId { get; set; } = new TextNode("");

    [Menu("Protect keeper charts",
        "With a strategy selected, hold back charts that other strategies need (Divine/Meatfish/Ethereal " +
        "fuel) so burn strategies only spend true junk. Keepers show a violet frame in the chart inventory.")]
    public ToggleNode ProtectKeeperCharts { get; set; } = new ToggleNode(true);

    [Menu("Highlight chart quality",
        "Color-codes charts in the chart inventory: violet = specialty combo piece, green = good, " +
        "yellow = some value, red = junk. When a strategy is selected, charts are judged by that " +
        "strategy's weights and rules instead of the configured chart modifier weights.")]
    public ToggleNode HighlightChartQuality { get; set; } = new ToggleNode(true);

    [Menu("Good chart threshold", "Summed weight at which a chart's frame turns green.")]
    public RangeNode<int> GoodChartThreshold { get; set; } = new RangeNode<int>(6, 1, 30);

    public ColorNode GoodChartColor { get; set; } = new ColorNode(SharpDX.Color.LimeGreen);

    public ColorNode UsefulChartColor { get; set; } = new ColorNode(SharpDX.Color.Yellow);

    public ColorNode JunkChartColor { get; set; } = new ColorNode(new SharpDX.Color(220, 40, 40, 160));

    public ColorNode SpecialtyChartColor { get; set; } = new ColorNode(SharpDX.Color.Violet);

    [Menu("Draw combo labels",
        "Overlay specialty combo pieces (Pelagic/farm rooms, starfish, rares, boxes, Lost Message) and strategy borders " +
        "(orbs, scarabs, strong treasure anchors). No-consume borders are used for strategy but not labeled. " +
        "Turn off for a clean voyage UI.")]
    public ToggleNode DrawComboLabels { get; set; } = new ToggleNode(true);

    [Menu("Show score debug details",
        "Verbose optimizer tables: per-tile (row,col) score breakdown and contribution sources. " +
        "Off by default — strategy labels stay available without this noise.")]
    public ToggleNode ShowScoreDebugDetails { get; set; } = new ToggleNode(false);

    public ToggleNode ShowAllBorderModifiers { get; set; } = new ToggleNode(true);
    public ToggleNode ShowAllChartModifiers { get; set; } = new ToggleNode(true);
    public ToggleNode ShowChartInventoryInformation { get; set; } = new ToggleNode(true);

    public ListNode ProfileSelector { get; set; } = new ListNode();
    [JsonIgnore] public ButtonNode AddProfile { get; set; } = new ButtonNode();
    [JsonIgnore] public ButtonNode ReloadProfiles { get; set; } = new ButtonNode();
    [JsonIgnore][Menu("Delete current profile (hold shift)")] public ButtonNode DeleteCurrentProfile { get; set; } = new ButtonNode();
    [JsonIgnore] public CustomNode ProfileRenameNode { get; set; } = new CustomNode();

    [JsonIgnore]
    public ButtonNode ClearBorderModifiers { get; set; }

    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<VoyageBorderModifier> BorderModifiers { get; set; } = new ContentNode<VoyageBorderModifier>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageBorderModifier(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                               o.Abbreviation.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };

    [JsonIgnore]
    public ButtonNode ClearChartModifiers { get; set; }

    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<VoyageChartModifier> ChartModifiers { get; set; } = new ContentNode<VoyageChartModifier>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageChartModifier(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };
}

[Submenu(CollapsedByDefault = true)]
public class VoyageExcludedChartSettings
{
    private static readonly ConcurrentDictionary<string, ItemQuery<ChartData>> FilterCache = [];

    public VoyageExcludedChartSettings()
    {
        Status.DrawDelegate = () =>
        {
            if (Query.FailedToCompile)
            {
                ImGui.Text($"Compilation failed: {Query.Error}");
            }
        };
    }

    [JsonIgnore]
    public CustomNode Status { get; set; } = new CustomNode();

    [Menu("IFL")]
    public TextNode IFL { get; set; } = new TextNode("false");
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [IgnoreMenu]
    [JsonIgnore]
    public ItemQuery<ChartData> Query => FilterCache.GetOrAdd(IFL.Value, ItemQuery.Load<ChartData>);

    public override string ToString()
    {
        return $"{(Enabled ? "" : "[Disabled]")}{IFL.Value}###";
    }
}

public class ChartData : ItemData
{
    public Vector2i Pos { get; }

    public ChartData(Entity queriedItem, GameController gc, Vector2i pos) 
        : base(queriedItem, gc)
    {
        Pos = pos;
    }

    public ChartData(Entity queriedItem, Entity groundItem, GameController gameController, Vector2i pos) 
        : base(queriedItem, groundItem, gameController)
    {
        Pos = pos;
    }
}

public class VoyageProfileEntry
{
    public string Name;
    public VoyageProfile Profile;
}

[Submenu(CollapsedByDefault = true)]
public class VoyageBorderModifier
{
    public TextNode Id { get; set; } = new TextNode("");    
    public TextNode Abbreviation { get; set; } = new TextNode("");

    [Menu(null, "For per-connection borders this is the multiplier per single connection: effective = 1 + (multiplier - 1) x connections")]
    public RangeNode<float> ValueMultiplier { get; set; } = new RangeNode<float>(1, 0, 10);

    [Menu(null, "Comma-separated reward categories this border boosts (e.g. 'Monsters, RareMonsters'). " +
                "'All' matches every chart modifier, 'None' makes the border inert for scoring (flat value). " +
                "Empty = All (legacy behavior). Categories: Monsters, MagicMonsters, RareMonsters, Essences, Strongboxes, " +
                "Uniques, Currency, Scarabs, Gold, Equipment, Experience, Resources, Lanterns, Rarity")]
    public TextNode Tags { get; set; } = new TextNode("");

    [Menu("Per connection", "Multiplier scales with the connection count of the chart placed on the affected tile ('... per Chart connection' borders)")]
    public ToggleNode PerConnection { get; set; } = new ToggleNode(false);

    [Menu("Affects placed chart", "Multiplies the modifiers of the chart placed on the adjacent tile (e.g. 'increased effect of adjacent Charts', chart refunds) instead of rewards landing on that tile")]
    public ToggleNode AffectsPlacedChart { get; set; } = new ToggleNode(false);

    public ColorNode HighlightColor { get; set; } = Color.Cyan;

    public override string ToString()
    {
        var tags = ModifierTagParser.Parse(Tags.Value, ModifierTag.All);
        return $"{Id.Value} x{ValueMultiplier.Value}{(PerConnection.Value ? "/conn" : "")}{(AffectsPlacedChart.Value ? " [chart]" : "")} ({tags})###";
    }
}

[Submenu(CollapsedByDefault = true)]
public class VoyageChartModifier
{
    public TextNode Id { get; set; } = new TextNode("");
    public TextNode Label { get; set; } = new TextNode("");
    public RangeNode<float> Weight { get; set; } = new RangeNode<float>(0, 0, 100);
    public ToggleNode IsGlobal { get; set; } = new ToggleNode(false);

    [Menu(null, "Comma-separated reward categories this modifier's reward belongs to. Empty/'None' = " +
                "not boosted by any category-specific border (only by 'All' borders). Categories: Monsters, MagicMonsters, " +
                "RareMonsters, Essences, Strongboxes, Uniques, Currency, Scarabs, Gold, Equipment, Experience, Resources, Lanterns, Rarity")]
    public TextNode Tags { get; set; } = new TextNode("");

    public ColorNode HighlightColor { get; set; } = Color.Violet;

    public override string ToString()
    {
        var tags = ModifierTagParser.Parse(Tags.Value, ModifierTag.None);
        return $"{Id.Value} {Weight.Value} ({tags})###";
    }
}

public enum SearchState
{
    Empty,
    Searching,
    Stopped,
}
