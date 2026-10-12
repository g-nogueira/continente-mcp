using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Continente.Mcp;

/// <summary>
/// Extracts catalogue data from Continente's search-result tiles. The markup is
/// server-rendered HTML (Search-ShowAjax / pesquisa), not a public JSON API.
/// </summary>
public static class ProductSearchHtmlParser
{
    private static readonly Regex MoneyRegex = new(
        @"(?<number>\d[\d\s]*[,.]\s*\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnitPriceRegex = new(
        @"(?<price>\d+(?:[,.]\d+)?)\s*€\s*/\s*(?<unit>kg|l|lt|un|folha|rolo)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PackRegex = new(
        @"(?:(?<count>\d+)\s*[x×]\s*)?(?<amount>\d+(?:[,.]\d+)?)\s*(?<unit>kg|gr|g|ml|lt|l|un|unid|folhas?|rolos?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProductIdRegex = new(
        @"(?:pid-|-)(?<id>\d{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProductSearchPage Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new ProductSearchPage(null, []);

        var document = new HtmlParser().ParseDocument(html);
        var products = new Dictionary<string, ProductSearchItem>(StringComparer.Ordinal);

        foreach (var tile in document.QuerySelectorAll(".product-tile"))
        {
            var impression = ReadImpression(tile.GetAttribute("data-product-tile-impression"));
            var anchor = tile.QuerySelector("a.image-link[href*='/produto/']")
                         ?? tile.QuerySelector("a[href*='/produto/']");
            var href = anchor?.GetAttribute("href");
            var id = impression.Id ?? ProductIdRegex.Match(tile.ClassName ?? "").Groups["id"].Value;
            if (string.IsNullOrWhiteSpace(id) && href is not null)
                id = ProductIdRegex.Match(href).Groups["id"].Value;

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = Clean(tile.QuerySelector(".pwc-tile--description")?.TextContent)
                       ?? impression.Name
                       ?? Clean(anchor?.TextContent);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var url = ProductUrl(href, id);
            var package = Clean(tile.QuerySelector(".pwc-tile--quantity")?.TextContent);
            var price = ParseMoney(tile.QuerySelector(".pwc-tile--price-primary")?.TextContent)
                        ?? impression.Price;

            var secondary = Clean(tile.QuerySelector(".pwc-tile--price-secondary")?.TextContent);
            var unitPrice = ParseUnitPrice(secondary);
            var unitPriceSource = unitPrice.Price.HasValue ? "site" : null;

            if (!unitPrice.Price.HasValue && price.HasValue && package is not null)
            {
                var quantity = ParsePackageQuantity(package);
                if (quantity.Quantity is > 0)
                {
                    unitPrice = (decimal.Round(price.Value / quantity.Quantity.Value, 4),
                                 quantity.Basis);
                    unitPriceSource = "calculated_from_package";
                }
            }

            // The HTML contains a hidden "Indisponível" template for *every* tile.
            // Only a visibly displayed unavailable badge is evidence of unavailability.
            var unavailableBadge = tile.QuerySelector(".dual-badge-unavailable-message");
            bool? available = unavailableBadge is not null &&
                              !unavailableBadge.ClassList.Contains("d-none")
                ? false
                : null;

            var result = new ProductSearchItem(
                Id: id,
                Name: name,
                Url: url,
                Brand: impression.Brand,
                Price: price,
                Currency: "EUR",
                PackageDescription: package,
                UnitPrice: unitPrice.Price,
                UnitPriceBasis: unitPrice.Basis,
                UnitPriceSource: unitPriceSource,
                Available: available,
                OnPromotion: tile.QuerySelector(".ct-product-tile-badge--promotional") is not null);

            // Some layouts render separate anchors for the image and title.
            products.TryAdd(id, result);
        }

        var totalValue = document.QuerySelector("[data-total-count]")?.GetAttribute("data-total-count")
                         ?? document.QuerySelector("[data-gtm-results]")?.GetAttribute("data-gtm-results");

        int? totalCount = int.TryParse(totalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            ? total
            : null;

        return new ProductSearchPage(totalCount, products.Values.ToArray());
    }

    private static (string? Id, string? Name, string? Brand, decimal? Price) ReadImpression(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? GetString(string key) =>
                root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            decimal? price = root.TryGetProperty("price", out var amount) &&
                             amount.ValueKind == JsonValueKind.Number &&
                             amount.TryGetDecimal(out var number)
                ? number
                : null;
            return (GetString("id"), GetString("name"), GetString("brand"), price);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    private static (decimal? Price, string? Basis) ParseUnitPrice(string? text)
    {
        if (text is null)
            return (null, null);

        var match = UnitPriceRegex.Match(text);
        if (!match.Success)
            return (null, null);

        var price = ParseDecimal(match.Groups["price"].Value);
        var basis = match.Groups["unit"].Value.ToLowerInvariant();
        return (price, basis == "lt" ? "l" : basis);
    }

    private static (decimal? Quantity, string? Basis) ParsePackageQuantity(string text)
    {
        // "6 rolos x 100 folhas" cannot safely be interpreted as just 100 sheets.
        if (Regex.IsMatch(text, @"\brolos?\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(text, @"\bfolhas?\b", RegexOptions.IgnoreCase))
            return (null, null);

        var match = PackRegex.Match(text);
        if (!match.Success)
            return (null, null);

        var amount = ParseDecimal(match.Groups["amount"].Value);
        var count = match.Groups["count"].Success
            ? ParseDecimal(match.Groups["count"].Value)
            : 1m;
        if (amount is null || count is null)
            return (null, null);

        var quantity = amount.Value * count.Value;
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit switch
        {
            "kg" => (quantity, "kg"),
            "gr" or "g" => (quantity / 1000m, "kg"),
            "l" or "lt" => (quantity, "l"),
            "ml" => (quantity / 1000m, "l"),
            "un" or "unid" => (quantity, "un"),
            "folha" or "folhas" => (quantity, "folha"),
            "rolo" or "rolos" => (quantity, "rolo"),
            _ => (null, null)
        };
    }

    private static decimal? ParseMoney(string? text)
    {
        if (text is null)
            return null;

        var match = MoneyRegex.Match(text);
        return match.Success ? ParseDecimal(match.Groups["number"].Value) : null;
    }

    private static decimal? ParseDecimal(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", "").Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? Clean(string? text)
    {
        var value = text is null ? "" : Regex.Replace(text, @"\s+", " ").Trim();
        return value.Length == 0 ? null : value;
    }

    private static string ProductUrl(string? href, string id)
    {
        if (href is not null &&
            Uri.TryCreate(new Uri("https://www.continente.pt"), href, out var url) &&
            url.Scheme == Uri.UriSchemeHttps &&
            url.Host.Equals("www.continente.pt", StringComparison.OrdinalIgnoreCase) &&
            url.AbsolutePath.Contains("/produto/", StringComparison.OrdinalIgnoreCase))
            return url.GetLeftPart(UriPartial.Path);

        return "https://www.continente.pt/produto/unknown-" + id + ".html";
    }
}
