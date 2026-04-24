using SharpClaw.Code.Infrastructure.Abstractions;
using SharpClaw.Code.Protocol.Enums;
using SharpClaw.Code.Protocol.Models;
using SharpClaw.Code.Tools.Abstractions;
using SharpClaw.Code.Tools.Models;
using SharpClaw.Code.Tools.Utilities;

namespace SharpClaw.Code.Tools.BuiltIn;

/// <summary>
/// Edits a file by replacing either one unique occurrence or every occurrence of a string.
/// </summary>
/// <remarks>
/// Mirrors the Claude Code <c>Edit</c> contract: requires byte-exact matches, supports a
/// <c>replace_all</c> mode, enforces a session-level read-before-edit guard when an
/// <see cref="IToolFileAccessTracker"/> is present, and returns a small post-edit snippet so
/// the caller can validate the change without a follow-up read.
/// </remarks>
public sealed class EditFileTool(IFileSystem fileSystem, IPathService pathService) : SharpClawToolBase
{
    /// <summary>
    /// Gets the stable tool name.
    /// </summary>
    public const string ToolName = "edit_file";

    /// <inheritdoc />
    public override ToolDefinition Definition { get; } = new(
        Name: ToolName,
        Description: "Replace a string in a workspace file. By default the match must be unique; set replace_all to replace every occurrence.",
        ApprovalScope: ApprovalScope.FileSystemWrite,
        IsDestructive: true,
        RequiresApproval: true,
        InputTypeName: nameof(EditFileToolArguments),
        InputDescription: "JSON object with path, oldString, newString, and optional replaceAll flag.",
        Tags: ["file", "edit", "workspace"]);

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        ToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var arguments = DeserializeArguments<EditFileToolArguments>(request);
        if (string.IsNullOrEmpty(arguments.OldString))
        {
            return CreateFailureResult(context, request, "The 'oldString' argument must not be empty.");
        }

        if (string.Equals(arguments.OldString, arguments.NewString, StringComparison.Ordinal))
        {
            return CreateFailureResult(context, request, "The 'newString' argument must differ from 'oldString'.");
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

        var content = await fileSystem.ReadAllTextIfExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return CreateFailureResult(context, request, $"File '{arguments.Path}' was not found.");
        }

        var occurrences = StringReplacer.CountOccurrences(content, arguments.OldString);
        if (occurrences == 0)
        {
            return CreateFailureResult(
                context,
                request,
                $"String was not found in '{arguments.Path}'. Ensure the oldString matches byte-for-byte, including whitespace and line endings.");
        }

        if (!arguments.ReplaceAll && occurrences > 1)
        {
            return CreateFailureResult(
                context,
                request,
                $"String matched {occurrences} occurrences in '{arguments.Path}'; expand oldString with surrounding context to make it unique or set replaceAll to true.");
        }

        var replacementCount = arguments.ReplaceAll ? occurrences : 1;
        var updatedContent = arguments.ReplaceAll
            ? StringReplacer.ReplaceAll(content, arguments.OldString, arguments.NewString)
            : StringReplacer.ReplaceFirst(content, arguments.OldString, arguments.NewString);

        await fileSystem.WriteAllTextAsync(fullPath, updatedContent, cancellationToken).ConfigureAwait(false);

        context.FileAccessTracker?.RecordRead(context.SessionId, relativePath);

        context.MutationRecorder?.Record(
            new FileMutationOperation(
                OperationId: $"op-{Guid.NewGuid():N}",
                Kind: FileMutationKind.Replace,
                ToolName: ToolName,
                RelativePath: relativePath,
                ContentBefore: content,
                ContentAfter: updatedContent));

        var (snippet, snippetStart, snippetEnd) = EditSnippetBuilder.Build(content, updatedContent);
        var message = replacementCount == 1
            ? $"Replaced 1 occurrence in '{relativePath}'."
            : $"Replaced {replacementCount} occurrences in '{relativePath}'.";

        var payload = new EditFileToolResult(
            Path: relativePath,
            Message: message,
            ReplacementCount: replacementCount,
            Snippet: snippet,
            SnippetStartLine: snippetStart,
            SnippetEndLine: snippetEnd);

        var textOutput = string.IsNullOrEmpty(snippet)
            ? message
            : $"{message}{Environment.NewLine}{snippet}";

        return CreateSuccessResult(context, request, textOutput, payload);
    }
}
