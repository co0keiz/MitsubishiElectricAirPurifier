using AirPurifier.Core;

namespace AirPurifier.Tests;

public sealed class AutoCleaningTests
{
    [Fact]
    public void BuildsExactLiveVerifiedCleaningCommandAndPreservesOtherFields()
    {
        var source = Convert.FromHexString(Fixtures.Normal);
        var command = PurifierProtocol.BuildCommand(source, startAutoCleaning: true);
        Assert.Equal("fc4101351001400031424543003131000000000000db", Convert.ToHexStringLower(command));
        foreach (var index in new[] { 0, 2, 3, 4 }.Concat(Enumerable.Range(8, 6)).Concat(Enumerable.Range(15, 6)))
            Assert.Equal(source[index], command[index]);
        Assert.Equal(Fixtures.Normal, Convert.ToHexStringLower(source));
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, on: true, startAutoCleaning: true));
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, mode: AutoMode.Normal, startAutoCleaning: true));
    }

    [Fact]
    public async Task ConfirmsStartedThroughSeparateReadbackDespiteAnActiveAcknowledgement()
    {
        using var device = new ScriptedDevice(Fixtures.Normal, Fixtures.AutoCleaningActive, Fixtures.Normal, Fixtures.AutoCleaningActive);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.StartAutoCleaningAsync();
        Assert.True(result.IsAutoCleaning);
        Assert.True(result.IsOn);
        Assert.Equal(AutoMode.Normal, result.AutoMode);
        Assert.Equal(4, device.Requests.Count);
        Assert.Equal("<CSV><CODE><VALUE>fc4101351001400031424543003131000000000000db</VALUE></CODE></CSV>", device.Requests.ElementAt(1));
        Assert.Equal("<CSV></CSV>", device.Requests.Last());
    }

    [Fact]
    public async Task AlreadyActiveIsANoOpAndNaturalCompletionIsReportedAsIdle()
    {
        using var device = new ScriptedDevice(Fixtures.AutoCleaningActive, Fixtures.Normal);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        Assert.True((await client.StartAutoCleaningAsync()).IsAutoCleaning);
        Assert.False((await client.GetStatusAsync()).IsAutoCleaning);
        Assert.All(device.Requests, payload => Assert.Equal("<CSV></CSV>", payload));
    }

    [Fact]
    public async Task UnconfirmedStartIsNotResent()
    {
        using var device = new ScriptedDevice(Fixtures.Normal);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.StartAutoCleaningAsync());
        Assert.Equal(PurifierError.CommandUnconfirmed, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
        Assert.Equal(5, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task RejectsOffUnsupportedAndUnknownCleaningStatesWithoutWriting()
    {
        var unknown = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.Normal, 14, 0x32));
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
            var error = await Assert.ThrowsAsync<PurifierException>(() => client.StartAutoCleaningAsync());
            Assert.Equal(expectedError, error.Code);
            Assert.False(error.CommandMayHaveSucceeded);
            Assert.Equal("<CSV></CSV>", Assert.Single(device.Requests));
        }
    }

    [Theory]
    [InlineData(8, 0x30)]
    [InlineData(9, 0x43)]
    public async Task RejectsPowerOrModeChangeDuringStartReadback(int index, byte value)
    {
        var changed = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.AutoCleaningActive, index, value));
        using var device = new ScriptedDevice(Fixtures.Normal, Fixtures.Normal, changed);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.StartAutoCleaningAsync());
        Assert.Equal(PurifierError.ProtocolError, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
    }
}
