using CombatSimulator.Animation;
namespace CombatSimulator
{
    public sealed class Configuration
    {
        public bool KoStripHead, KoStripBody = true, KoStripHands, KoStripLegs, KoStripFeet,
            KoStripEars, KoStripNeck, KoStripWrists, KoStripRFinger, KoStripLFinger;
        public bool KoStripEnabled = true, KoStripOnHitEnabled, KoStripSyncWithRagdoll,
            KoStripPhysicsDrop, KoStripPhysicsDropClothing = true;
        public float RagdollActivationDelay;
    }
}
namespace CombatSimulator.Core
{
    public static class Services { public static Table ObjectTable = new(); }
    public sealed class Table { public Player? LocalPlayer; }
    public sealed class Player { public nint Address; }
}
namespace CombatSimulator.Integration
{
    public sealed class GlamourerIpc
    {
        public int Removes;
        public string GetStateBase64(int index) => "captured-outfit";
        public bool SetItem(int index, byte slot, ulong item, bool persist) { Removes++; return true; }
    }
}
namespace CombatSimulator.Animation
{
    public sealed class DismembermentController
    {
        public readonly List<GearDropOperation> Operations = new();
        public int Calls, FailCall = -1;
        public bool SourceUnchanged = true;
        public GearDropOperation? SpawnGearDrop(nint address, string bone, int slot, string? state, bool hideSkin, string? hiddenOppositeRootBone)
        {
            if (++Calls == FailCall) return null;
            var operation = new GearDropOperation(); Operations.Add(operation); return operation;
        }
        public bool IsGearDropSourceUnchanged(GearDropOperation operation) => SourceUnchanged;
    }
}
namespace Dalamud.Plugin.Services
{
    public interface IPluginLog
    {
        void Info(string message);
        void Warning(string message);
        void Warning(Exception exception, string message);
    }
    public sealed class Log : IPluginLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Warning(Exception exception, string message) { }
    }
}
