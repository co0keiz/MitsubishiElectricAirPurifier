using AirPurifier.Core;

namespace AirPurifier.Tests;

public sealed class ManualSpeedTests
{
    [Theory]
    [InlineData(ManualSpeed.Silent, Fixtures.ManualSilent)]
    [InlineData(ManualSpeed.Low, Fixtures.ManualLow)]
    [InlineData(ManualSpeed.Mid, Fixtures.ManualMid)]
    [InlineData(ManualSpeed.High, Fixtures.ManualHigh)]
    [InlineData(ManualSpeed.Turbo, Fixtures.ManualTurbo)]
    public async Task SwitchesFromAutoToEachManualSpeedWithSeparateReadback(ManualSpeed speed, string target)
    {
        using var device = new ScriptedDevice(Fixtures.Turbo, Fixtures.Turbo, target);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.SetManualSpeedAsync(speed);
        Assert.Equal(PurifierOperatingMode.Manual, result.OperatingMode);
        Assert.Equal(speed, result.ManualSpeed);
        Assert.Null(result.AutoMode);
        Assert.True(result.IsOn);
        Assert.Equal(3, device.Requests.Count);
        Assert.Equal("<CSV></CSV>", device.Requests.Last());
    }

    [Fact]
    public async Task SwitchesBackToAutoWithoutReportingTheRememberedManualSpeed()
    {
        using var device = new ScriptedDevice(Fixtures.ManualTurbo, Fixtures.ManualTurbo, Fixtures.Normal);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.SetAutoModeAsync(AutoMode.Normal);
        Assert.Equal(PurifierOperatingMode.Auto, result.OperatingMode);
        Assert.Equal(AutoMode.Normal, result.AutoMode);
        Assert.Null(result.ManualSpeed);
    }

    [Fact]
    public async Task SkipsManualNoOpButDoesNotConfuseAutoTurboWithManualTurbo()
    {
        using var device = new ScriptedDevice(Fixtures.Turbo, Fixtures.Turbo, Fixtures.ManualTurbo);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        await client.SetManualSpeedAsync(ManualSpeed.Turbo);
        Assert.Equal(3, device.Requests.Count);
        await client.SetManualSpeedAsync(ManualSpeed.Turbo);
        Assert.Equal(4, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task DoesNotAcceptAnotherManualSpeedOrAutoModeAsConfirmation()
    {
        using var device = new ScriptedDevice(Fixtures.ManualSilent, Fixtures.ManualSilent,
            Fixtures.ManualLow, Fixtures.Turbo, Fixtures.ManualHigh);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetManualSpeedAsync(ManualSpeed.Turbo));
        Assert.Equal(PurifierError.CommandUnconfirmed, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
        Assert.Equal(5, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task ManualSpeedRequiresPowerOnAndRejectsInvalidEnumBeforeNetworkAccess()
    {
        using var device = new ScriptedDevice(Fixtures.Off);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetManualSpeedAsync((ManualSpeed)999));
        Assert.Empty(device.Requests);
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetManualSpeedAsync(ManualSpeed.Low));
        Assert.Equal(PurifierError.DeviceOff, error.Code);
        Assert.False(error.CommandMayHaveSucceeded);
        Assert.Single(device.Requests);
    }

    [Fact]
    public async Task PowerWritesPreserveManualSpeed()
    {
        var off = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.ManualHigh, 8, 0x30));
        using var device = new ScriptedDevice(Fixtures.ManualHigh, Fixtures.ManualHigh, off);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.SetPowerAsync(false);
        Assert.False(result.IsOn);
        Assert.Equal(ManualSpeed.High, result.ManualSpeed);
        Assert.Equal(PurifierOperatingMode.Manual, result.OperatingMode);
        var command = PurifierProtocol.ParseXml(device.Requests.ElementAt(1)).Root!.Element("CODE")!.Element("VALUE")!.Value;
        Assert.Equal(0x33, Convert.FromHexString(command)[9]);
    }
}
