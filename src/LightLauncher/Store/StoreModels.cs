namespace LightLauncher.Store;

/// <summary>Resultado de búsqueda u oferta de la tienda de Steam.</summary>
public sealed record StoreItem(
    string AppId,
    string Name,
    string? ImageUrl,
    string? PriceText,
    string? OriginalPriceText,
    int DiscountPercent,
    bool IsFree);

/// <summary>Ficha de producto de la tienda de Steam.</summary>
public sealed record StoreAppDetails(
    string AppId,
    string Name,
    string Type,
    string? Description,
    string? HeaderImageUrl,
    string? PriceText,
    string? OriginalPriceText,
    int DiscountPercent,
    bool IsFree,
    bool ComingSoon,
    string? ReleaseDate,
    string? Developers,
    string? Publishers,
    string? Genres,
    int? Metacritic,
    bool SupportsWindows);
