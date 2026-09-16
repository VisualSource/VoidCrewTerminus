using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace VoidCrewTerminus;

internal static class TerminusConfig
{
    // Every entry is a triplet: a `const` default, the ConfigEntry bound to it, and a typed
    // accessor. A ConfigEntry is null until Init() runs (never under the test host), so read
    // config through the accessor, never `Entry?.Value ?? default` at the call site.

    // Fields below are assigned via reflection in Init(); suppress "never assigned".
#pragma warning disable CS0649

    [BindConfig("ui", false, "Control if relics are allowed to be created from the fabricator")]
    internal static ConfigEntry<bool> AllowRelicReplication;

    [BindConfig("dev", false, "Enable dev mode")]
    internal static ConfigEntry<bool> EnableDevMode;

    // Unbound reads as false so a dev command can never fire before Init() has run.
    internal static bool DevMode => EnableDevMode?.Value ?? false;

    private const float DefaultLobbyShipFadeDuration = 0.6f;
    [BindConfig("lobby", DefaultLobbyShipFadeDuration, "Duration fade affect between ship when after ship selection")]
    internal static ConfigEntry<float> LobbyShipFadeDuration;
    internal static float ShipFadeDuration => LobbyShipFadeDuration?.Value ?? DefaultLobbyShipFadeDuration;

    private const float DefaultLobbyShipBuildBudgetMs = 3f;
    [BindConfig("lobby", DefaultLobbyShipBuildBudgetMs, "Per-frame time budget (ms) for building hangar ship visuals during preload; lower = smoother scene start, slower build")]
    internal static ConfigEntry<float> LobbyShipBuildBudgetMs;
    internal static float ShipBuildBudgetMs => LobbyShipBuildBudgetMs?.Value ?? DefaultLobbyShipBuildBudgetMs;

    private const string DefaultForgeCostCurve = "1,1,2,2,3,3,4";
    [BindConfig("forge", DefaultForgeCostCurve, "Comma-separated relic cost per module level step L4..L10 (default: 1,1,2,2,3,3,4 = 16 total to hit L10)")]
    internal static ConfigEntry<string> ForgeCostCurve;
    internal static string CostCurveRaw => ForgeCostCurve?.Value ?? DefaultForgeCostCurve;

    private const float DefaultForgeMeterPerSectorJump = 20f;
    [BindConfig("forge", DefaultForgeMeterPerSectorJump, "Forge Meter fill per successful sector jump")]
    internal static ConfigEntry<float> ForgeMeterPerSectorJump;
    internal static float MeterPerSectorJump => ForgeMeterPerSectorJump?.Value ?? DefaultForgeMeterPerSectorJump;

    private const float DefaultForgeMeterPerAlloy = 1f;
    [BindConfig("forge", DefaultForgeMeterPerAlloy, "Forge Meter fill per alloy spent at the Alloy Terminal")]
    internal static ConfigEntry<float> ForgeMeterPerAlloy;
    internal static float MeterPerAlloy => ForgeMeterPerAlloy?.Value ?? DefaultForgeMeterPerAlloy;

    private const int DefaultAlloyTerminalSpendPerUse = 10;
    [BindConfig("forge", DefaultAlloyTerminalSpendPerUse, "Alloys consumed per Alloy Terminal use")]
    internal static ConfigEntry<int> AlloyTerminalSpendPerUse;
    internal static int AlloySpendPerUse => AlloyTerminalSpendPerUse?.Value ?? DefaultAlloyTerminalSpendPerUse;

    private const float DefaultForgeMeterBaseThreshold = 100f;
    [BindConfig("forge", DefaultForgeMeterBaseThreshold, "Forge Meter threshold for L1→L2; multiplied by ForgeMeterLevelMultiplier each subsequent level")]
    internal static ConfigEntry<float> ForgeMeterBaseThreshold;
    internal static float MeterBaseThreshold => ForgeMeterBaseThreshold?.Value ?? DefaultForgeMeterBaseThreshold;

    private const float DefaultForgeMeterLevelMultiplier = 1.5f;
    [BindConfig("forge", DefaultForgeMeterLevelMultiplier, "Multiplicative scale applied to meter threshold per Forge level")]
    internal static ConfigEntry<float> ForgeMeterLevelMultiplier;
    internal static float MeterLevelMultiplier => ForgeMeterLevelMultiplier?.Value ?? DefaultForgeMeterLevelMultiplier;

    private const float DefaultPerkRollChanceCommon = 0.25f;
    [BindConfig("forge", DefaultPerkRollChanceCommon, "Perk roll chance when upgrading with a Common relic")]
    internal static ConfigEntry<float> PerkRollChanceCommon;
    internal static float PerkChanceCommon => PerkRollChanceCommon?.Value ?? DefaultPerkRollChanceCommon;

    private const float DefaultPerkRollChanceRare = 0.40f;
    [BindConfig("forge", DefaultPerkRollChanceRare, "Perk roll chance when upgrading with a Rare relic")]
    internal static ConfigEntry<float> PerkRollChanceRare;
    internal static float PerkChanceRare => PerkRollChanceRare?.Value ?? DefaultPerkRollChanceRare;

    private const float DefaultPerkRollChanceLegendary = 0.75f;
    [BindConfig("forge", DefaultPerkRollChanceLegendary, "Perk roll chance when upgrading with a Legendary relic")]
    internal static ConfigEntry<float> PerkRollChanceLegendary;
    internal static float PerkChanceLegendary => PerkRollChanceLegendary?.Value ?? DefaultPerkRollChanceLegendary;

    // The three enemy-pressure knobs below are read together on every scaling path, via
    // EscalationIntensity.Current rather than individually.
    private const float DefaultEscalationStatScalarPerJump = 0.05f;
    [BindConfig("forge", DefaultEscalationStatScalarPerJump, "Fractional multiplier added to enemy HP and damage per DifficultyScalar tick (minor boost — density is the primary axis)")]
    internal static ConfigEntry<float> EscalationStatScalarPerJump;
    internal static float StatScalarPerJump => EscalationStatScalarPerJump?.Value ?? DefaultEscalationStatScalarPerJump;

    private const float DefaultEscalationDensityScalarPerJump = 0.12f;
    [BindConfig("forge", DefaultEscalationDensityScalarPerJump, "Fractional multiplier added to enemy spawner intensity per DifficultyScalar tick (primary escalation axis — deeper sectors bring more enemies). At the default cap of 10, density tops out at 1 + 10*0.12 = 2.2x.")]
    internal static ConfigEntry<float> EscalationDensityScalarPerJump;
    internal static float DensityScalarPerJump => EscalationDensityScalarPerJump?.Value ?? DefaultEscalationDensityScalarPerJump;

    private const int DefaultEscalationScalarCap = 10;
    [BindConfig("forge", DefaultEscalationScalarCap, "Upper cap on the effective DifficultyScalar used for ENEMY scaling (density + HP + damage). Raw scalar keeps climbing (loot tier / display), but enemy pressure plateaus here so deep runs stay survivable and don't spawn an unbounded number of networked ships. Set 0 to disable the cap (uncapped linear growth).")]
    internal static ConfigEntry<int> EscalationScalarCap;
    internal static int ScalarCap => EscalationScalarCap?.Value ?? DefaultEscalationScalarCap;

    private const int DefaultEscalationBossActivationThreshold = 2;
    [BindConfig("forge", DefaultEscalationBossActivationThreshold, "Number of boss objectives that must be defeated in a run before any escalation (density, HP, damage, loot tier biasing) takes effect. DifficultyScalar and BossesDefeated still accumulate during the warm-up so scaling kicks in with full accumulated intensity once the threshold is crossed.")]
    internal static ConfigEntry<int> EscalationBossActivationThreshold;
    internal static int BossActivationThreshold => EscalationBossActivationThreshold?.Value ?? DefaultEscalationBossActivationThreshold;

    private const float DefaultRelicBaseCurseChance = 0.15f;
    [BindConfig("forge", DefaultRelicBaseCurseChance, "Base chance (0-1) that a spawned relic is flagged as Cursed. Per-relic modifiers in RelicTierData.BaseCurseChanceModifier are added on top, plus a DifficultyScalar bonus. Applies from the first sector — curses are NOT gated on the escalation boss threshold.")]
    internal static ConfigEntry<float> RelicBaseCurseChance;
    internal static float BaseCurseChance => RelicBaseCurseChance?.Value ?? DefaultRelicBaseCurseChance;

    private const float DefaultEscalationCurseChancePerScalar = 0.03f;
    [BindConfig("forge", DefaultEscalationCurseChancePerScalar, "Additional cursed chance per DifficultyScalar tick — deeper sectors produce more cursed relics. Note DifficultyScalar only starts climbing once escalation activates, so in practice this is flat during warm-up.")]
    internal static ConfigEntry<float> EscalationCurseChancePerScalar;
    internal static float CurseChancePerScalar => EscalationCurseChancePerScalar?.Value ?? DefaultEscalationCurseChancePerScalar;

    private const float DefaultRelicMaxCurseChance = 0.50f;
    [BindConfig("forge", DefaultRelicMaxCurseChance, "Hard ceiling (0-1) on the final cursed chance, applied after base + per-relic modifier + scalar bonus. Without it the uncapped DifficultyScalar drives curse chance to 100% in deep runs (every relic cursed). Set 1 to disable the ceiling.")]
    internal static ConfigEntry<float> RelicMaxCurseChance;
    internal static float MaxCurseChance => RelicMaxCurseChance?.Value ?? DefaultRelicMaxCurseChance;

    // When a cursed relic is consumed in a successful commit, an independent roll
    // decides whether the module also takes on a burden. Perk roll is unaffected.
    private const float DefaultBurdenApplicationChance = 0.75f;
    [BindConfig("forge", DefaultBurdenApplicationChance, "Chance a successful commit consuming ≥1 cursed relic attaches the relic's baked Maintenance Burden to the target module — 'high chance' per design intent")]
    internal static ConfigEntry<float> BurdenApplicationChance;
    internal static float BurdenChance => BurdenApplicationChance?.Value ?? DefaultBurdenApplicationChance;

    // Deconstruct has no knob on purpose — it is measured off a live vanilla
    // ExtruderLever at runtime (ForgeHoldGate.VanillaDeconstructSeconds) so it tracks
    // whatever the game ships. Commit has no vanilla counterpart to match, so it gets
    // a knob to tune by feel.
    private const float DefaultForgeCommitHoldSeconds = 2f;
    [BindConfig("forge", DefaultForgeCommitHoldSeconds, "Seconds the Commit lever must be held to fire. Committing is irreversible and consumes relics, so this is deliberately longer than the generic hold prompt. Deconstruct is not configurable — it matches vanilla's module deconstruct lever.")]
    internal static ConfigEntry<float> ForgeCommitHoldSeconds;
    internal static float CommitHoldSeconds => ForgeCommitHoldSeconds?.Value ?? DefaultForgeCommitHoldSeconds;

    private const float DefaultBurdenIntervalMinSeconds = 30f;
    [BindConfig("forge", DefaultBurdenIntervalMinSeconds, "RandomShutoff burden — minimum seconds between shutoff events (the burden only turns the module OFF; the crew restores it manually)")]
    internal static ConfigEntry<float> BurdenIntervalMinSeconds;
    internal static float BurdenMinInterval => BurdenIntervalMinSeconds?.Value ?? DefaultBurdenIntervalMinSeconds;

    private const float DefaultBurdenIntervalMaxSeconds = 90f;
    [BindConfig("forge", DefaultBurdenIntervalMaxSeconds, "RandomShutoff burden — maximum seconds between shutoff events")]
    internal static ConfigEntry<float> BurdenIntervalMaxSeconds;
    internal static float BurdenMaxInterval => BurdenIntervalMaxSeconds?.Value ?? DefaultBurdenIntervalMaxSeconds;

    private const float DefaultBurdenRestoreGraceSeconds = 20f;
    [BindConfig("forge", DefaultBurdenRestoreGraceSeconds, "RandomShutoff burden — minimum seconds of uptime after the crew restores power before the burden may cut it again. Guards against a shutoff landing immediately after someone walks over and switches the module back on")]
    internal static ConfigEntry<float> BurdenRestoreGraceSeconds;
    internal static float BurdenRestoreGrace => BurdenRestoreGraceSeconds?.Value ?? DefaultBurdenRestoreGraceSeconds;

    private const int DefaultEscalationRareUnlockScalar = 3;
    [BindConfig("forge", DefaultEscalationRareUnlockScalar, "DifficultyScalar at which Rare relics start dropping (below this, Rares in the loot pool are downgraded to Common)")]
    internal static ConfigEntry<int> EscalationRareUnlockScalar;
    internal static int RareUnlockScalar => EscalationRareUnlockScalar?.Value ?? DefaultEscalationRareUnlockScalar;

    private const int DefaultEscalationLegendaryUnlockScalar = 6;
    [BindConfig("forge", DefaultEscalationLegendaryUnlockScalar, "DifficultyScalar at which Legendary relics start dropping (below this, Legendaries in the loot pool are downgraded to Rare)")]
    internal static ConfigEntry<int> EscalationLegendaryUnlockScalar;
    internal static int LegendaryUnlockScalar => EscalationLegendaryUnlockScalar?.Value ?? DefaultEscalationLegendaryUnlockScalar;

    private const int DefaultEscalationBossScalarBonus = 1;
    [BindConfig("forge", DefaultEscalationBossScalarBonus, "DifficultyScalar bump applied when a boss objective is defeated (in addition to the boss's tier-ceiling unlock)")]
    internal static ConfigEntry<int> EscalationBossScalarBonus;
    internal static int BossScalarBonus => EscalationBossScalarBonus?.Value ?? DefaultEscalationBossScalarBonus;

    // Leech Carrier encounter. Every value here is a playtest starter, not a
    // tuned figure. The *Bands entries are three comma-separated steps covering
    // DifficultyScalar 2-3 / 4-5 / 6+; below LeechGateScalar the encounter is
    // inert and no band applies.

    private const int DefaultLeechGateScalar = 2;
    [BindConfig("leech", DefaultLeechGateScalar, "DifficultyScalar at or above which Leech Carriers arm. Below this the host patch stays inert, so lowering it is the supported way to test the encounter early")]
    internal static ConfigEntry<int> LeechGateScalar;
    internal static int LeechGate => LeechGateScalar?.Value ?? DefaultLeechGateScalar;

    private const int DefaultLeechConcurrencyCap = 8;
    [BindConfig("leech", DefaultLeechConcurrencyCap, "Concurrency Safety Rail — hard cap on Leeches attached to the ship at once. A missile impacting a saturated ship still plays VFX but deploys nothing, which keeps a runaway swarm from bricking a run")]
    internal static ConfigEntry<int> LeechConcurrencyCap;
    internal static int LeechCap => LeechConcurrencyCap?.Value ?? DefaultLeechConcurrencyCap;

    private const float DefaultLeechHullFloorPercent = 0.25f;
    [BindConfig("leech", DefaultLeechHullFloorPercent, "Hull Floor — fraction of ship max HP below which Hull-Biter chomps cannot push the ship. Leeches strip survival margin but never land the killing blow; ordinary combat damage still kills freely. Set 0 to let Leeches kill the ship")]
    internal static ConfigEntry<float> LeechHullFloorPercent;
    internal static float LeechHullFloor => LeechHullFloorPercent?.Value ?? DefaultLeechHullFloorPercent;

    private const float DefaultLeechVariantProximityRadius = 1.5f;
    [BindConfig("leech", DefaultLeechVariantProximityRadius, "Distance in world units from a module's authored bounding box within which an anchor classifies as Module-Biter rather than Hull-Biter. TUNE DOWNWARD ONLY — at 2.0 and above Hull-Biters stop occurring entirely, because the grid spacing leaves no bare hull outside the radius")]
    internal static ConfigEntry<float> LeechVariantProximityRadius;
    internal static float LeechProximityRadius => LeechVariantProximityRadius?.Value ?? DefaultLeechVariantProximityRadius;

    // Missile baselines. The band multipliers below scale these.
    private const float DefaultLeechMissileBaseSpeed = 50f;
    [BindConfig("leech", DefaultLeechMissileBaseSpeed, "Leech Missile speed in m/s before band scaling. Matches the vanilla SyncedProjectile default; point-defense tracks from 525 m with a ~2.25 s engagement cycle, so this value sets how many PD shots a missile eats on approach (~4 at 50 m/s)")]
    internal static ConfigEntry<float> LeechMissileBaseSpeed;
    internal static float LeechMissileSpeed => LeechMissileBaseSpeed?.Value ?? DefaultLeechMissileBaseSpeed;

    private const float DefaultLeechMissileArcLength = 500f;
    [BindConfig("leech", DefaultLeechMissileArcLength, "Leech Missile turn radius in world units, matching the vanilla GuidedProjectile default. This is an INVERSE control: vanilla derives angular velocity as distance/arcLength, so a SMALLER value turns harder. The LeechMissileTurnRateBands multipliers divide into this")]
    internal static ConfigEntry<float> LeechMissileArcLength;
    internal static float LeechMissileTurnArc => LeechMissileArcLength?.Value ?? DefaultLeechMissileArcLength;

    private const int DefaultLeechMissileBaseHitPoints = 2;
    [BindConfig("leech", DefaultLeechMissileBaseHitPoints, "Leech Missile hit points before band scaling. This is a HIT COUNTER, not a damage pool — point-defense hardcodes its damage to float.MaxValue, so each intercept costs exactly one point. Above 3-4 effective HP the missile becomes PD-immune rather than PD-resistant, so this compounds with LeechMissileBaseSpeed and the two cannot be tuned independently")]
    internal static ConfigEntry<int> LeechMissileBaseHitPoints;
    internal static int LeechMissileHitPoints => LeechMissileBaseHitPoints?.Value ?? DefaultLeechMissileBaseHitPoints;

    private const string DefaultLeechMissileVisualPrefab = "Projectile_HollowRocket_Fighter";
    [BindConfig("leech", DefaultLeechMissileVisualPrefab, "Vanilla NPC projectile prefab (Objects/Projectiles/*) to borrow the Leech Missile's mesh and trail from. Only the visuals are used; the clone's own projectile logic, collider and PhotonView are stripped. Blank falls back to a plain capsule. !leechvisual lists what is loadable and switches this mid-run")]
    internal static ConfigEntry<string> LeechMissileVisualPrefab;
    internal static string LeechMissileVisualName => LeechMissileVisualPrefab?.Value ?? DefaultLeechMissileVisualPrefab;

    // Damage is expressed as a fraction of the target's max HP because module
    // hit points are per-prefab asset data — an absolute figure would gut a
    // Small utility module and barely scratch a Large reactor.
    private const float DefaultLeechModuleDamagePercentPerTick = 0.02f;
    [BindConfig("leech", DefaultLeechModuleDamagePercentPerTick, "Module-Biter damage per tick as a fraction of the target module's max HP. Stacks — every Leech on a module ticks independently, unlike the effectiveness debuff which applies once")]
    internal static ConfigEntry<float> LeechModuleDamagePercentPerTick;
    internal static float LeechModuleDamagePerTick => LeechModuleDamagePercentPerTick?.Value ?? DefaultLeechModuleDamagePercentPerTick;

    private const float DefaultLeechModuleDamageTickSeconds = 5f;
    [BindConfig("leech", DefaultLeechModuleDamageTickSeconds, "Seconds between Module-Biter damage ticks")]
    internal static ConfigEntry<float> LeechModuleDamageTickSeconds;
    internal static float LeechModuleTickInterval => LeechModuleDamageTickSeconds?.Value ?? DefaultLeechModuleDamageTickSeconds;

    private const float DefaultLeechHullDamagePercentPerChomp = 0.015f;
    [BindConfig("leech", DefaultLeechHullDamagePercentPerChomp, "Hull-Biter damage per chomp as a fraction of ship max HP. Routes through the ship's normal damage path so resistances apply and breaches accrue by vanilla's own accounting — a Leech never promotes a breach itself, because repairing one restores 10-20% of max HP and would net-heal the ship")]
    internal static ConfigEntry<float> LeechHullDamagePercentPerChomp;
    internal static float LeechHullDamagePerChomp => LeechHullDamagePercentPerChomp?.Value ?? DefaultLeechHullDamagePercentPerChomp;

    // Chomp cadence is a range rather than a fixed tick so several Hull-Biters
    // don't bite in lockstep. Deliberately NOT band-scaled: chomp damage already
    // scales, and compounding cadence on top inverts the intent.
    private const float DefaultLeechChompIntervalMinSeconds = 6f;
    [BindConfig("leech", DefaultLeechChompIntervalMinSeconds, "Hull-Biter — minimum seconds between chomps. Host-scheduled")]
    internal static ConfigEntry<float> LeechChompIntervalMinSeconds;
    internal static float LeechChompMinInterval => LeechChompIntervalMinSeconds?.Value ?? DefaultLeechChompIntervalMinSeconds;

    private const float DefaultLeechChompIntervalMaxSeconds = 10f;
    [BindConfig("leech", DefaultLeechChompIntervalMaxSeconds, "Hull-Biter — maximum seconds between chomps")]
    internal static ConfigEntry<float> LeechChompIntervalMaxSeconds;
    internal static float LeechChompMaxInterval => LeechChompIntervalMaxSeconds?.Value ?? DefaultLeechChompIntervalMaxSeconds;

    private const float DefaultLeechFleeLerpSeconds = 2.5f;
    [BindConfig("leech", DefaultLeechFleeLerpSeconds, "Seconds a Leech takes to crawl to its new anchor after a Containment Failure. v1 is a straight lerp; procedural leg IK is deferred")]
    internal static ConfigEntry<float> LeechFleeLerpSeconds;
    internal static float LeechFleeDuration => LeechFleeLerpSeconds?.Value ?? DefaultLeechFleeLerpSeconds;

    private const string DefaultLeechSpawnCountMinBands = "1,1,2";
    [BindConfig("leech", DefaultLeechSpawnCountMinBands, "Minimum Leeches deployed per missile impact, per band")]
    internal static ConfigEntry<string> LeechSpawnCountMinBands;
    internal static string LeechSpawnMinRaw => LeechSpawnCountMinBands?.Value ?? DefaultLeechSpawnCountMinBands;

    private const string DefaultLeechSpawnCountMaxBands = "2,3,3";
    [BindConfig("leech", DefaultLeechSpawnCountMaxBands, "Maximum Leeches deployed per missile impact, per band")]
    internal static ConfigEntry<string> LeechSpawnCountMaxBands;
    internal static string LeechSpawnMaxRaw => LeechSpawnCountMaxBands?.Value ?? DefaultLeechSpawnCountMaxBands;

    private const string DefaultLeechMissileTurnRateBands = "1.0,1.3,1.6";
    [BindConfig("leech", DefaultLeechMissileTurnRateBands, "Turn-rate multiplier per band. Applied as a divisor on LeechMissileArcLength, so a higher multiplier turns harder")]
    internal static ConfigEntry<string> LeechMissileTurnRateBands;
    internal static string LeechTurnRateRaw => LeechMissileTurnRateBands?.Value ?? DefaultLeechMissileTurnRateBands;

    private const string DefaultLeechMissileHitPointBands = "1.0,1.5,2.0";
    [BindConfig("leech", DefaultLeechMissileHitPointBands, "Hit-point multiplier per band, rounded to a whole intercept count. See LeechMissileBaseHitPoints for why this saturates fast")]
    internal static ConfigEntry<string> LeechMissileHitPointBands;
    internal static string LeechMissileHitPointRaw => LeechMissileHitPointBands?.Value ?? DefaultLeechMissileHitPointBands;

    private const string DefaultLeechMissileSpeedBands = "1.0,1.15,1.3";
    [BindConfig("leech", DefaultLeechMissileSpeedBands, "Speed multiplier per band")]
    internal static ConfigEntry<string> LeechMissileSpeedBands;
    internal static string LeechMissileSpeedRaw => LeechMissileSpeedBands?.Value ?? DefaultLeechMissileSpeedBands;

    private const string DefaultLeechHostFireIntervalBands = "30,25,20";
    [BindConfig("leech", DefaultLeechHostFireIntervalBands, "Seconds between Leech Missile launches from one Leech Carrier, per band")]
    internal static ConfigEntry<string> LeechHostFireIntervalBands;
    internal static string LeechFireIntervalRaw => LeechHostFireIntervalBands?.Value ?? DefaultLeechHostFireIntervalBands;

    private const string DefaultLeechContainmentPromptBands = "1,2,3";
    [BindConfig("leech", DefaultLeechContainmentPromptBands, "Containment Prompts issued during one removal hold, per band")]
    internal static ConfigEntry<string> LeechContainmentPromptBands;
    internal static string LeechPromptCountRaw => LeechContainmentPromptBands?.Value ?? DefaultLeechContainmentPromptBands;

    private const string DefaultLeechPromptWindowBands = "1.5,1.2,1.0";
    [BindConfig("leech", DefaultLeechPromptWindowBands, "Seconds a crewmember has to answer a Containment Prompt, per band. Missing one frees the Leech, capped at one escape each")]
    internal static ConfigEntry<string> LeechPromptWindowBands;
    internal static string LeechPromptWindowRaw => LeechPromptWindowBands?.Value ?? DefaultLeechPromptWindowBands;

    private const string DefaultLeechHoldDurationBands = "3,4,5";
    [BindConfig("leech", DefaultLeechHoldDurationBands, "Seconds of sustained multi-tool hold needed to remove a Leech, per band")]
    internal static ConfigEntry<string> LeechHoldDurationBands;
    internal static string LeechHoldDurationRaw => LeechHoldDurationBands?.Value ?? DefaultLeechHoldDurationBands;

    private const string DefaultLeechModuleDebuffMagnitudeBands = "0.25,0.40,0.50";
    [BindConfig("leech", DefaultLeechModuleDebuffMagnitudeBands, "Fractional effectiveness penalty a Module-Biter inflicts, per band. Non-stacking — several Leeches on one module apply this once")]
    internal static ConfigEntry<string> LeechModuleDebuffMagnitudeBands;
    internal static string LeechDebuffMagnitudeRaw => LeechModuleDebuffMagnitudeBands?.Value ?? DefaultLeechModuleDebuffMagnitudeBands;

    private const string DefaultLeechModuleDamageRateBands = "1.0,1.5,2.0";
    [BindConfig("leech", DefaultLeechModuleDamageRateBands, "Multiplier on LeechModuleDamagePercentPerTick, per band")]
    internal static ConfigEntry<string> LeechModuleDamageRateBands;
    internal static string LeechModuleDamageRateRaw => LeechModuleDamageRateBands?.Value ?? DefaultLeechModuleDamageRateBands;

    private const string DefaultLeechHullChompDamageBands = "1.0,1.5,2.0";
    [BindConfig("leech", DefaultLeechHullChompDamageBands, "Multiplier on LeechHullDamagePercentPerChomp, per band")]
    internal static ConfigEntry<string> LeechHullChompDamageBands;
    internal static string LeechChompDamageRateRaw => LeechHullChompDamageBands?.Value ?? DefaultLeechHullChompDamageBands;

#pragma warning restore CS0649

    internal static ConfigEntry<Vector3> FrigateLobbyHangerPosition;
    internal static ConfigEntry<Vector3> StrikerLobbyHangerPosition;
    internal static ConfigEntry<Vector3> DestroyerLobbyHangerPosition;

    internal static ConfigEntry<Vector3> FrigateLobbyHangerRot;
    internal static ConfigEntry<Vector3> StrikerLobbyHangerRot;
    internal static ConfigEntry<Vector3> DestroyerLobbyHangerRot;


    internal static void Init(ConfigFile cfg)
    {
        BindAttributedFields(cfg);

        FrigateLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerPosition = cfg.Bind("lobby", "StrikerLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");

        FrigateLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 20, 0), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerRot = cfg.Bind("lobby", "StrikerLobbyHangerRot", new Vector3(0, 0, 0), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 20, 0), "Position of the frigate prefab in the lobby");
    }

    // Split from Init because the lobby binds above construct a UnityEngine.Vector3,
    // whose GameLibs reference stub throws under the test host. Everything
    // attribute-driven stays reachable headlessly.
    internal static void BindAttributedFields(ConfigFile cfg)
    {
        Type type = typeof(TerminusConfig);

        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var attr = field.GetCustomAttribute<BindConfig>();
            if (attr == null) continue;

            if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(ConfigEntry<>))
                throw new InvalidOperationException(
                    $"[BindConfig] on {field.Name}: field must be ConfigEntry<T>, got {field.FieldType}");

            Type expected = field.FieldType.GetGenericArguments()[0];

            if (attr.DefaultValue == null || attr.DefaultValue.GetType() != expected)
                throw new InvalidOperationException(
                    $"[BindConfig] on {field.Name}: default value type " +
                    $"{attr.DefaultValue?.GetType()?.ToString() ?? "null"} " +
                    $"does not match ConfigEntry<{expected}>");

            MethodInfo bindMethod = typeof(ConfigFile).GetMethods()
                .First(m => m.Name == nameof(ConfigFile.Bind)
                    && m.IsGenericMethod
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[3].ParameterType == typeof(ConfigDescription))
                .MakeGenericMethod(expected);

            object entry = bindMethod.Invoke(cfg, new object[]
            {
                attr.Section,
                field.Name,
                attr.DefaultValue,
                new ConfigDescription(attr.Description, attr.AcceptableValues, attr.Tags),
            });

            field.SetValue(null, entry);
        }
    }
}


[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
internal class BindConfig : Attribute
{
    public string Section { get; set; } = "";
    public string Description { get; set; } = "";
    public object DefaultValue { get; set; }
    public object[] Tags { get; set; }
    public AcceptableValueBase AcceptableValues { get; set; } = null;

    public BindConfig(string section, object defaultValue, string desc)
    {
        Section = section;
        DefaultValue = defaultValue;
        Description = desc;
    }

}