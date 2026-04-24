namespace SharpClaw.Code.Tools.Abstractions;

/// <summary>
/// Tracks which workspace files the active session has read.
/// Used by edit tools to enforce a Claude-Code-style read-before-edit guard,
/// reducing the chance of blind rewrites against stale model context.
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent use across tool executions.
/// Paths are compared using workspace-relative, forward-slash-normalized form.
/// </remarks>
public interface IToolFileAccessTracker
{
    /// <summary>
    /// Marks the supplied workspace-relative path as observed by the session.
    /// </summary>
    /// <param name="sessionId">The active session identifier.</param>
    /// <param name="relativePath">The workspace-relative, forward-slash path.</param>
    void RecordRead(string sessionId, string relativePath);

    /// <summary>
    /// Returns true when the supplied workspace-relative path has been read during the session.
    /// </summary>
    /// <param name="sessionId">The active session identifier.</param>
    /// <param name="relativePath">The workspace-relative, forward-slash path.</param>
    bool HasRead(string sessionId, string relativePath);
}
