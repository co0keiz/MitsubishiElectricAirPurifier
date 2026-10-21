using System.ComponentModel.DataAnnotations;
using AirPurifier.Core;
using Microsoft.Extensions.Options;

namespace AirPurifier.Api;

internal sealed class PurifierOptionsValidator : IValidateOptions<PurifierOptions>
{
    public ValidateOptionsResult Validate(string? name, PurifierOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ValidationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
