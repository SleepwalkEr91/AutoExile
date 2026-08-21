using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Searing Exarch encounter.
    ///
    /// IMPORTANT:
    /// This class intentionally remains named FearEncounter so existing
    /// AutoExile code/settings do not need to be changed.
    ///
    /// Fragment: CurrencyCleansingFireBossKey
    /// Boss: SearingExarch
    ///
    /// Strategy:
    /// - Move to the existing FearDpsPosition setting.
    /// - Find the Searing Exarch.
    /// - Let normal combat handle DPS/movement.
    /// - Track the boss position.
    /// - Detect boss death.
    /// - Navigate to the death position.
    /// - Perform the existing loot sweep.
    ///
    /// Re-entry:
    /// - If the player dies and re-enters the zone, the encounter resumes
    ///   when the Exarch becomes visible again.
    /// </summary>
    public class FearEncounter : IBossEncounter
    {
        // Keep the public encounter identity compatible with the rest
        // of the bot.
        public string Name => "Searing Exarch";

        public string Status { get; private set; } = "";

        // Searing Exarch invitation / fragment.
        private const string FragmentPath = "CurrencyCleansingFireBossKey";

        // Intentionally broad match.
        // Once the exact ExileAPI path is confirmed, this can be made exact.
        private const string BossPath = "SearingExarch";

        // Keep the existing setting name so no other files need changing.
        private static readonly Vector2 DefaultDpsPosition = new(0, 0);

        private static Vector2 GetDpsPosition(BotSettings settings)
        {
            var text = settings.Boss.FearDpsPosition?.Value;

            if (!string.IsNullOrWhiteSpace(text))
            {
                var parts = text.Split(',');

                if (parts.Length == 2 &&
                    float.TryParse(parts[0].Trim(), out var x) &&
                    float.TryParse(parts[1].Trim(), out var y))
                {
                    return new Vector2(x, y);
                }
            }

            return DefaultDpsPosition;
        }

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;

            return entity?.Path?.Contains(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;

        public int FragmentCost => 1;

        // Unlike Fear, Exarch does not need combat suppressed while
        // waiting for a special "emerge" state.
        public bool SuppressCombat =>
            _phase == FearPhase.Approaching;

        // Prevent normal combat positioning from pulling us away from
        // the boss death position while looting.
        public bool SuppressCombatPositioning =>
            _phase == FearPhase.WaitingForLoot;

        // ─────────────────────────────────────────────
        // State
        // ─────────────────────────────────────────────

        private FearPhase _phase = FearPhase.Idle;

        private DateTime _phaseStartTime;

        private Entity? _bossEntity;

        private bool _bossWasAlive;

        private bool _isReentry;

        private Vector2? _bossDeathPos;

        private DateTime _lastLootScan;

        private enum FearPhase
        {
            Idle,
            Approaching,
            Fighting,
            WaitingForLoot,
        }

        // ─────────────────────────────────────────────
        // Zone Entry
        // ─────────────────────────────────────────────

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;

            var pfGrid =
                gc.IngameState?.Data?.RawPathfindingData;

            var tgtGrid =
                gc.IngameState?.Data?.RawTerrainTargetingData;

            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(
                    gc.Player.GridPosNum.X,
                    gc.Player.GridPosNum.Y);

                ctx.Exploration.Initialize(
                    pfGrid,
                    tgtGrid,
                    playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }

            // If we had previously seen a living boss, assume this
            // is a re-entry after death.
            _isReentry = _bossWasAlive;

            _phase = FearPhase.Approaching;

            _phaseStartTime = DateTime.Now;

            _bossEntity = null;

            _lastLootScan = DateTime.MinValue;

            // Do NOT clear _bossDeathPos here.
            // This allows the loot position to survive re-entry.

            Status = _isReentry
                ? "Re-entering Searing Exarch"
                : "Entered Searing Exarch";

            ctx.Log(
                $"[Fear] Searing Exarch zone entered " +
                $"(reentry={_isReentry})");
        }

        // ─────────────────────────────────────────────
        // Main Tick
        // ─────────────────────────────────────────────

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc?.Player == null)
                return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(
                gc.Player.GridPosNum.X,
                gc.Player.GridPosNum.Y);

            ctx.Exploration.Update(playerGrid);

            // Find the Exarch.
            _bossEntity = FindBoss(gc);

            // Remember that the boss has been seen alive.
            if (_bossEntity != null &&
                _bossEntity.IsAlive)
            {
                _bossWasAlive = true;
            }

            // ─────────────────────────────────────────
            // Kill detection
            // ─────────────────────────────────────────

            if (_bossWasAlive &&
                _phase != FearPhase.WaitingForLoot &&
                (_bossEntity == null ||
                 !_bossEntity.IsAlive))
            {
                // Save final boss position if available.
                if (!_bossDeathPos.HasValue &&
                    _bossEntity != null)
                {
                    _bossDeathPos =
                        _bossEntity.GridPosNum;
                }

                // If the entity disappeared before we could get
                // its position, use the player's current position.
                if (!_bossDeathPos.HasValue)
                {
                    _bossDeathPos = playerGrid;
                }

                _phase = FearPhase.WaitingForLoot;

                _phaseStartTime = DateTime.Now;

                _lastLootScan = DateTime.MinValue;

                ctx.Log(
                    $"[Fear] Searing Exarch killed — " +
                    $"looting at " +
                    $"({_bossDeathPos.Value.X:F0}," +
                    $"{_bossDeathPos.Value.Y:F0})");
            }

            // ─────────────────────────────────────────
            // State machine
            // ─────────────────────────────────────────

            switch (_phase)
            {
                case FearPhase.Approaching:
                    return TickApproaching(
                        ctx,
                        gc,
                        playerGrid);

                case FearPhase.Fighting:
                    return TickFighting(
                        ctx,
                        gc,
                        playerGrid);

                case FearPhase.WaitingForLoot:
                    return TickWaitingForLoot(
                        ctx,
                        gc,
                        playerGrid);

                default:
                    return BossEncounterResult.InProgress;
            }
        }

        // ─────────────────────────────────────────────
        // Approaching
        // ─────────────────────────────────────────────

        private BossEncounterResult TickApproaching(
            BotContext ctx,
            GameController gc,
            Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime)
                .TotalSeconds > 90)
            {
                Status =
                    "Timeout: couldn't reach Exarch";

                ctx.Log(
                    "[Fear] Exarch approach timeout");

                return BossEncounterResult.Failed;
            }

            // If the boss is already nearby, immediately switch
            // to combat.
            if (_bossEntity != null &&
                _bossEntity.IsAlive)
            {
                var bossDistance =
                    Vector2.Distance(
                        playerGrid,
                        _bossEntity.GridPosNum);

                if (bossDistance < 100)
                {
                    _phase =
                        FearPhase.Fighting;

                    _phaseStartTime =
                        DateTime.Now;

                    ctx.Log(
                        "[Fear] Searing Exarch found — fighting");

                    return BossEncounterResult.InProgress;
                }
            }

            // Keep using the existing FearDpsPosition setting.
            var dpsPos =
                GetDpsPosition(ctx.Settings);

            // If a valid position is configured, navigate there.
            //
            // We deliberately don't navigate to (0,0) when the
            // setting is missing.
            if (dpsPos != Vector2.Zero)
            {
                var distToDps =
                    Vector2.Distance(
                        playerGrid,
                        dpsPos);

                if (distToDps > 10)
                {
                    if (!ctx.Navigation.IsNavigating)
                    {
                        ctx.Navigation.NavigateTo(
                            gc,
                            dpsPos);
                    }

                    Status =
                        $"Moving to Exarch DPS position " +
                        $"({distToDps:F0}g)";

                    return BossEncounterResult.InProgress;
                }
            }

            Status = _isReentry
                ? "Re-entry — looking for Exarch"
                : "Looking for Exarch";

            return BossEncounterResult.InProgress;
        }

        // ─────────────────────────────────────────────
        // Fighting
        // ─────────────────────────────────────────────

        private BossEncounterResult TickFighting(
            BotContext ctx,
            GameController gc,
            Vector2 playerGrid)
        {
            // Give Exarch a generous timeout because the encounter
            // contains several mechanics/phases.
            if ((DateTime.Now - _phaseStartTime)
                .TotalSeconds > 600)
            {
                Status =
                    "Fight timeout (10min)";

                ctx.Log(
                    "[Fear] Exarch fight timeout");

                return BossEncounterResult.Failed;
            }

            // Boss can disappear temporarily during mechanics.
            // Do NOT treat that as a death.
            if (_bossEntity == null)
            {
                Status =
                    "Exarch not currently visible";

                return BossEncounterResult.InProgress;
            }

            if (!_bossEntity.IsAlive)
            {
                Status =
                    "Exarch death detected";

                return BossEncounterResult.InProgress;
            }

            // Continuously cache the boss position.
            _bossDeathPos =
                _bossEntity.GridPosNum;

            var bossGrid =
                _bossEntity.GridPosNum;

            var distToBoss =
                Vector2.Distance(
                    playerGrid,
                    bossGrid);

            // We don't implement any Exarch-specific combat logic here.
            // Your existing Combat system handles the actual fight.
            //
            // Only recover if the player somehow gets very far away.
            if (distToBoss > 100 &&
                !ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.NavigateTo(
                    gc,
                    bossGrid);

                Status =
                    $"Moving toward Exarch " +
                    $"({distToBoss:F0}g)";

                return BossEncounterResult.InProgress;
            }

            // Read boss HP for status display.
            var hp =
                _bossEntity.GetComponent<Life>();

            var hpPct = 0;

            if (hp != null)
            {
                hpPct =
                    hp.CurHP * 100 /
                    Math.Max(1, hp.MaxHP);
            }

            Status =
                $"Fighting Exarch — " +
                $"HP:{hpPct}% " +
                $"dist={distToBoss:F0}g";

            return BossEncounterResult.InProgress;
        }

        // ─────────────────────────────────────────────
        // Loot
        // ─────────────────────────────────────────────

        private BossEncounterResult TickWaitingForLoot(
            BotContext ctx,
            GameController gc,
            Vector2 playerGrid)
        {
            var timeout =
                ctx.Settings.Run
                    .LootSweepTimeoutSeconds.Value;

            var elapsed =
                (DateTime.Now - _phaseStartTime)
                    .TotalSeconds;

            if (elapsed > timeout)
            {
                Status =
                    "Loot sweep done";

                ctx.Log(
                    "[Fear] Exarch loot sweep timeout — " +
                    "signaling Complete");

                return BossEncounterResult.Complete;
            }

            var remaining =
                timeout - elapsed;

            var countdown =
                $"({remaining:F0}s left)";

            // ─────────────────────────────────────────
            // Navigate to boss death position
            // ─────────────────────────────────────────

            var lootPos =
                _bossDeathPos ?? playerGrid;

            var distToLoot =
                Vector2.Distance(
                    playerGrid,
                    lootPos);

            if (distToLoot > 15 &&
                !ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.NavigateTo(
                    gc,
                    lootPos);
            }

            // ─────────────────────────────────────────
            // Scan for loot
            // ─────────────────────────────────────────

            if ((DateTime.Now - _lastLootScan)
                .TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);

                _lastLootScan =
                    DateTime.Now;
            }

            // ─────────────────────────────────────────
            // Interaction busy
            // ─────────────────────────────────────────

            if (ctx.Interaction.IsBusy)
            {
                Status =
                    $"Picking up loot {countdown}";

                return BossEncounterResult.InProgress;
            }

            // ─────────────────────────────────────────
            // Pick up loot
            // ─────────────────────────────────────────

            if (ctx.Loot.HasLootNearby)
            {
                var (_, candidate) =
                    ctx.Loot.PickupNext(
                        ctx.Interaction,
                        ctx.Navigation);

                if (candidate != null)
                {
                    Status =
                        $"Looting: " +
                        $"{candidate.ItemName} " +
                        countdown;

                    return BossEncounterResult.InProgress;
                }
            }

            // ─────────────────────────────────────────
            // Label toggle
            // ─────────────────────────────────────────

            if (ctx.Loot.TogglePhase !=
                LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);

                Status =
                    $"Label toggle {countdown}";

                return BossEncounterResult.InProgress;
            }

            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);

                return BossEncounterResult.InProgress;
            }

            Status =
                $"Waiting for loot at Exarch position " +
                countdown;

            return BossEncounterResult.InProgress;
        }

        // ─────────────────────────────────────────────
        // Find Boss
        // ─────────────────────────────────────────────

        private Entity? FindBoss(GameController gc)
        {
            try
            {
                foreach (var entity in
                    gc.EntityListWrapper
                        .ValidEntitiesByType[
                            EntityType.Monster])
                {
                    if (!entity.IsHostile)
                        continue;

                    if (entity.Rarity !=
                        MonsterRarity.Unique)
                        continue;

                    if (entity.Path?.Contains(
                            BossPath,
                            StringComparison.OrdinalIgnoreCase)
                        != true)
                        continue;

                    return entity;
                }
            }
            catch (IndexOutOfRangeException)
            {
            }

            return null;
        }

        // ─────────────────────────────────────────────
        // Render
        // ─────────────────────────────────────────────

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;

            if (gc?.Player == null ||
                g == null)
                return;

            var cam =
                gc.IngameState.Camera;

            // ─────────────────────────────────────────
            // Boss marker
            // ─────────────────────────────────────────

            if (_bossEntity != null)
            {
                var screen =
                    cam.WorldToScreen(
                        _bossEntity.BoundsCenterPosNum);

                if (screen.X > -200 &&
                    screen.X < 2400)
                {
                    var color =
                        _bossEntity.IsAlive
                            ? SharpDX.Color.Red
                            : SharpDX.Color.LimeGreen;

                    var label =
                        _bossEntity.IsAlive
                            ? "SEARING EXARCH"
                            : "EXARCH DEAD";

                    g.DrawText(
                        label,
                        screen +
                            new Vector2(-50, -30),
                        color);
                }
            }

            // ─────────────────────────────────────────
            // Loot position marker
            // ─────────────────────────────────────────

            if (_phase ==
                    FearPhase.WaitingForLoot &&
                _bossDeathPos.HasValue)
            {
                var world =
                    new Vector3(
                        _bossDeathPos.Value.X * 10.88f,
                        _bossDeathPos.Value.Y * 10.88f,
                        0);

                var screen =
                    cam.WorldToScreen(world);

                if (screen.X > 0 &&
                    screen.X < 2400)
                {
                    g.DrawText(
                        "LOOT HERE",
                        screen +
                            new Vector2(-30, -20),
                        SharpDX.Color.Gold);
                }
            }

            // ─────────────────────────────────────────
            // HUD
            // ─────────────────────────────────────────

            float hudX = 20;
            float hudY = 250;
            float lineH = 18;

            var phaseColor =
                _phase switch
                {
                    FearPhase.Fighting =>
                        SharpDX.Color.Red,

                    FearPhase.WaitingForLoot =>
                        SharpDX.Color.Gold,

                    _ =>
                        SharpDX.Color.White
                };

            g.DrawText(
                $"Fear: {_phase}",
                new Vector2(hudX, hudY),
                phaseColor);

            hudY += lineH;

            g.DrawText(
                Status,
                new Vector2(hudX, hudY),
                SharpDX.Color.Gray);
        }

        // ─────────────────────────────────────────────
        // Reset
        // ─────────────────────────────────────────────

        public void Reset()
        {
            _phase =
                FearPhase.Idle;

            _bossEntity = null;

            _bossWasAlive = false;

            _isReentry = false;

            _bossDeathPos = null;

            _lastLootScan =
                DateTime.MinValue;

            Status = "";
        }
    }
}
