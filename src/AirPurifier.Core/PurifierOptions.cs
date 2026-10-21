using System.ComponentModel.DataAnnotations;

namespace AirPurifier.Core;

public sealed class PurifierOptions
{
    [Required]
    public string Host { get; set; } = "192.168.1.53";

    [Required, RegularExpression("^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")]
    public string ExpectedMac { get; set; } = "70:61:be:5b:6d:1e";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadbackDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 10)]
    public int ReadbackAttempts { get; set; } = 3;

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            throw new ValidationException("Purifier:Host must be a host name or IP address, without a URL, path, or port.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(60))
            throw new ValidationException("Purifier:RequestTimeout must be greater than zero and at most 60 seconds.");
        if (ReadbackDelay < TimeSpan.Zero || ReadbackDelay > TimeSpan.FromSeconds(30))
            throw new ValidationException("Purifier:ReadbackDelay must be between zero and 30 seconds.");
    }
}
