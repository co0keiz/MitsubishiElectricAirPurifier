using AirPurifier.Core;

namespace AirPurifier.Tests;

public sealed class SmartSearchTests
{
    [Theory]
    [InlineData(true, Fixtures.SmartSearchOff, "fc4101351001200031424543003130000000000000fc")]
    [InlineData(false, Fixtures.Normal, "fc4101351001200031424543003030000000000000fd")]
    public void BuildsExactLiveVerifiedCommandsAndPreservesUnrelatedSettings(bool enabled, string status, string expected)
    {
        var source = Convert.FromHexString(status);
        var command = PurifierProtocol.BuildCommand(source, smartSearchEnabled: enabled);
        Assert.Equal(expected, Convert.ToHexStringLower(command));
        foreach (var index in new[] { 0, 2, 3, 4 }.Concat(Enumerable.Range(8, 5)).Concat(Enumerable.Range(14, 7)))
            Assert.Equal(source[index], command[index]);
        Assert.Equal(status, Convert.ToHexStringLower(source));
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, on: false, smartSearchEnabled: false));
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, startAutoCleaning: true, smartSearchEnabled: enabled));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifiesBothStatesThroughSeparateReadbackAndSkipsMatchingState(bool enabled)
    {
        var before = enabled ? Fixtures.SmartSearchOff : Fixtures.Normal;
        var after = enabled ? Fixtures.Normal : Fixtures.SmartSearchOff;
        using var device = new ScriptedDevice(before, after, before, after);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.SetSmartSearchAsync(enabled);
        Assert.Equal(enabled, result.IsSmartSearchEnabled);
        Assert.True(result.IsOn);
        Assert.False(result.IsAutoCleaning);
        Assert.Equal(AutoMode.Normal, result.AutoMode);
        Assert.Equal(4, device.Requests.Count);
        Assert.Equal("<CSV></CSV>", device.Requests.Last());
        await client.SetSmartSearchAsync(enabled);
        Assert.Equal(5, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task UnconfirmedWriteIsBoundedAndNeverResent()
    {
        using var device = new ScriptedDevice(Fixtures.SmartSearchOff);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetSmartSearchAsync(true));
        Assert.Equal(PurifierError.CommandUnconfirmed, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
        Assert.Equal(5, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task RejectsOffUnknownAndUnsupportedStatesWithoutWriting()
    {
        var unknown = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.Normal, 13, 0x32));
        foreach (var (frame, firmware, expectedError) in new[]
                 {
                     (Fixtures.Off, "80.00", PurifierError.DeviceOff),
                     (Fixtures.Normal, "81.00", PurifierError.UnsupportedFirmware),
                     (unknown, "80.00", PurifierError.ProtocolError)
                 })
        {
            using var device = new ScriptedDevice { Respond = (_, _, _) => Task.FromResult(Fixtures.Snapshot(frame, firmware: firmware)) };
            using var http = new HttpClient(device);
            using var client = new PurifierClient(http, Fixtures.Options());
            var state = await client.GetStatusAsync();
            if (frame == unknown || firmware != "80.00") Assert.Null(state.IsSmartSearchEnabled);
            var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetSmartSearchAsync(false));
            Assert.Equal(expectedError, error.Code);
            Assert.False(error.CommandMayHaveSucceeded);
            Assert.All(device.Requests, payload => Assert.Equal("<CSV></CSV>", payload));
        }
    }

    [Theory]
    [InlineData(8, 0x30)]
    [InlineData(9, 0x43)]
    public async Task RejectsPowerOrModeChangeDuringReadback(int index, byte value)
    {
        var changed = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.Normal, index, value));
        using var device = new ScriptedDevice(Fixtures.SmartSearchOff, Fixtures.SmartSearchOff, changed);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetSmartSearchAsync(true));
        Assert.Equal(PurifierError.ProtocolError, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
    }

    [Fact]
    public void OtherCommandsPreserveSmartSearchAndSmartSearchPreservesActiveCleaning()
    {
        var off = Convert.FromHexString(Fixtures.SmartSearchOff);
        Assert.Equal(0x30, PurifierProtocol.BuildCommand(off, on: false)[13]);
        Assert.Equal(0x30, PurifierProtocol.BuildCommand(off, mode: AutoMode.Night)[13]);
        Assert.Equal(0x30, PurifierProtocol.BuildCommand(off, manualSpeed: ManualSpeed.Low)[13]);
        Assert.Equal(0x30, PurifierProtocol.BuildCommand(off, startAutoCleaning: true)[13]);
        var cleaning = Convert.FromHexString(Fixtures.AutoCleaningActive);
        Assert.Equal(0x31, PurifierProtocol.BuildCommand(cleaning, smartSearchEnabled: false)[14]);
    }
}
