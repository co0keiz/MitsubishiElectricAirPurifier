namespace AirPurifier.Core;

public enum PurifierError
{
    ProtocolError,
    IdentityMismatch,
    UnsupportedFirmware,
    DeviceOff,
    ConnectionFailed,
    RequestTimedOut,
    CommandUnconfirmed
}

public sealed class PurifierException(
    PurifierError code,
    string message,
    Exception? innerException = null,
    bool commandMayHaveSucceeded = false) : Exception(message, innerException)
{
    public PurifierError Code { get; } = code;
    public bool CommandMayHaveSucceeded { get; } = commandMayHaveSucceeded;
}
