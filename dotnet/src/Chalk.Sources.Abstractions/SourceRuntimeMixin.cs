namespace Chalk.Sources;

public struct SourceRuntimeMixin
{
    private object? _engineIdentity;

    public SourceRuntimeMixin(SourceSharing sharing)
    {
        if (sharing is not (SourceSharing.Shared or SourceSharing.Exclusive))
            throw new ArgumentOutOfRangeException(nameof(sharing));

        Sharing = sharing;
        _engineIdentity = null;
    }
    
    public SourceSharing Sharing { get; }

    public bool TryClaimEngine(object identity, out SourceSharing mode)
    {
        ArgumentNullException.ThrowIfNull(identity);

        mode = Sharing;
        if (Sharing == SourceSharing.Shared) return true;
        
        var existing = Interlocked.CompareExchange(ref _engineIdentity, identity, null); 
        
        return existing is null || ReferenceEquals(existing, identity);
    }

    public void ReleaseEngine(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (Sharing == SourceSharing.Shared) return;

        _ = Interlocked.CompareExchange(ref _engineIdentity, null, identity);
    }
}