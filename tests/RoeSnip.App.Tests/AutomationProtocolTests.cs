using System.Collections.Generic;
using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>The pure parse/dispatch/serialize half of the dev-gated automation channel (see
/// AppShell/AutomationServer.cs's own doc comment) — no live window, no Dispatcher, no pipe. Ported
/// from the WPF app's tests/RoeSnip.Tests/AutomationProtocolTests.cs; the live half (actually
/// driving OverlayController) is verified end-to-end against a resident process, not here — see
/// TESTING.md's "Driving RoeSnip.App programmatically" section. "confirm" accepts copy|save|share,
/// matching the WPF suite exactly (Sharing/* subsystem, item 12).</summary>
public class AutomationProtocolTests
{
    // ---------- TryParseRequest ----------

    [Fact]
    public void TryParseRequest_ValidObjectWithCmd_Succeeds()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"state\"}", out string? error);
        Assert.NotNull(request);
        Assert.Null(error);
        Assert.Equal("state", AutomationProtocol.CommandName(request!));
    }

    [Fact]
    public void TryParseRequest_ValidObjectWithExtraFields_KeepsThemForTheHandler()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"select\",\"x\":1,\"y\":2,\"w\":3,\"h\":4}", out string? error);
        Assert.NotNull(request);
        Assert.Null(error);
        Assert.Equal(1, AutomationProtocol.GetInt(request!, "x"));
        Assert.Equal(4, AutomationProtocol.GetInt(request!, "h"));
    }

    [Fact]
    public void TryParseRequest_MalformedJson_ReturnsNullAndAnError()
    {
        var request = AutomationProtocol.TryParseRequest("{not json", out string? error);
        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseRequest_JsonArrayNotObject_ReturnsNullAndAnError()
    {
        var request = AutomationProtocol.TryParseRequest("[1,2,3]", out string? error);
        Assert.Null(request);
        Assert.Contains("JSON object", error);
    }

    [Fact]
    public void TryParseRequest_MissingCmdField_ReturnsNullAndAnError()
    {
        var request = AutomationProtocol.TryParseRequest("{\"x\":1}", out string? error);
        Assert.Null(request);
        Assert.Contains("cmd", error);
    }

    [Fact]
    public void TryParseRequest_EmptyCmdField_ReturnsNullAndAnError()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"\"}", out string? error);
        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseRequest_CmdIsNotAString_ReturnsNullAndAnError()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":42}", out string? error);
        Assert.Null(request);
        Assert.NotNull(error);
    }

    // ---------- ValidateArgs (pure command JSON dispatch) ----------

    [Theory]
    [InlineData("state")]
    [InlineData("trigger")]
    [InlineData("escape")]
    public void ValidateArgs_ZeroArgCommands_NeverError(string cmd)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"{cmd}\"}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs(cmd, request));
    }

    [Fact]
    public void ValidateArgs_Select_RequiresAllFourNumericFields()
    {
        var complete = AutomationProtocol.TryParseRequest("{\"cmd\":\"select\",\"x\":0,\"y\":0,\"w\":10,\"h\":10}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("select", complete));

        var missingH = AutomationProtocol.TryParseRequest("{\"cmd\":\"select\",\"x\":0,\"y\":0,\"w\":10}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("select", missingH));

        var wrongType = AutomationProtocol.TryParseRequest("{\"cmd\":\"select\",\"x\":\"nope\",\"y\":0,\"w\":10,\"h\":10}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("select", wrongType));
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("mp4")]
    public void ValidateArgs_Record_AcceptsKnownFormats(string format)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"record\",\"format\":\"{format}\"}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("record", request));
    }

    [Fact]
    public void ValidateArgs_Record_RejectsUnknownFormat()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"record\",\"format\":\"avi\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("record", request));
    }

    [Theory]
    [InlineData("max")]
    [InlineData("quality")]
    [InlineData("balanced")]
    [InlineData("compact")]
    [InlineData("minimal")]
    public void ValidateArgs_Preset_AcceptsKnownTiers(string tier)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"preset\",\"tier\":\"{tier}\"}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("preset", request));
    }

    [Fact]
    public void ValidateArgs_Preset_RejectsUnknownTier()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"preset\",\"tier\":\"ultra\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("preset", request));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(37)]
    [InlineData(60)]
    public void ValidateArgs_Fps_AcceptsAnyIntegerInTheUnionRange(int fps)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"fps\",\"value\":{fps}}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("fps", request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(61)]
    [InlineData(999)]
    public void ValidateArgs_Fps_RejectsValueOutsideTheUnionRange(int fps)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"fps\",\"value\":{fps}}}", out _)!;
        string? error = AutomationProtocol.ValidateArgs("fps", request);
        Assert.NotNull(error);
        Assert.Contains(fps.ToString(), error);
    }

    [Fact]
    public void ValidateArgs_Fps_MissingValue_IsRejected()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"fps\"}", out _)!;
        string? error = AutomationProtocol.ValidateArgs("fps", request);
        Assert.NotNull(error);
        Assert.Contains("value", error);
    }

    [Fact]
    public void ValidateArgs_Fps_NonNumericValue_IsRejected()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"fps\",\"value\":\"fast\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("fps", request));
    }

    [Fact]
    public void ValidateArgs_Fps_NonIntegerValue_IsRejected()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"fps\",\"value\":25.5}", out _)!;
        string? error = AutomationProtocol.ValidateArgs("fps", request);
        Assert.NotNull(error);
        Assert.Contains("whole", error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("save")]
    [InlineData("cancel")]
    [InlineData("pause")]
    [InlineData("resume")]
    public void ValidateArgs_Chrome_AcceptsKnownActions(string action)
    {
        var request = AutomationProtocol.TryParseRequest($"{{\"cmd\":\"chrome\",\"action\":\"{action}\"}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("chrome", request));
    }

    [Fact]
    public void ValidateArgs_Chrome_RejectsUnknownAction()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"chrome\",\"action\":\"restart\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("chrome", request));
    }

    [Fact]
    public void ValidateArgs_Screenshot_RequiresNonEmptyPath()
    {
        var withPath = AutomationProtocol.TryParseRequest("{\"cmd\":\"screenshot\",\"path\":\"C:\\\\x.png\"}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("screenshot", withPath));

        var missingPath = AutomationProtocol.TryParseRequest("{\"cmd\":\"screenshot\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("screenshot", missingPath));

        var emptyPath = AutomationProtocol.TryParseRequest("{\"cmd\":\"screenshot\",\"path\":\"\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("screenshot", emptyPath));
    }

    [Fact]
    public void ValidateArgs_Screenshot_OptionalRectMustBeComplete()
    {
        var goodRect = AutomationProtocol.TryParseRequest(
            "{\"cmd\":\"screenshot\",\"path\":\"x.png\",\"rect\":{\"x\":0,\"y\":0,\"w\":100,\"h\":100}}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("screenshot", goodRect));

        var badRect = AutomationProtocol.TryParseRequest(
            "{\"cmd\":\"screenshot\",\"path\":\"x.png\",\"rect\":{\"x\":0,\"y\":0,\"w\":100}}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("screenshot", badRect));
    }

    [Fact]
    public void ValidateArgs_Confirm_CopyNeedsNoPath()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"confirm\",\"action\":\"copy\"}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("confirm", request));
    }

    [Fact]
    public void ValidateArgs_Confirm_SaveRequiresNonEmptyPath()
    {
        var withPath = AutomationProtocol.TryParseRequest(
            "{\"cmd\":\"confirm\",\"action\":\"save\",\"path\":\"C:\\\\x.png\"}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("confirm", withPath));

        var missingPath = AutomationProtocol.TryParseRequest("{\"cmd\":\"confirm\",\"action\":\"save\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("confirm", missingPath));

        var emptyPath = AutomationProtocol.TryParseRequest(
            "{\"cmd\":\"confirm\",\"action\":\"save\",\"path\":\"\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("confirm", emptyPath));
    }

    [Fact]
    public void ValidateArgs_Confirm_RejectsUnknownAction()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"confirm\",\"action\":\"saveHdr\"}", out _)!;
        Assert.NotNull(AutomationProtocol.ValidateArgs("confirm", request));
    }

    [Fact]
    public void ValidateArgs_Confirm_ShareNeedsNoPath()
    {
        // Sharing/* subsystem (item 12): "share" is accepted alongside copy|save, same as "copy" —
        // the upload result is a URL, not a local file, so no "path" is required.
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"confirm\",\"action\":\"share\"}", out _)!;
        Assert.Null(AutomationProtocol.ValidateArgs("confirm", request));
    }

    [Fact]
    public void ValidateArgs_UnknownCommand_IsRejected()
    {
        var request = AutomationProtocol.TryParseRequest("{\"cmd\":\"teleport\"}", out _)!;
        string? error = AutomationProtocol.ValidateArgs("teleport", request);
        Assert.NotNull(error);
        Assert.Contains("teleport", error);
    }

    // ---------- NormalizeClientArgument (CLI --auto shorthand) ----------

    [Fact]
    public void NormalizeClientArgument_BareWord_ExpandsToCmdObject()
    {
        string json = AutomationProtocol.NormalizeClientArgument("state");
        var request = AutomationProtocol.TryParseRequest(json, out string? error);
        Assert.Null(error);
        Assert.Equal("state", AutomationProtocol.CommandName(request!));
    }

    [Fact]
    public void NormalizeClientArgument_AlreadyJsonObject_PassesThroughUntouched()
    {
        string json = AutomationProtocol.NormalizeClientArgument("  {\"cmd\":\"escape\"}  ");
        var request = AutomationProtocol.TryParseRequest(json, out string? error);
        Assert.Null(error);
        Assert.Equal("escape", AutomationProtocol.CommandName(request!));
    }

    // ---------- BuildError ----------

    [Fact]
    public void BuildError_ProducesOkFalseWithTheMessage()
    {
        string json = AutomationProtocol.BuildError("boom");
        Assert.Contains("\"ok\":false", json);
        Assert.Contains("boom", json);
    }

    // ---------- SerializeState (pure DTO -> JSON) ----------

    [Fact]
    public void SerializeState_IdleWithNoSelection_OmitsSelectionAndRecordingFields()
    {
        var monitors = new List<AutomationProtocol.MonitorDto>
        {
            new("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true),
        };
        var state = new AutomationProtocol.StateSnapshot("idle", null, null, null, null, null, null, monitors);
        string json = AutomationProtocol.SerializeState(state);

        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"mode\":\"idle\"", json);
        Assert.Contains("\"selection\":null", json);
        Assert.Contains("\"fps\":null", json);
        Assert.Contains("\"fpsRange\":null", json);
        Assert.Contains("\"deviceName\":\"\\\\\\\\.\\\\DISPLAY1\"", json);
        Assert.Contains("\"isPrimary\":true", json);
    }

    [Fact]
    public void SerializeState_WithSelection_EmitsXYWH()
    {
        var state = new AutomationProtocol.StateSnapshot(
            "overlay", new AutomationProtocol.SelectionDto(10, 20, 300, 400), null, null, null, null, null,
            new List<AutomationProtocol.MonitorDto>());
        string json = AutomationProtocol.SerializeState(state);

        Assert.Contains("\"x\":10", json);
        Assert.Contains("\"y\":20", json);
        Assert.Contains("\"w\":300", json);
        Assert.Contains("\"h\":400", json);
    }

    [Fact]
    public void SerializeState_NeverContainsAnEmDash()
    {
        var state = new AutomationProtocol.StateSnapshot(
            "overlay", null, null, null, null, null, null,
            new List<AutomationProtocol.MonitorDto> { new("\\\\.\\DISPLAY1", 0, 0, 100, 100, true) });
        string json = AutomationProtocol.SerializeState(state);

        Assert.DoesNotContain('—', json); // em dash
        Assert.DoesNotContain('–', json); // en dash
    }

    // ---------- KnownCommands ----------

    [Fact]
    public void KnownCommands_MatchesTheDocumentedTenCommands()
    {
        // Wire-shape parity with the WPF app (see AutomationProtocol's own doc comment):
        // record/preset/fps/chrome remain KNOWN/validated commands here even though this port's
        // live handlers reject them as "unsupported until recording is ported" — never as
        // "unknown command" — so a future recording port only has to swap the live handlers.
        Assert.Equal(
            new[] { "state", "trigger", "select", "record", "preset", "fps", "chrome", "escape", "screenshot", "confirm" },
            AutomationProtocol.KnownCommands);
    }
}
