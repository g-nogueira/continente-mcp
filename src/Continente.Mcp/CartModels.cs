namespace Continente.Mcp;

public sealed record CartSummary(
    int TotalNumberOfProducts,
    decimal NumItems,
    string? Total,
    string? DepositTotal,
    IReadOnlyList<CartProduct> Products);

public sealed record CartProduct(
    string Id,
    string Name,
    decimal Quantity,
    decimal SecondaryQuantity,
    string PrimaryUnit,
    string SecondaryUnit,
    decimal StepQuantity,
    decimal MinOrderQuantity,
    decimal MaxNumberOfUnitsPerSale,
    bool HasAlternativeSaleUnit,
    bool IsPlasticBag);

public sealed record CartMutationResult(
    string ProductId,
    decimal? FinalQuantity,
    string? CartTotal,
    string Message);

public sealed record ProductSearchResult(
    string Query,
    int Start,
    int Count,
    int? TotalCount,
    bool HasMore,
    int NextStart,
    IReadOnlyList<ProductSearchItem> Products);

public sealed record ProductSearchPage(
    int? TotalCount,
    IReadOnlyList<ProductSearchItem> Products);

public sealed record ProductSearchItem(
    string Id,
    string Name,
    string Url,
    string? Brand,
    decimal? Price,
    string Currency,
    string? PackageDescription,
    decimal? UnitPrice,
    string? UnitPriceBasis,
    string? UnitPriceSource,
    bool? Available,
    bool OnPromotion);
