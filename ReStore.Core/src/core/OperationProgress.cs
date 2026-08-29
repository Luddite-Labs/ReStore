namespace ReStore.Core.src.core;

public enum BackupPhase
{
    Enumerating,
    Chunking,
    Uploading,
    Finalising
}

public enum RestorePhase
{
    Previewing,
    Restoring,
    Finalising
}

public sealed record BackupProgress(
    string CurrentFile,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    BackupPhase Phase)
{
    /// <summary>Fraction in 0..1, by bytes when totals are known and by file count otherwise.</summary>
    public double Fraction => ProgressMath.Fraction(FilesDone, FilesTotal, BytesDone, BytesTotal);
}

public sealed record RestoreProgress(
    string CurrentFile,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    RestorePhase Phase)
{
    public double Fraction => ProgressMath.Fraction(FilesDone, FilesTotal, BytesDone, BytesTotal);
}

public sealed record VerificationProgress(
    string CurrentItem,
    int ItemsDone,
    int ItemsTotal,
    string Phase)
{
    public double Fraction => ItemsTotal > 0 ? Math.Clamp((double)ItemsDone / ItemsTotal, 0, 1) : 0;
}

internal static class ProgressMath
{
    public static double Fraction(int itemsDone, int itemsTotal, long bytesDone, long bytesTotal)
    {
        if (bytesTotal > 0)
        {
            return Math.Clamp((double)bytesDone / bytesTotal, 0, 1);
        }

        return itemsTotal > 0 ? Math.Clamp((double)itemsDone / itemsTotal, 0, 1) : 0;
    }
}
