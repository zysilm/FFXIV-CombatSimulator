using System.Runtime.InteropServices;
using CombatSimulator;
using AdvancedRagdoll;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using CombatSimulator.Dev;
using CombatSimulator.Integration;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

unsafe class Program
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    sealed class Fixture : IDisposable
    {
        public readonly Configuration Config = new();
        public readonly GlamourerIpc Glamour = new();
        public readonly DismembermentController Drops = new();
        public readonly KoStripController Controller;
        public Character* Actor = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        CharacterBase* draw = (CharacterBase*)NativeMemory.AllocZeroed((nuint)sizeof(CharacterBase));
        public nint Address => (nint)Actor;
        // An ally or an enemy (another actor than the player), dressed the same way.
        public Character* Other = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        CharacterBase* otherDraw = (CharacterBase*)NativeMemory.AllocZeroed((nuint)sizeof(CharacterBase));
        public nint OtherAddress => (nint)Other;
        public Fixture()
        {
            ((GameObject*)Actor)->DrawObject = (DrawObject*)draw;
            draw->SlotCount = 10;
            draw->Models = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model**)NativeMemory.AllocZeroed(80);
            // Slot presence only; production controller never dereferences these model pointers.
            draw->Models[1] = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model*)1;
            ((GameObject*)Other)->DrawObject = (DrawObject*)otherDraw;
            ((GameObject*)Other)->ObjectIndex = 210;
            ((GameObject*)Other)->EntityId = 0x40000001;
            otherDraw->SlotCount = 10;
            otherDraw->Models = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model**)NativeMemory.AllocZeroed(80);
            otherDraw->Models[1] = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model*)1;
            Services.ObjectTable.LocalPlayer = new Player { Address = Address };
            Controller = new(Config, Glamour, Drops, new Dalamud.Plugin.Services.Log());
        }
        public void Dispose()
        {
            Controller.Dispose(); Services.ObjectTable.LocalPlayer = null;
            NativeMemory.Free(draw->Models); NativeMemory.Free(draw); NativeMemory.Free(Actor);
            NativeMemory.Free(otherDraw->Models); NativeMemory.Free(otherDraw); NativeMemory.Free(Other);
        }
    }
    static void Main()
    {
        var tests = new (string, Action)[]
        {
            ("non-physics stripping stays immediate", () => {
                using var f = new Fixture(); f.Config.KoStripPhysicsDropClothing = false;
                f.Controller.StripNow(f.Address);
                Check(f.Glamour.Removes == 1 && f.Drops.Calls == 0, "non-physics strip changed behavior");
            }),
            ("original remains until rendered clone is ready", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0, "hidden while clone preparing");
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1 && f.Drops.Operations[0].IsCommitted, "not committed exactly once");
            }),
            ("creation failure preserves outfit and permits retry", () => {
                using var f = new Fixture(); f.Drops.FailCall = 1; f.Controller.StripOnKo(f.Address);
                Check(f.Glamour.Removes == 0, "failed allocation stripped outfit");
                f.Controller.StripOnKo(f.Address); Check(f.Drops.Calls == 2, "once-per-KO guard blocked retry");
            }),
            ("appearance failure cancels and permits another KO attempt", () => {
                using var f = new Fixture(); f.Controller.StripOnKo(f.Address);
                f.Drops.Operations[0].Cancel(); f.Controller.Tick(.1f); f.Controller.StripOnKo(f.Address);
                Check(f.Glamour.Removes == 0 && f.Drops.Calls == 2, "failure hid outfit or poisoned next attempt");
            }),
            ("both gloves must be ready before stripping", () => {
                using var f = new Fixture(); f.Config.KoStripBody = false; f.Config.KoStripHands = true;
                f.Controller.StripNow(f.Address); f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0, "one ready glove stripped pair");
                f.Drops.Operations[1].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1 && f.Drops.Operations.All(p => p.IsCommitted), "pair not committed together");
            }),
            ("partial paired allocation rolls back", () => {
                using var f = new Fixture(); f.Config.KoStripBody = false; f.Config.KoStripFeet = true;
                f.Drops.FailCall = 2; f.Controller.StripNow(f.Address);
                Check(f.Drops.Operations[0].IsCancelled && f.Glamour.Removes == 0, "orphaned first boot or stripped failed pair");
            }),
            ("one paired appearance failure cancels both pieces", () => {
                using var f = new Fixture(); f.Config.KoStripBody = false; f.Config.KoStripHands = true;
                f.Controller.StripNow(f.Address); f.Drops.Operations[0].Ready(); f.Drops.Operations[1].Cancel();
                f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0 && f.Drops.Operations.All(p => p.IsCancelled), "failed pair partially committed");
            }),
            ("reset cancels preparation and cannot strip afterward", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); f.Controller.Reset();
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Drops.Operations[0].IsCancelled && f.Glamour.Removes == 0, "late ready callback revived reset request");
            }),
            ("manual detachment works with automatic KO disabled", () => {
                using var f = new Fixture(); f.Config.KoStripTrigger = ArmorDetachmentTrigger.Off;
                f.Controller.StripNow(f.Address); f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1, "manual preparation was not ticked");
            }),
            ("changed outfit cancels stale preparation", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); f.Drops.SourceUnchanged = false;
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0 && f.Drops.Operations[0].IsCancelled, "stripped newer outfit");
            }),
            ("loading timeout preserves outfit", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); f.Controller.Tick(9);
                Check(f.Glamour.Removes == 0 && f.Drops.Operations[0].IsCancelled, "timeout left pending request");
            }),
            ("logout cancels before dereferencing source", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); Services.ObjectTable.LocalPlayer = null;
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0 && f.Drops.Operations[0].IsCancelled, "committed after logout");
            }),
            ("repeated manual click only prepares once until completion", () => {
                using var f = new Fixture(); f.Controller.StripNow(f.Address); f.Controller.StripNow(f.Address);
                Check(f.Drops.Calls == 1, "duplicate pending requests");
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f); f.Controller.StripNow(f.Address);
                Check(f.Drops.Calls == 2, "completed drop suppressed next manual request");
            }),
            // Allies and enemies: the player's settings, behind their own checkbox.
            ("others need their checkbox and the player's trigger", () => {
                using var f = new Fixture();
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 0, "enemy stripped with Enemies unticked");
                f.Config.KoStripEnemies = true; f.Config.KoStripTrigger = ArmorDetachmentTrigger.Off;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 0, "enemy stripped with the trigger Off");
                f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnKo;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 1, "enemy not stripped on KO");
            }),
            ("allies and enemies are ticked separately", () => {
                using var f = new Fixture(); f.Config.KoStripAllies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 0, "Allies ticked stripped an enemy");
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: true);
                Check(f.Drops.Calls == 1, "ally not stripped");
            }),
            ("a non-humanoid is left alone and judged once", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true; f.Drops.Detachable = false;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 0 && f.Drops.Judged == 1, "non-humanoid stripped or judged on every KO");
            }),
            ("others are not kept stripped in Glamourer and not cloned from its state", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1 && f.Glamour.PersistentRemoves == 0 && f.Glamour.StateCaptures == 0,
                    "enemy strip persisted in or captured from Glamourer");
            }),
            ("the player's strip still persists in Glamourer and skips the humanoid judge", () => {
                using var f = new Fixture(); f.Controller.StripOnKo(f.Address);
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.PersistentRemoves == 1 && f.Drops.Judged == 0 && f.Drops.RetireRequests == 0,
                    "player path changed");
            }),
            ("another actor in the slot cancels an enemy's preparation", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                ((GameObject*)f.Other)->EntityId = 0x40000002;
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 0 && f.Drops.Operations[0].IsCancelled, "stripped the next actor");
            }),
            ("a recycled address is judged afresh", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                ((GameObject*)f.Other)->EntityId = 0x40000002;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 2 && f.Drops.Judged == 2, "new actor inherited the old one's guard");
            }),
            ("others' drops ask for room under the cap", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.RetireRequests == 1 && f.Drops.LastKeep == KoStripController.MaxOthersDroppedPieces - 1,
                    "no room made for the enemy's piece");
            }),
            ("unequipped slots on others are skipped", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true; f.Drops.Equipped = false;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                Check(f.Drops.Calls == 0 && f.Glamour.Removes == 0, "stripped a slot with no equipment");
            }),
            // "Detach armor": Off / On KO (at the knockout) / On ragdoll activated.
            ("retired switches map to the trigger", () => {
                Check(ArmorDetachmentTriggers.FromRetiredSwitches(false, false) == ArmorDetachmentTrigger.Off, "off");
                Check(ArmorDetachmentTriggers.FromRetiredSwitches(true, false) == ArmorDetachmentTrigger.OnKo, "On KO not kept");
                Check(ArmorDetachmentTriggers.FromRetiredSwitches(false, true) == ArmorDetachmentTrigger.OnRagdollActivated, "on-hit (as saved)");
                Check(ArmorDetachmentTriggers.FromRetiredSwitches(true, true) == ArmorDetachmentTrigger.OnRagdollActivated, "on-hit with KO");
            }),
            ("Off does nothing on a KO", () => {
                using var f = new Fixture(); f.Config.KoStripTrigger = ArmorDetachmentTrigger.Off;
                f.Controller.StripOnKo(f.Address); f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0 && f.Glamour.Removes == 0, "stripped while Off");
            }),
            ("On KO detaches at the knockout without waiting for the ragdoll", () => {
                using var f = new Fixture(); var asked = 0;
                f.Controller.RagdollPhysicsStarted = _ => { asked++; return false; };
                f.Controller.StripOnKo(f.Address);
                Check(f.Drops.Calls == 1 && asked == 0, "On KO waited for or asked the ragdoll");
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1 && f.Drops.Operations[0].IsCommitted, "On KO not committed while the ragdoll has not started");
            }),
            ("On ragdoll activated waits for the ragdoll's physics", () => {
                using var f = new Fixture(); f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                var started = false; f.Controller.RagdollPhysicsStarted = a => a == f.Address && started;
                f.Controller.StripOnKo(f.Address);
                for (var i = 0; i < 30; i++) f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0, "detached before the ragdoll started");
                started = true; f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 1, "not detached when the ragdoll started");
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                Check(f.Glamour.Removes == 1, "not committed");
                f.Controller.StripOnKo(f.Address); f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 1, "second KO stripped again");
            }),
            ("others follow the player's trigger On ragdoll activated", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                var started = false; f.Controller.RagdollPhysicsStarted = a => a == f.OtherAddress && started;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false); f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0, "enemy detached before its ragdoll");
                started = true; f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 1 && f.Glamour.StateCaptures == 0, "enemy not detached by its ragdoll");
            }),
            ("a wait ends when the trigger changes, the actor changes, or no ragdoll comes", () => {
                using var f = new Fixture(); f.Config.KoStripEnemies = true;
                f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                var started = false; f.Controller.RagdollPhysicsStarted = _ => started;
                f.Controller.StripOnKo(f.Address); f.Config.KoStripTrigger = ArmorDetachmentTrigger.Off;
                f.Controller.Tick(.1f); f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                started = true; f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0, "wait survived a trigger change");
                started = false;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: false);
                ((GameObject*)f.Other)->EntityId = 0x40000002; f.Controller.Tick(.1f);
                ((GameObject*)f.Other)->EntityId = 0x40000001; started = true; f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0, "wait survived another actor in the slot");
                using var g = new Fixture(); g.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                var late = false; g.Controller.RagdollPhysicsStarted = _ => late;
                g.Controller.StripOnKo(g.Address); g.Controller.Tick(KoStripController.MaxRagdollWaitSeconds + 1f);
                late = true; g.Controller.Tick(.1f);
                Check(g.Drops.Calls == 0, "wait never expired");
            }),
            ("reset and forget drop a wait", () => {
                using var f = new Fixture(); f.Config.KoStripAllies = true;
                f.Config.KoStripTrigger = ArmorDetachmentTrigger.OnRagdollActivated;
                var started = false; f.Controller.RagdollPhysicsStarted = _ => started;
                f.Controller.StripOnKo(f.Address); f.Controller.StripOtherOnKo(f.OtherAddress, ally: true);
                f.Controller.Forget(f.OtherAddress); f.Controller.Reset(); started = true; f.Controller.Tick(.1f);
                Check(f.Drops.Calls == 0, "a dropped wait still detached");
            }),
            ("a revived ally can lose its armour again", () => {
                using var f = new Fixture(); f.Config.KoStripAllies = true;
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: true);
                f.Drops.Operations[0].Ready(); f.Controller.Tick(.1f);
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: true);
                Check(f.Drops.Calls == 1, "once-per-actor guard missing");
                f.Controller.Forget(f.OtherAddress);
                f.Controller.StripOtherOnKo(f.OtherAddress, ally: true);
                Check(f.Drops.Calls == 2, "revived ally kept its guard");
            }),
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
    }
}
