using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Shared "wait for the follower" check for leader-side modes (Simulacrum, Boss, ...).
    /// A follower counts when it's a living player entity other than ourselves whose name is in
    /// Settings.FollowerGate.FollowerNames (an empty list accepts any other living player).
    /// Detection mirrors FollowerMode.FindLeader (EntityType.Player + Player.PlayerName);
    /// a dead follower deliberately does NOT count, since that's exactly when the leader dies too.
    ///
    /// Each mode owns one instance and gates on its own "Wait For Follower" toggle — this class
    /// deliberately doesn't know about that toggle, so it stays usable from anywhere.
    /// </summary>
    public sealed class FollowerGate
    {
        // Cached split of the comma-separated name setting so we don't allocate per check.
        private string _namesRaw = "";
        private string[] _names = Array.Empty<string>();

        // Scan throttle. Callers may run this every tick; the scan walks the entity list, so keep
        // it in line with the rest of the per-tick entity work in this project. 200ms is far below
        // any reaction time that matters here.
        private DateTime _lastCheckAt = DateTime.MinValue;
        private const float CheckIntervalMs = 200f;

        /// <summary>Distance to the nearest usable follower at the last scan, null if none.</summary>
        public float? LastDistance { get; private set; }

        /// <summary>A usable follower is somewhere in the current area, at any distance.</summary>
        public bool IsInArea(BotContext ctx)
        {
            UpdateDistance(ctx);
            return LastDistance.HasValue;
        }

        /// <summary>A usable follower is in the area AND within the configured max distance.</summary>
        public bool IsInRange(BotContext ctx)
        {
            UpdateDistance(ctx);
            return LastDistance.HasValue
                && LastDistance.Value <= ctx.Settings.FollowerGate.FollowerMaxDistance.Value;
        }

        /// <summary>
        /// Status suffix telling apart the two cases a gate blocks on, for the mode's StatusText.
        /// </summary>
        public string WaitReason(BotContext ctx)
        {
            return LastDistance.HasValue
                ? $"follower too far away (dist: {LastDistance.Value:F0} > {ctx.Settings.FollowerGate.FollowerMaxDistance.Value:F0})"
                : "follower not in area";
        }

        /// <summary>
        /// Refresh <see cref="LastDistance"/> with the distance to the nearest usable follower.
        /// Throttled to CheckIntervalMs.
        /// </summary>
        private void UpdateDistance(BotContext ctx)
        {
            // Plain time throttle — deliberately NOT ModeHelpers.CanAct(), which also gates on
            // BotInput.CanAct and would freeze this scan while input is blocked.
            if ((DateTime.Now - _lastCheckAt).TotalMilliseconds < CheckIntervalMs)
                return;
            _lastCheckAt = DateTime.Now;

            var gc = ctx.Game;
            if (gc?.Player == null) return;

            // Re-split the comma-separated list only when the setting text actually changed.
            var raw = ctx.Settings.FollowerGate.FollowerNames.Value ?? "";
            if (!string.Equals(raw, _namesRaw, StringComparison.Ordinal))
            {
                _namesRaw = raw;
                _names = raw.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            var playerPos = gc.Player.GridPosNum;
            float? nearest = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Player) continue;
                if (entity.Id == gc.Player.Id) continue; // that's us
                if (!entity.IsAlive) continue;           // a dead follower is no help
                if (!MatchesName(entity)) continue;

                var entityPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(playerPos, entityPos);
                if (!nearest.HasValue || dist < nearest.Value)
                    nearest = dist;
            }

            LastDistance = nearest;
        }

        /// <summary>Name match. An empty name list accepts any player.</summary>
        private bool MatchesName(Entity entity)
        {
            if (_names.Length == 0) return true;

            var name = entity.GetComponent<Player>()?.PlayerName;
            if (string.IsNullOrEmpty(name)) return false;

            foreach (var candidate in _names)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
