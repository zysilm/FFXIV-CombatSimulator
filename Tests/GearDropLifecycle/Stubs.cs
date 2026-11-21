using AdvancedRagdoll;
using CombatSimulator.Animation;
namespace CombatSimulator
{
    public sealed class Configuration
    {
        public bool KoStripHead, KoStripBody = true, KoStripHands, KoStripLegs, KoStripFeet,
            KoStripEars, KoStripNeck, KoStripWrists, KoStripRFinger, KoStripLFinger;
        public CombatSimulator.Dev.ArmorDetachmentTrigger KoStripTrigger = CombatSimulator.Dev.ArmorDetachmentTrigger.OnKo;
        public bool KoStripPhysicsDrop, KoStripPhysicsDropClothing = true;
        public bool KoStripAllies, KoStripEnemies;
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
        public int Removes, PersistentRemoves, StateCaptures;
        public string GetStateBase64(int index) { StateCaptures++; return "captured-outfit"; }
        public bool SetItem(int index, byte slot, ulong item, bool persist) { Removes++; if (persist) PersistentRemoves++; return true; }
    }
}
namespace CombatSimulator.Animation
{
    public sealed class DismembermentController
    {
        internal readonly List<GearDropOperation> Operations = new();
        public int Calls, FailCall = -1, Judged, RetireRequests, LastKeep = -1;
        public bool SourceUnchanged = true, Detachable = true, Equipped = true;
        internal GearDropOperation? SpawnGearDrop(nint address, string bone, int slot, string? state, bool hideSkin, string? hiddenOppositeRootBone)
        {
            if (++Calls == FailCall) return null;
            var operation = new GearDropOperation(); Operations.Add(operation); return operation;
        }
        internal bool IsGearDropSourceUnchanged(GearDropOperation operation) => SourceUnchanged;
        internal bool CanDetachArmorFrom(nint address) { Judged++; return Detachable; }
        // Stands in for the object-table lookup: the same entity still in that slot.
        internal unsafe bool IsSameLiveActor(nint address, uint objectIndex, uint entityId)
            => ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->ObjectIndex == objectIndex &&
               ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->EntityId == entityId;
        internal bool HasEquipmentInSlot(nint address, int modelSlot) => Equipped;
        internal int RetireOldestOthersGearPieces(int keep) { RetireRequests++; LastKeep = keep; return 0; }
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
