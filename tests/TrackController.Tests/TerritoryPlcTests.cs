using TrackController.Contracts.Messages;
using TrackController.Runtime;

namespace TrackController.Tests;

/// <summary>Checks independent PLC routing, persistence, and PC/Pi placement.</summary>
[TestClass]
public sealed class TerritoryPlcTests
{
    [TestMethod]
    public async Task Assignment_SaveReloadAndPiPlacement_PreserveIndependentPrograms()
    {
        using var files = new DeploymentFiles();
        var original = files.Read();
        string source = original.ProgramSource.Replace("allow_route = movement_enabled", "allow_route = false");
        Assert.AreNotEqual(original.ProgramSource, source);
        var plan = original.WithTerritoryProgram("wayside-b", "blocked.plc", source);
        plan.SaveExecution();
        var saved = files.Read();
        Assert.IsNull(saved.Territories[0].ProgramSource);
        Assert.AreEqual(source, saved.Territories[1].ProgramSource);
        Assert.AreEqual(original.ProgramSource, File.ReadAllText(files.PathFor("wayside.plc")));
        await using var server = new PiRuntimeServer(0);
        server.Start();
        await using var runtime = saved.WithExecution(TerritoryDeploymentTests.Choices(saved, "Pi", server.Port)).CreateRuntime();
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Clear(saved.Territories[0].Configuration))).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, new WaysideSimulationMessage { IsPaused = false })).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Movement())).IsAccepted);
        Assert.AreEqual(0d, (await runtime.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, new WaysidePlcProgramMessage { TerritoryId = "wayside-b", Name = "restored.plc", Source = original.ProgramSource })).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Clear(saved.Territories[0].Configuration))).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, new WaysideSimulationMessage { IsPaused = false })).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Movement())).IsAccepted);
        var restored = await runtime.PollAsync();
        Assert.IsGreaterThan(0d, restored.Authorizations.Single().AuthorizedAuthorityMeters);
        StringAssert.Contains(restored.PlcName, "restored.plc");
        StringAssert.Contains(restored.PlcName, original.ProgramName);
    }

    [TestMethod]
    public async Task TargetedUpload_ChangesOnlySelectedTerritory_AndRejectsUnknownTarget()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        await using var runtime = plan.CreateRuntime();
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, new WaysidePlcProgramMessage { TerritoryId = "wayside-b", Name = "junction.plc", Source = plan.ProgramSource })).IsAccepted);
        string names = (await runtime.PollAsync()).PlcName;
        StringAssert.Contains(names, "junction.plc");
        StringAssert.Contains(names, plan.ProgramName);
        Assert.IsFalse((await PiRuntimeTests.Send(runtime, new WaysidePlcProgramMessage { TerritoryId = "missing", Name = "wrong.plc", Source = plan.ProgramSource })).IsAccepted);
        Assert.AreEqual(names, (await runtime.PollAsync()).PlcName);
        Assert.IsFalse((await PiRuntimeTests.Send(runtime, new WaysidePlcProgramMessage { TerritoryId = "wayside-b", Name = "invalid.plc", Source = "invalid" })).IsAccepted);
        StringAssert.Contains((await runtime.PollAsync()).PlcName, plan.ProgramName);
        Assert.ThrowsExactly<ArgumentException>(() => plan.WithTerritoryProgram("wayside-b", "invalid.plc", "invalid"));
    }

    [TestMethod]
    public void DeploymentOverride_RejectsMissingInvalidAndEscapingFiles()
    {
        foreach (string path in new[] { "missing.plc", "invalid.plc", "../outside.plc" })
        {
            using var files = new DeploymentFiles();
            File.WriteAllText(files.PathFor("invalid.plc"), "invalid");
            files.Edit("all-pc.deployment.json", json => json["territories"]![0]!["plcFile"] = path);
            Assert.Throws<Exception>(() => files.Read());
        }
    }
}
