namespace AirPurifier.Core;

public enum AutoMode { Normal, Night, Turbo }
public enum ManualSpeed { Silent, Low, Mid, High, Turbo }
public enum PurifierOperatingMode { Auto, Manual }

public sealed record PurifierState(
    string DeviceHost,
    string Mac,
    string Firmware,
    bool IsOn,
    AutoMode? AutoMode,
    ManualSpeed? ManualSpeed,
    bool? IsAutoCleaning,
    bool? IsSmartSearchEnabled,
    string AutoModeRaw,
    string? RssiReported,
    string? AdapterStatusRaw,
    string? CloudConnectionRaw,
    string? EchonetRaw,
    DateTimeOffset ObservedAtUtc,
    string GeneralFrameHex)
{
    public bool Confirmed => true;
    public PurifierOperatingMode? OperatingMode => AutoMode.HasValue ? PurifierOperatingMode.Auto :
        ManualSpeed.HasValue ? PurifierOperatingMode.Manual : null;
}

public interface IPurifierClient
{
    Task<PurifierState> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<PurifierState> SetPowerAsync(bool on, CancellationToken cancellationToken = default);
    Task<PurifierState> SetAutoModeAsync(AutoMode mode, CancellationToken cancellationToken = default);
    Task<PurifierState> SetManualSpeedAsync(ManualSpeed speed, CancellationToken cancellationToken = default);
    Task<PurifierState> StartAutoCleaningAsync(CancellationToken cancellationToken = default);
    Task<PurifierState> SetSmartSearchAsync(bool enabled, CancellationToken cancellationToken = default);
}
