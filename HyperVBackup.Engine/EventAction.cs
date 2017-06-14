namespace HyperVBackUp.Engine
{
    public enum EventAction
    {
        InitializingVss,
        StartingSnaphotSet,
        SnapshotSetDone,
        StartingArchive,
        StartingEntry,
        SavingEntry,
        ArchiveDone,
        PercentProgress,
        DeletingSnapshotSet
    }
}
