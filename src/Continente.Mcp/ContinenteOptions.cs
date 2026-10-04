namespace Continente.Mcp;

public sealed class ContinenteOptions
{
    public const string SectionName = "Continente";

    public string? Email { get; init; }
    public string? Password { get; init; }
    public string ClientId { get; init; } = "NLR6WHyO8Iba4eRS";
    public decimal ConfidenceScore { get; init; } = 0.7m;
    public string? DeviceId { get; init; }
}
