using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Core;

/// <summary>
/// Shared view of the client-created character pool. Combat enemies, companions, and optional
/// dev actors must inspect the real manager rather than independent local index sets, otherwise
/// one subsystem can hand CreateBattleCharacter a hint already owned by another.
/// </summary>
internal static unsafe class ClientActorSlotAllocator
{
    // Dismemberment clones intentionally own 200-248.
    public const int SharedSlotLimit = 200;
    public const int TotalSlotCount = 249;

    public static uint FindFreeAscending(ClientObjectManager* manager, int preferredStart = 0)
    {
        if (manager == null)
            return uint.MaxValue;

        preferredStart = System.Math.Clamp(preferredStart, 0, SharedSlotLimit - 1);
        for (var i = preferredStart; i < SharedSlotLimit; i++)
        {
            if (manager->GetObjectByIndex((ushort)i) == null)
                return (uint)i;
        }
        for (var i = 0; i < preferredStart; i++)
        {
            if (manager->GetObjectByIndex((ushort)i) == null)
                return (uint)i;
        }
        return uint.MaxValue;
    }

    public static uint FindFreeDescending(ClientObjectManager* manager)
    {
        if (manager == null)
            return uint.MaxValue;

        for (var i = SharedSlotLimit - 1; i >= 0; i--)
        {
            if (manager->GetObjectByIndex((ushort)i) == null)
                return (uint)i;
        }
        return uint.MaxValue;
    }

    public static int CountFree(ClientObjectManager* manager)
    {
        if (manager == null)
            return 0;

        var count = 0;
        for (var i = 0; i < SharedSlotLimit; i++)
        {
            if (manager->GetObjectByIndex((ushort)i) == null)
                count++;
        }
        return count;
    }
}
