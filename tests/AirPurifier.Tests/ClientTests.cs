using AirPurifier.Core;

namespace AirPurifier.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task ReadsStatusWithoutChangingAdapterConfiguration()
    {
        using var device = new ScriptedDevice();
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var state = await client.GetStatusAsync();
        Assert.Equal(AutoMode.Turbo, state.AutoMode);
        Assert.True(state.Confirmed);
        Assert.Equal("ERROR", state.AdapterStatusRaw);
        Assert.Equal("<CSV></CSV>", Assert.Single(device.Requests));
    }

    [Theory]
    [InlineData(AutoMode.Normal, Fixtures.Turbo, Fixtures.Normal)]
    [InlineData(AutoMode.Night, Fixtures.Turbo, Fixtures.Night)]
    [InlineData(AutoMode.Turbo, Fixtures.Normal, Fixtures.Turbo)]
    public async Task ConfirmsEachModeWithSeparateReadback(AutoMode mode, string before, string after)
    {
        using var device = new ScriptedDevice(before, before, after);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var result = await client.SetAutoModeAsync(mode);
        Assert.Equal(mode, result.AutoMode);
        Assert.True(result.IsOn);
        Assert.Equal(3, device.Requests.Count);
        Assert.Equal("<CSV></CSV>", device.Requests.Last());
    }

    [Theory]
    [InlineData(true, Fixtures.Off, Fixtures.Normal)]
    [InlineData(false, Fixtures.Normal, Fixtures.Off)]
    public async Task ConfirmsPowerThroughSeparateStatus(bool on, string before, string after)
    {
        using var device = new ScriptedDevice(before, before, after);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        Assert.Equal(on, (await client.SetPowerAsync(on)).IsOn);
        Assert.Equal(3, device.Requests.Count);
    }

    [Fact]
    public async Task WaitsPastStaleAcknowledgementAndFirstReadback()
    {
        using var device = new ScriptedDevice(Fixtures.Normal, Fixtures.Normal, Fixtures.Normal, Fixtures.Night);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        Assert.Equal(AutoMode.Night, (await client.SetAutoModeAsync(AutoMode.Night)).AutoMode);
        Assert.Equal(4, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
    }

    [Fact]
    public async Task DoesNotWriteWhenSettingAlreadyMatches()
    {
        using var device = new ScriptedDevice(Fixtures.Normal);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        await client.SetPowerAsync(true);
        await client.SetAutoModeAsync(AutoMode.Normal);
        Assert.Equal(2, device.Requests.Count);
        Assert.All(device.Requests, payload => Assert.Equal("<CSV></CSV>", payload));
    }

    [Fact]
    public async Task BoundsReadbackAndNeverResendsUnconfirmedWrite()
    {
        using var device = new ScriptedDevice(Fixtures.Normal);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetAutoModeAsync(AutoMode.Night));
        Assert.Equal(PurifierError.CommandUnconfirmed, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
        Assert.Equal(5, device.Requests.Count);
        Assert.Single(device.Requests, payload => payload != "<CSV></CSV>");
        // A later request reads fresh state; an uncertain write does not poison the lock.
        Assert.Equal(AutoMode.Normal, (await client.GetStatusAsync()).AutoMode);
    }

    [Fact]
    public async Task RejectsOffDeviceWithoutTurningItOn()
    {
        using var device = new ScriptedDevice(Fixtures.Off);
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetAutoModeAsync(AutoMode.Night));
        Assert.Equal(PurifierError.DeviceOff, error.Code);
        Assert.False(error.CommandMayHaveSucceeded);
        Assert.Equal("<CSV></CSV>", Assert.Single(device.Requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsIdentityMismatchBeforeOrAfterWrite(bool afterWrite)
    {
        using var device = new ScriptedDevice
        {
            Respond = (_, index, _) => Task.FromResult(Fixtures.Snapshot(Fixtures.Normal,
                mac: afterWrite && index < 2 ? Fixtures.Mac : "00:00:00:00:00:00"))
        };
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetAutoModeAsync(AutoMode.Night));
        Assert.Equal(PurifierError.IdentityMismatch, error.Code);
        Assert.Equal(afterWrite, error.CommandMayHaveSucceeded);
        Assert.Equal(afterWrite ? 3 : 1, device.Requests.Count);
    }

    [Fact]
    public async Task UnsupportedFirmwareCanBeReadButNeverWritten()
    {
        using var device = new ScriptedDevice { Respond = (_, _, _) => Task.FromResult(Fixtures.Snapshot(firmware: "81.00")) };
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        Assert.Null((await client.GetStatusAsync()).AutoMode);
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetPowerAsync(false));
        Assert.Equal(PurifierError.UnsupportedFirmware, error.Code);
        Assert.All(device.Requests, payload => Assert.Equal("<CSV></CSV>", payload));
    }

    [Fact]
    public async Task ReportsLostWriteResponseAsAmbiguousWithoutRetrying()
    {
        using var device = new ScriptedDevice
        {
            Respond = (_, index, _) => index == 0
                ? Task.FromResult(Fixtures.Snapshot(Fixtures.Normal))
                : throw new HttpRequestException("Simulated connection failure after sending command")
        };
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.SetAutoModeAsync(AutoMode.Night));
        Assert.Equal(PurifierError.ConnectionFailed, error.Code);
        Assert.True(error.CommandMayHaveSucceeded);
        Assert.Equal(2, device.Requests.Count);
    }

    [Fact]
    public async Task TimesOutIndividualDeviceRequests()
    {
        using var device = new ScriptedDevice
        {
            Respond = async (_, _, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ""; }
        };
        using var http = new HttpClient(device);
        var options = Fixtures.Options();
        options.RequestTimeout = TimeSpan.FromMilliseconds(30);
        using var client = new PurifierClient(http, options);
        var error = await Assert.ThrowsAsync<PurifierException>(() => client.GetStatusAsync());
        Assert.Equal(PurifierError.RequestTimedOut, error.Code);
        Assert.False(error.CommandMayHaveSucceeded);
    }

    [Fact]
    public async Task SerializesReadAndWriteIncludingReadbackAndAllowsCancellingQueuedRequests()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new ScriptedDevice
        {
            Respond = async (_, index, ct) =>
            {
                if (index == 1)
                {
                    writeStarted.SetResult();
                    await releaseWrite.Task.WaitAsync(ct);
                }
                return Fixtures.Snapshot(index < 2 ? Fixtures.Normal : Fixtures.Night);
            }
        };
        using var http = new HttpClient(device);
        using var client = new PurifierClient(http, Fixtures.Options());
        var write = client.SetAutoModeAsync(AutoMode.Night);
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var cancelledRead = client.GetStatusAsync(cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
            var read = client.GetStatusAsync();
            Assert.False(read.IsCompleted);
            Assert.Equal(2, device.Requests.Count);
            releaseWrite.SetResult();
            Assert.Equal(AutoMode.Night, (await write).AutoMode);
            Assert.Equal(AutoMode.Night, (await read).AutoMode);
            Assert.Equal(4, device.Requests.Count);
        }
        finally { releaseWrite.TrySetResult(); }
    }
}
