// Copies of the two plain types the combat loops use from Simulation/CombatEngine.cs (which this test does not
// compile: it is the game-bound engine). Kept member for member as declared there.

namespace CombatSimulator.Simulation
{
    public class SimulatedActionResult
    {
        public bool Success { get; set; }
        public string? FailReason { get; set; }
        public int Damage { get; set; }
        public int Healing { get; set; }
        public bool IsCritical { get; set; }
        public bool IsDirectHit { get; set; }
        public bool IsCombo { get; set; }
        public uint ActionId { get; set; }
        public uint SourceId { get; set; }
        public ulong TargetId { get; set; }
        public bool TargetKilled { get; set; }
    }

    public enum CombatLogType
    {
        DamageDealt,
        DamageTaken,
        Healing,
        Miss,
        Death,
        Info,
    }
}

// Namespaces the controllers' files import for game-side types this test does not compile (MovementBlockHook,
// the Integration helpers): declared here so the imports resolve.
namespace CombatSimulator.Safety { }
namespace CombatSimulator.Integration { }
