using System.Text.Json;
using FluentAssertions;
using SharpClaw.Code.Infrastructure;
using SharpClaw.Code.Infrastructure.Services;
using SharpClaw.Code.Permissions.Models;
using SharpClaw.Code.Protocol.Enums;
using SharpClaw.Code.Protocol.Models;
using SharpClaw.Code.Tools.Abstractions;
using SharpClaw.Code.Tools.BuiltIn;
using SharpClaw.Code.Tools.Models;
using SharpClaw.Code.Tools.Services;

namespace SharpClaw.Code.UnitTests.Tools;

/// <summary>
/// Verifies Claude-Code-style behavior for <see cref="EditFileTool"/> and <see cref="MultiEditFileTool"/>:
/// replace-all, read-before-edit guard, post-edit snippet, and friendlier error messages.
/// </summary>
public sealed class EditFileToolBehaviorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EditFileTool_should_fail_when_match_is_not_unique_and_report_occurrence_count()
    {
        var workspace = CreateWorkspace(out var tool, out _);
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "foo foo bar");

        var result = await ExecuteAsync(tool, workspace, new EditFileToolArguments("file.txt", "foo", "baz"));

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("matched 2 occurrences");
        result.ErrorMessage.Should().Contain("replaceAll");
        (await File.ReadAllTextAsync(path)).Should().Be("foo foo bar");
    }

    [Fact]
    public async Task EditFileTool_should_replace_all_when_flag_is_set()
    {
        var workspace = CreateWorkspace(out var tool, out _);
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "alpha beta alpha gamma alpha");

        var result = await ExecuteAsync(tool, workspace, new EditFileToolArguments("file.txt", "alpha", "omega", ReplaceAll: true));

        result.Succeeded.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("omega beta omega gamma omega");
        var payload = JsonSerializer.Deserialize<EditFileToolResult>(result.StructuredOutputJson!, JsonOptions);
        payload!.ReplacementCount.Should().Be(3);
    }

    [Fact]
    public async Task EditFileTool_should_reject_when_old_equals_new()
    {
        var workspace = CreateWorkspace(out var tool, out _);
        await File.WriteAllTextAsync(Path.Combine(workspace, "file.txt"), "hello");

        var result = await ExecuteAsync(tool, workspace, new EditFileToolArguments("file.txt", "hello", "hello"));

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("must differ");
    }

    [Fact]
    public async Task EditFileTool_should_return_snippet_with_line_numbers_on_success()
    {
        var workspace = CreateWorkspace(out var tool, out _);
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "line 1\nline 2\nline 3\nold value\nline 5\nline 6\nline 7");

        var result = await ExecuteAsync(tool, workspace, new EditFileToolArguments("file.txt", "old value", "new value"));

        result.Succeeded.Should().BeTrue();
        var payload = JsonSerializer.Deserialize<EditFileToolResult>(result.StructuredOutputJson!, JsonOptions);
        payload!.Snippet.Should().Contain("4|new value");
        payload.SnippetStartLine.Should().Be(1);
        payload.SnippetEndLine.Should().Be(7);
    }

    [Fact]
    public async Task EditFileTool_should_enforce_read_before_edit_when_tracker_is_present()
    {
        var workspace = CreateWorkspace(out var tool, out _);
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "hello world");

        var tracker = new InMemoryToolFileAccessTracker();
        var context = CreateContext(workspace, tracker);
        var result = (await tool.ExecuteAsync(
            context,
            CreateRequest(new EditFileToolArguments("file.txt", "world", "there")),
            CancellationToken.None));

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("read_file");
        (await File.ReadAllTextAsync(path)).Should().Be("hello world");

        // After a read is recorded, the edit should succeed.
        tracker.RecordRead(context.SessionId, "file.txt");
        var second = await tool.ExecuteAsync(
            context,
            CreateRequest(new EditFileToolArguments("file.txt", "world", "there")),
            CancellationToken.None);
        second.Succeeded.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("hello there");
    }

    [Fact]
    public async Task ReadFileTool_should_record_access_for_tracker()
    {
        var workspace = CreateWorkspace(out _, out var readTool);
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "content");

        var tracker = new InMemoryToolFileAccessTracker();
        var context = CreateContext(workspace, tracker);
        _ = await readTool.ExecuteAsync(
            context,
            CreateRequest(new ReadFileToolArguments("file.txt", null, null), ReadFileTool.ToolName),
            CancellationToken.None);

        tracker.HasRead(context.SessionId, "file.txt").Should().BeTrue();
    }

    [Fact]
    public async Task MultiEditFileTool_should_apply_edits_atomically_and_report_counts()
    {
        var (workspace, multiEditTool) = CreateMultiEditWorkspace();
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "foo bar baz foo");

        var edits = new List<EditFileOperation>
        {
            new("foo", "FOO", ReplaceAll: true),
            new("bar", "BAR")
        };
        var result = await ExecuteAsync(
            multiEditTool,
            workspace,
            new MultiEditFileToolArguments("file.txt", edits),
            toolName: MultiEditFileTool.ToolName);

        result.Succeeded.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("FOO BAR baz FOO");
        var payload = JsonSerializer.Deserialize<MultiEditFileToolResult>(result.StructuredOutputJson!, JsonOptions);
        payload!.EditCount.Should().Be(2);
        payload.ReplacementCount.Should().Be(3);
    }

    [Fact]
    public async Task MultiEditFileTool_should_roll_back_when_any_edit_fails()
    {
        var (workspace, multiEditTool) = CreateMultiEditWorkspace();
        var path = Path.Combine(workspace, "file.txt");
        const string original = "foo bar";
        await File.WriteAllTextAsync(path, original);

        var edits = new List<EditFileOperation>
        {
            new("foo", "FOO"),
            new("missing", "replaced")
        };
        var result = await ExecuteAsync(
            multiEditTool,
            workspace,
            new MultiEditFileToolArguments("file.txt", edits),
            toolName: MultiEditFileTool.ToolName);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Edit #2");
        (await File.ReadAllTextAsync(path)).Should().Be(original);
    }

    [Fact]
    public async Task MultiEditFileTool_should_validate_later_edits_against_intermediate_state()
    {
        var (workspace, multiEditTool) = CreateMultiEditWorkspace();
        var path = Path.Combine(workspace, "file.txt");
        await File.WriteAllTextAsync(path, "alpha");

        var edits = new List<EditFileOperation>
        {
            new("alpha", "beta"),
            new("beta", "gamma")
        };
        var result = await ExecuteAsync(
            multiEditTool,
            workspace,
            new MultiEditFileToolArguments("file.txt", edits),
            toolName: MultiEditFileTool.ToolName);

        result.Succeeded.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("gamma");
    }

    private static string CreateWorkspace(out EditFileTool editTool, out ReadFileTool readTool)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "sharpclaw-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var fs = new LocalFileSystem();
        var path = new PathService();
        editTool = new EditFileTool(fs, path);
        readTool = new ReadFileTool(fs, path);
        return workspace;
    }

    private static (string Workspace, MultiEditFileTool Tool) CreateMultiEditWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "sharpclaw-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var fs = new LocalFileSystem();
        var path = new PathService();
        return (workspace, new MultiEditFileTool(fs, path));
    }

    private static async Task<ToolResult> ExecuteAsync<TArgs>(
        SharpClawToolBase tool,
        string workspace,
        TArgs args,
        string? toolName = null)
    {
        var context = CreateContext(workspace, fileAccessTracker: null);
        var request = CreateRequest(args, toolName);
        return await tool.ExecuteAsync(context, request, CancellationToken.None);
    }

    private static ToolExecutionRequest CreateRequest<TArgs>(TArgs args, string? toolName = null)
    {
        var name = toolName ?? InferToolName<TArgs>();
        return new ToolExecutionRequest(
            Id: "req-1",
            SessionId: "session-1",
            TurnId: "turn-1",
            ToolName: name,
            ArgumentsJson: JsonSerializer.Serialize(args),
            ApprovalScope: ApprovalScope.FileSystemWrite,
            WorkingDirectory: null,
            RequiresApproval: false,
            IsDestructive: true);
    }

    private static string InferToolName<TArgs>()
        => typeof(TArgs).Name switch
        {
            nameof(EditFileToolArguments) => EditFileTool.ToolName,
            nameof(MultiEditFileToolArguments) => MultiEditFileTool.ToolName,
            nameof(ReadFileToolArguments) => ReadFileTool.ToolName,
            _ => throw new InvalidOperationException($"Unknown arg type {typeof(TArgs).Name}")
        };

    private static ToolExecutionContext CreateContext(string workspace, IToolFileAccessTracker? fileAccessTracker)
        => new(
            SessionId: "session-1",
            TurnId: "turn-1",
            WorkspaceRoot: workspace,
            WorkingDirectory: workspace,
            PermissionMode: PermissionMode.DangerFullAccess,
            OutputFormat: OutputFormat.Text,
            EnvironmentVariables: null,
            AllowedTools: null,
            AllowDangerousBypass: false,
            IsInteractive: false,
            SourceKind: PermissionRequestSourceKind.Runtime,
            FileAccessTracker: fileAccessTracker);
}
