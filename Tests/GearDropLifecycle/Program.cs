using System.Runtime.InteropServices;
using CombatSimulator;
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
        public Fixture()
        {
            ((GameObject*)Actor)->DrawObject = (DrawObject*)draw;
            draw->SlotCount = 10;
            draw->Models = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model**)NativeMemory.AllocZeroed(80);
            // Slot presence only; production controller never dereferences these model pointers.
            draw->Models[1] = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model*)1;
            Services.ObjectTable.LocalPlayer = new Player { Address = Address };
            Controller = new(Config, Glamour, Drops, new Dalamud.Plugin.Services.Log());
        }
        public void Dispose()
        {
            Controller.Dispose(); Services.ObjectTable.LocalPlayer = null;
            NativeMemory.Free(draw->Models); NativeMemory.Free(draw); NativeMemory.Free(Actor);
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
                using var f = new Fixture(); f.Config.KoStripEnabled = false;
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
            ("on-hit failure can retry the same slot", () => {
                using var f = new Fixture(); f.Config.KoStripOnHitEnabled = true; f.Controller.AllowOnHitDetach = () => true;
                f.Drops.FailCall = 1; Check(!f.Controller.TryStripNextOnHit(f.Address), "failed hit reported success");
                Check(f.Controller.TryStripNextOnHit(f.Address) && f.Drops.Calls == 2, "failed slot stuck in done set");
            }),
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
    }
}
