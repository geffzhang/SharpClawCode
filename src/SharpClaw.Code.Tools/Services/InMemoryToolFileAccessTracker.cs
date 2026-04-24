using System.Collections.Concurrent;
using SharpClaw.Code.Tools.Abstractions;

namespace SharpClaw.Code.Tools.Services;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IToolFileAccessTracker"/>.
/// Tracks observed reads keyed by <c>(sessionId, relativePath)</c>, using a case-sensitive
/// comparison on the already-normalized workspace-relative path.
/// </summary>
public sealed class InMemoryToolFileAccessTracker : IToolFileAccessTracker
{
    private readonly ConcurrentDictionary<(string SessionId, string RelativePath), byte> reads = new();

    /// <inheritdoc />
    public void RecordRead(string sessionId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        reads[(sessionId, relativePath)] = 0;
    }

    /// <inheritdoc />
    public bool HasRead(string sessionId, string relativePath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        return reads.ContainsKey((sessionId, relativePath));
    }
}
