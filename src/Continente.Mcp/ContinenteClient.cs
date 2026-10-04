using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Continente.Mcp;

public sealed class ContinenteClient : IDisposable
{
    private const string LoginBase = "https://login.continente.pt";
    private const string StoreRoot = "https://www.continente.pt";
    private const string StoreBase = StoreRoot + "/on/demandware.store/Sites-continente-Site/default";

    private static readonly TimeSpan LoginRefreshInterval = TimeSpan.FromMinutes(20);

    private readonly ContinenteOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly Dictionary<string, string> _identityState = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _deviceId;

    private DateTimeOffset _authenticatedAt = DateTimeOffset.MinValue;

    public ContinenteClient(IOptions<ContinenteOptions> options)
    {
        _options = options.Value;
        _deviceId = string.IsNullOrWhiteSpace(_options.DeviceId)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            : _options.DeviceId;

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<CartSummary> GetCartAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetCartStateAsync(cancellationToken);
        return state.Summary;
    }

    public async Task<ProductSearchResult> SearchProductsAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A search query is required.", nameof(query));

        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 50.");

        var searchQuery = QueryString(new Dictionary<string, string?>
        {
            ["q"] = query.Trim(),
            ["start"] = "0",
            ["sz"] = limit.ToString(CultureInfo.InvariantCulture)
        });

        using var response = await SendStoreAsync(
            HttpMethod.Get,
            $"Search-ShowAjax?{searchQuery}",
            content: null,
            cancellationToken);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var products = ParseProductSearchHtml(html, limit);

        return new ProductSearchResult(query.Trim(), products.Count, products);
    }

    public async Task<CartMutationResult> SetUnitsAsync(
        string productId,
        int units,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("A product ID is required.", nameof(productId));

        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(units), "Units cannot be negative.");

        if (units == 0)
            return await RemoveProductAsync(productId, cancellationToken);

        var cart = await GetCartStateAsync(cancellationToken);
        var item = cart.Items.FirstOrDefault(x =>
            x.Id == productId &&
            !x.IsDeposit);

        if (item is null)
            throw new InvalidOperationException(
                $"Product {productId} is not currently in the cart. Use add_product first.");

        var quantity = item.HasAlternativeSaleUnit
            ? units * item.StepQuantity
            : units;

        if (quantity < item.MinOrderQuantity)
        {
            var minimumUnits = item.StepQuantity > 0
                ? (int)Math.Ceiling(item.MinOrderQuantity / item.StepQuantity)
                : 1;

            throw new InvalidOperationException(
                $"Product {productId} requires at least {minimumUnits} " +
                $"{item.SecondaryUnit} ({item.MinOrderQuantity.ToString(CultureInfo.InvariantCulture)} {item.PrimaryUnit}).");
        }

        var dimension = item.HasAlternativeSaleUnit ? item.SecondaryUnit : "undefined";

        var query = QueryString(new Dictionary<string, string?>
        {
            ["pid"] = item.Id,
            ["quantity"] = Invariant(quantity),
            ["step"] = Invariant(item.StepQuantity <= 0 ? 1m : item.StepQuantity),
            ["uuid"] = item.Uuid,
            ["dimension"] = dimension,
            ["isCart"] = "false",
            ["gtmList"] = "MCP"
        });

        using var response = await SendStoreAsync(
            HttpMethod.Get,
            $"Cart-UpdateQuantity?{query}",
            content: null,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);

        var finalQuantity = TryGetDecimal(json.RootElement, "finalQuantity") ?? quantity;
        var cartTotal = TryGetString(json.RootElement, "cartTotal")
                        ?? TryGetNestedString(json.RootElement, "totals", "grandTotal");

        return new CartMutationResult(
            productId,
            finalQuantity,
            cartTotal,
            $"Set {productId} to {units} {item.SecondaryUnit}.");
    }

    public async Task<CartMutationResult> AddProductAsync(
        string productId,
        decimal quantity = 1m,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("A product ID is required.", nameof(productId));

        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");

        await EnsureAuthenticatedAsync(cancellationToken);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pid"] = productId,
            ["quantity"] = Invariant(quantity),
            ["isCart"] = "0",
            ["gtmList"] = "MCP",
            ["gtmIndex"] = "1",
            ["breadcrumbs"] = "",
            ["promotionData"] = "",
            ["taggstarPromotionData"] = "",
            ["options"] = "[]"
        });

        using var response = await SendStoreAsync(
            HttpMethod.Post,
            "Cart-AddProduct",
            content,
            cancellationToken);

        // The add response shape varies. Re-read the cart to return a stable result.
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        var cart = await GetCartStateAsync(cancellationToken);
        var item = cart.Items.FirstOrDefault(x => x.Id == productId && !x.IsDeposit);

        if (item is null)
            throw new InvalidOperationException($"Continente did not add product {productId} to the cart.");

        return new CartMutationResult(
            productId,
            item.Quantity,
            cart.Summary.Total,
            $"Added {productId} to the cart.");
    }

    public async Task<CartMutationResult> RemoveProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("A product ID is required.", nameof(productId));

        var cart = await GetCartStateAsync(cancellationToken);
        var item = cart.Items.FirstOrDefault(x => x.Id == productId && !x.IsDeposit);

        if (item is null)
        {
            return new CartMutationResult(
                productId,
                0,
                cart.Summary.Total,
                $"Product {productId} is already absent from the cart.");
        }

        var query = QueryString(new Dictionary<string, string?>
        {
            ["isMiniCart"] = "true",
            ["pid"] = item.Id,
            ["uuid"] = item.Uuid,
            ["gtmList"] = "MCP"
        });

        using var response = await SendStoreAsync(
            HttpMethod.Get,
            $"Cart-RemoveProductLineItem?{query}",
            content: null,
            cancellationToken);

        _ = await response.Content.ReadAsStringAsync(cancellationToken);

        var updated = await GetCartStateAsync(cancellationToken);

        return new CartMutationResult(
            productId,
            0,
            updated.Summary.Total,
            $"Removed {productId} from the cart.");
    }

    private async Task<CartState> GetCartStateAsync(CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        using var response = await SendStoreAsync(
            HttpMethod.Get,
            "Cart-Get",
            content: null,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;

        var plasticBagId = TryGetNestedString(root, "resources", "plasticBagID");
        var items = new List<CartLine>();
        var products = new List<CartProduct>();

        if (root.TryGetProperty("items", out var itemArray) &&
            itemArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemArray.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                var uuid = TryGetString(item, "UUID");

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(uuid))
                    continue;

                var name = TryGetString(item, "productName") ?? id;
                var quantity = TryGetDecimal(item, "quantity") ?? 0m;
                var secondaryQuantity = TryGetDecimal(item, "secondaryQuantity") ?? quantity;

                var rates = GetNestedObject(item, "measurementInfo", "quantityConversionRates");
                var primaryUnit = rates is { } r1 ? TryGetString(r1, "primaryunit") ?? "un" : "un";
                var secondaryUnit = rates is { } r2 ? TryGetString(r2, "secondaryunit") ?? primaryUnit : primaryUnit;
                var stepQuantity = rates is { } r3 ? TryGetDecimal(r3, "stepQuantity") ?? 1m : 1m;
                var minOrderQuantity = rates is { } r4 ? TryGetDecimal(r4, "minOrderQuantity") ?? stepQuantity : stepQuantity;
                var maxUnits = rates is { } r5 ? TryGetDecimal(r5, "maxNumberOfUnitsPerSale") ?? 0m : 0m;
                var hasAlternative = rates is { } r6 && TryGetBoolean(r6, "hasAlternativeSaleUnit") == true;

                var productSdr = item.TryGetProperty("productSDR", out var sdr) &&
                                 sdr.ValueKind == JsonValueKind.Object
                    ? sdr
                    : (JsonElement?)null;

                var isDeposit = productSdr is { } sdrValue &&
                                TryGetBoolean(sdrValue, "isDeposit") == true;
                var isPlasticBag = id == plasticBagId;

                items.Add(new CartLine(
                    id,
                    uuid,
                    quantity,
                    secondaryQuantity,
                    primaryUnit,
                    secondaryUnit,
                    stepQuantity,
                    minOrderQuantity,
                    maxUnits,
                    hasAlternative,
                    isDeposit,
                    isPlasticBag));

                if (!isDeposit)
                {
                    products.Add(new CartProduct(
                        id,
                        name,
                        quantity,
                        secondaryQuantity,
                        primaryUnit,
                        secondaryUnit,
                        stepQuantity,
                        minOrderQuantity,
                        maxUnits,
                        hasAlternative,
                        isPlasticBag));
                }
            }
        }

        var summary = new CartSummary(
            TotalNumberOfProducts: TryGetInt32(root, "totalNumberOfProducts") ?? products.Count,
            NumItems: TryGetDecimal(root, "numItems") ?? products.Sum(x => x.Quantity),
            Total: TryGetNestedString(root, "totals", "grandTotal"),
            DepositTotal: TryGetNestedString(root, "totals", "totalSDRDeposit"),
            Products: products);

        return new CartState(summary, items);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _authenticatedAt < LoginRefreshInterval)
            return;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _authenticatedAt < LoginRefreshInterval)
                return;

            var email = _options.Email;
            var password = _options.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "Continente credentials are not configured. Set Continente__Email and Continente__Password.");
            }

            _identityState.Clear();

            using (var storefront = await _http.GetAsync($"{StoreRoot}/login/", cancellationToken))
            {
                storefront.EnsureSuccessStatusCode();
            }

            var (verifier, challenge) = CreatePkce();

            using (var usernameRequest = new HttpRequestMessage(HttpMethod.Post, $"{LoginBase}/api/username"))
            {
                usernameRequest.Headers.Referrer = new Uri($"{LoginBase}/user-register?clientId={Uri.EscapeDataString(_options.ClientId)}");
                usernameRequest.Headers.TryAddWithoutValidation("Origin", LoginBase);
                usernameRequest.Content = JsonContent.Create(new
                {
                    username = email,
                    clientId = _options.ClientId,
                    returnUrl = (string?)null,
                    deviceId = _deviceId,
                    confidenceScore = _options.ConfidenceScore
                });

                using var usernameResponse = await _http.SendAsync(usernameRequest, cancellationToken);
                usernameResponse.EnsureSuccessStatusCode();
                ApplyIdentityCookies(usernameResponse);

                using var usernameJson = await ReadJsonAsync(usernameResponse, cancellationToken);
                EnsureSupportedAuthStep(usernameJson.RootElement, "/api/email/login/validate-password");
            }

            using (var passwordRequest = new HttpRequestMessage(
                       HttpMethod.Post,
                       $"{LoginBase}/api/email/login/validate-password"))
            {
                passwordRequest.Headers.Referrer = new Uri($"{LoginBase}/user-register?clientId={Uri.EscapeDataString(_options.ClientId)}");
                passwordRequest.Headers.TryAddWithoutValidation("Origin", LoginBase);
                AddIdentityCookieHeader(passwordRequest);
                passwordRequest.Content = JsonContent.Create(new { password });

                using var passwordResponse = await _http.SendAsync(passwordRequest, cancellationToken);
                passwordResponse.EnsureSuccessStatusCode();
                ApplyIdentityCookies(passwordResponse);

                using var passwordJson = await ReadJsonAsync(passwordResponse, cancellationToken);
                EnsureSupportedAuthStep(passwordJson.RootElement, $"/api/auth/success/{_options.ClientId}");
            }

            var authorizeQuery = QueryString(new Dictionary<string, string?>
            {
                ["clientId"] = _options.ClientId,
                ["codeChallenge"] = challenge,
                ["codeChallengeMethod"] = "S256"
            });

            string authorizationCode;
            using (var authorizeRequest = new HttpRequestMessage(
                       HttpMethod.Get,
                       $"{LoginBase}/api/credentials/authorize?{authorizeQuery}"))
            {
                authorizeRequest.Headers.Referrer = new Uri($"{LoginBase}/user-register?clientId={Uri.EscapeDataString(_options.ClientId)}");
                AddIdentityCookieHeader(authorizeRequest);

                using var authorizeResponse = await _http.SendAsync(authorizeRequest, cancellationToken);
                authorizeResponse.EnsureSuccessStatusCode();
                ApplyIdentityCookies(authorizeResponse);

                using var authorizeJson = await ReadJsonAsync(authorizeResponse, cancellationToken);
                authorizationCode = TryGetString(authorizeJson.RootElement, "authorizationCode")
                    ?? throw new InvalidOperationException("Continente did not return an authorization code.");
            }

            using var accountContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["authorizationCode"] = authorizationCode,
                ["codeVerifier"] = verifier,
                ["ssoLogin"] = "false",
                ["rurl"] = $"{StoreRoot}/"
            });

            using (var accountResponse = await SendStoreAsync(
                       HttpMethod.Post,
                       "Account-Login",
                       accountContent,
                       cancellationToken,
                       ensureAuthenticated: false))
            {
                using var accountJson = await ReadJsonAsync(accountResponse, cancellationToken);
                if (TryGetBoolean(accountJson.RootElement, "success") != true)
                    throw new InvalidOperationException("Continente Account-Login did not report success.");
            }

            _authenticatedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private static void EnsureSupportedAuthStep(JsonElement root, string expectedNextStep)
    {
        if (GetNestedObject(root, "properties") is { } properties &&
            TryGetBoolean(properties, "requiredRecaptcha") == true)
        {
            throw new InvalidOperationException(
                "Continente requested CAPTCHA; interactive authentication is required.");
        }

        var nextStep = TryGetString(root, "nextStep");
        if (!string.Equals(nextStep, expectedNextStep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported Continente authentication step: {nextStep ?? "<missing>"}.");
        }
    }

    private void ApplyIdentityCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-set-cookie", out var values))
            return;

        foreach (var header in values)
        {
            var pair = header.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (string.IsNullOrEmpty(value) || value == ".")
                _identityState.Remove(name);
            else
                _identityState[name] = value;
        }
    }

    private void AddIdentityCookieHeader(HttpRequestMessage request)
    {
        if (_identityState.Count == 0)
            return;

        var value = string.Join("; ", _identityState.Select(x => $"{x.Key}={x.Value}"));
        request.Headers.TryAddWithoutValidation("x-cookie", value);
    }

    private async Task<HttpResponseMessage> SendStoreAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool ensureAuthenticated = true)
    {
        if (ensureAuthenticated)
            await EnsureAuthenticatedAsync(cancellationToken);

        var request = new HttpRequestMessage(method, $"{StoreBase}/{relativePath}")
        {
            Content = content
        };

        request.Headers.Referrer = new Uri($"{StoreRoot}/");
        request.Headers.TryAddWithoutValidation("Origin", StoreRoot);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");

        var response = await _http.SendAsync(request, cancellationToken);
        request.Dispose();

        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<ProductSearchItem> ParseProductSearchHtml(string html, int limit)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<ProductSearchItem>();

        // Product pages on Continente consistently end in "-<productId>.html".
        // Matching the URL shape is less brittle than depending on storefront CSS classes.
        var anchorRegex = new Regex(
            @"<a\b(?<attrs>[^>]*?)href\s*=\s*(?:""(?<dq>[^""]+)""|'(?<sq>[^']+)')(?<tail>[^>]*)>(?<body>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        var productUrlRegex = new Regex(
            @"/produto/[^""'?#>]*-(?<id>\d+)\.html(?:[?][^""'#>]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var byId = new Dictionary<string, ProductSearchItem>(StringComparer.Ordinal);

        foreach (Match anchor in anchorRegex.Matches(html))
        {
            var rawHref = anchor.Groups["dq"].Success
                ? anchor.Groups["dq"].Value
                : anchor.Groups["sq"].Value;

            var href = WebUtility.HtmlDecode(rawHref);
            var productMatch = productUrlRegex.Match(href);
            if (!productMatch.Success)
                continue;

            var id = productMatch.Groups["id"].Value;
            var attrs = anchor.Groups["attrs"].Value + " " + anchor.Groups["tail"].Value;
            var body = anchor.Groups["body"].Value;

            var name = ExtractAnchorText(body)
                       ?? ExtractHtmlAttribute(attrs, "title")
                       ?? ExtractHtmlAttribute(body, "alt")
                       ?? ProductNameFromUrl(href);

            var absoluteUrl = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute.ToString()
                : new Uri(new Uri(StoreRoot), href).ToString();

            var candidate = new ProductSearchItem(id, name, absoluteUrl);

            // Product tiles usually contain both an image link and a text link.
            // Prefer the candidate with the richer display name.
            if (!byId.TryGetValue(id, out var existing) ||
                candidate.Name.Length > existing.Name.Length)
            {
                byId[id] = candidate;
            }

            if (byId.Count >= limit &&
                byId.Values.All(x => !string.IsNullOrWhiteSpace(x.Name)))
            {
                // Keep parsing a little longer would only find duplicate links for the same tiles.
                // The result order is preserved by Dictionary insertion order on modern .NET.
                continue;
            }
        }

        return byId.Values.Take(limit).ToArray();
    }

    private static string? ExtractAnchorText(string html)
    {
        var text = Regex.Replace(
            html,
            "<[^>]+>",
            " ",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        text = NormalizeHtmlText(text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ExtractHtmlAttribute(string html, string attribute)
    {
        var match = Regex.Match(
            html,
            $@"\b{Regex.Escape(attribute)}\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        var value = match.Groups["dq"].Success
            ? match.Groups["dq"].Value
            : match.Groups["sq"].Value;

        value = NormalizeHtmlText(value);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeHtmlText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string ProductNameFromUrl(string href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : href.Split('?', '#')[0];

        var fileName = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "product";
        var slug = Regex.Replace(fileName, @"-\d+\.html$", "", RegexOptions.IgnoreCase);
        slug = Uri.UnescapeDataString(slug).Replace('-', ' ');
        return NormalizeHtmlText(slug);
    }

    private static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(42));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string QueryString(IReadOnlyDictionary<string, string?> values) =>
        string.Join("&", values
            .Where(x => x.Value is not null)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));

    private static string Invariant(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static JsonElement? GetNestedObject(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Object ? current : null;
    }

    private static string? TryGetNestedString(JsonElement root, params string[] path)
    {
        if (path.Length == 0)
            return null;

        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(path[i], out current))
                return null;
        }

        return TryGetString(current, path[^1]);
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static decimal? TryGetDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            return number;

        return null;
    }

    private static int? TryGetInt32(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public void Dispose()
    {
        _authLock.Dispose();
        _http.Dispose();
    }

    private sealed record CartState(CartSummary Summary, IReadOnlyList<CartLine> Items);

    private sealed record CartLine(
        string Id,
        string Uuid,
        decimal Quantity,
        decimal SecondaryQuantity,
        string PrimaryUnit,
        string SecondaryUnit,
        decimal StepQuantity,
        decimal MinOrderQuantity,
        decimal MaxNumberOfUnitsPerSale,
        bool HasAlternativeSaleUnit,
        bool IsDeposit,
        bool IsPlasticBag);
}
