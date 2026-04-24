using SharpClaw.Code.Infrastructure.Abstractions;
using SharpClaw.Code.Protocol.Enums;
using SharpClaw.Code.Protocol.Models;
using SharpClaw.Code.Tools.Models;
using SharpClaw.Code.Tools.Utilities;

namespace SharpClaw.Code.Tools.BuiltIn;

/// <summary>
/// Applies an ordered sequence of string replacements to a single workspace file atomically.
/// </summary>
/// <remarks>
/// Mirrors the Claude Code <c>MultiEdit</c> contract: each edit is validated against the
/// intermediate file state produced by earlier edits in the same batch; if any edit fails the
/// whole batch is rejected and the file is left untouched. A single combined file write, mutation
/// record, and post-edit snippet are produced on success.
/// </remarks>
public sealed class MultiEditFileTool(IFileSystem fileSystem, IPathService pathService) : SharpClawToolBase
{
    /// <summary>
    /// Gets the stable tool name.
    /// </summary>
    public const string ToolName = "multi_edit_file";

    /// <inheritdoc />
    public override ToolDefinition Definition { get; } = new(
        Name: ToolName,
        Description: "Apply an ordered sequence of string replacements to a single workspace file atomically.",
        ApprovalScope: ApprovalScope.FileSystemWrite,
        IsDestructive: true,
        RequiresApproval: true,
        InputTypeName: nameof(MultiEditFileToolArguments),
        InputDescription: "JSON object with path and a non-empty edits array of {oldString, newString, replaceAll?}.",
        Tags: ["file", "edit", "multi-edit", "workspace"]);

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        ToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var arguments = DeserializeArguments<MultiEditFileToolArguments>(request);
        if (arguments.Edits is null || arguments.Edits.Count == 0)
        {
            return CreateFailureResult(context, request, "The 'edits' array must contain at least one edit operation.");
        }

        for (var i = 0; i < arguments.Edits.Count; i++)
        {
            var edit = arguments.Edits[i];
            if (string.IsNullOrEmpty(edit.OldString))
            {
                return CreateFailureResult(context, request, $"Edit #{i + 1}: 'oldString' must not be empty.");
            }

            if (string.Equals(edit.OldString, edit.NewString, StringComparison.Ordinal))
            {
                return CreateFailureResult(context, request, $"Edit #{i + 1}: 'newString' must differ from 'oldString'.");
            }
        }

        var pathResolver = new WorkspacePathResolver(pathService);
        var fullPath = pathResolver.ResolvePath(context, arguments.Path);
        var relativePath = pathResolver.ToRelativePath(context, fullPath);

        if (context.FileAccessTracker is { } tracker
            && !tracker.HasRead(context.SessionId, relativePath))
        {
            return CreateFailureResult(
                context,
                request,
                $"File '{arguments.Path}' must be read with the 'read_file' tool before it can be edited.");
        }

        var originalContent = await fileSystem.ReadAllTextIfExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (originalContent is null)
        {
            return CreateFailureResult(context, request, $"File '{arguments.Path}' was not found.");
        }

        var current = originalContent;
        var totalReplacements = 0;
        for (var i = 0; i < arguments.Edits.Count; i++)
        {
            var edit = arguments.Edits[i];
            var occurrences = StringReplacer.CountOccurrences(current, edit.OldString);
            if (occurrences == 0)
            {
                return CreateFailureResult(
                    context,
                    request,
                    $"Edit #{i + 1}: string was not found in '{arguments.Path}' after prior edits. Ensure the oldString matches byte-for-byte including whitespace and line endings.");
            }

            if (!edit.ReplaceAll && occurrences > 1)
            {
                return CreateFailureResult(
                    context,
                    request,
                    $"Edit #{i + 1}: string matched {occurrences} occurrences in '{arguments.Path}' after prior edits; expand oldString with surrounding context to make it unique or set replaceAll to true.");
            }

            current = edit.ReplaceAll
                ? StringReplacer.ReplaceAll(current, edit.OldString, edit.NewString)
                : StringReplacer.ReplaceFirst(current, edit.OldString, edit.NewString);
            totalReplacements += edit.ReplaceAll ? occurrences : 1;
        }

        if (string.Equals(current, originalContent, StringComparison.Ordinal))
        {
            return CreateFailureResult(context, request, "The edit batch produced no net changes.");
        }

        await fileSystem.WriteAllTextAsync(fullPath, current, cancellationToken).ConfigureAwait(false);

        context.FileAccessTracker?.RecordRead(context.SessionId, relativePath);

        context.MutationRecorder?.Record(
            new FileMutationOperation(
                OperationId: $"op-{Guid.NewGuid():N}",
                Kind: FileMutationKind.Replace,
                ToolName: ToolName,
                RelativePath: relativePath,
                ContentBefore: originalContent,
                ContentAfter: current));

        var (snippet, snippetStart, snippetEnd) = EditSnippetBuilder.Build(originalContent, current);
        var message = $"Applied {arguments.Edits.Count} edit(s) ({totalReplacements} replacement(s)) to '{relativePath}'.";

        var payload = new MultiEditFileToolResult(
            Path: relativePath,
            Message: message,
            EditCount: arguments.Edits.Count,
            ReplacementCount: totalReplacements,
            Snippet: snippet,
            SnippetStartLine: snippetStart,
            SnippetEndLine: snippetEnd);

        var textOutput = string.IsNullOrEmpty(snippet)
            ? message
            : $"{message}{Environment.NewLine}{snippet}";

        return CreateSuccessResult(context, request, textOutput, payload);
    }
}
