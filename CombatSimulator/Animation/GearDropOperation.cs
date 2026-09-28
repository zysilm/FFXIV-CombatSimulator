namespace CombatSimulator.Animation;

/// <summary>A drop is prepared while the original is still visible. The strip controller commits
/// only after every piece of a slot (including both gloves/boots) is ready.</summary>
public sealed class GearDropOperation
{
    public enum Phase { Preparing, Ready, Committed, Cancelled }
    public Phase State { get; private set; }
    public bool IsReady => State == Phase.Ready;
    public bool IsCancelled => State == Phase.Cancelled;
    public bool IsCommitted => State == Phase.Committed;
    public void Ready() { if (State == Phase.Preparing) State = Phase.Ready; }
    public void Commit() { if (State == Phase.Ready) State = Phase.Committed; }
    public void Cancel() { if (State != Phase.Committed) State = Phase.Cancelled; }
}
