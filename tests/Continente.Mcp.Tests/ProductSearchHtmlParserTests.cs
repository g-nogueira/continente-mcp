using Continente.Mcp;
using Xunit;

namespace Continente.Mcp.Tests;

public class ProductSearchHtmlParserTests
{
    // Minimal synthetic markup matching the shape of the supplied browser HAR.
    // The full HAR is intentionally not committed: browser captures may hold secrets.
    private const string SearchHtml = """
        <div class="search-results-wrap" data-gtm-results="20">
          <div data-total-count="20">
            <div class="product-tile pid-4949515"
                 data-product-tile-impression='{"name":"Arroz Basmati Continente","id":"4949515","price":1.89,"brand":"Continente"}'>
              <a class="image-link" href="https://www.continente.pt/produto/arroz-basmati-continente-4949515.html"></a>
              <h2 class="pwc-tile--description">Arroz Basmati Continente</h2>
              <p class="pwc-tile--quantity">emb. 1 kg</p>
              <span class="pwc-tile--price-primary">1<span class="decimalPrice">,89€</span></span>
              <div class="pwc-tile--price-secondary d-none"></div>
              <div class="dual-badge-unavailable-message d-none">Indisponível</div>
            </div>
            <div class="product-tile pid-8488866"
                 data-product-tile-impression='{"name":"Arroz Basmati Pack Poupança Continente","id":"8488866","price":8.69,"brand":"Continente"}'>
              <a class="image-link" href="/produto/arroz-basmati-pack-poupanca-continente-8488866.html"></a>
              <h2 class="pwc-tile--description">Arroz Basmati Pack Poupança Continente</h2>
              <p class="pwc-tile--quantity">emb. 5 kg</p>
              <span class="pwc-tile--price-primary">8<span class="decimalPrice">,69€</span></span>
              <div class="pwc-tile--price-secondary">1,74€/kg</div>
              <div class="dual-badge-unavailable-message d-none">Indisponível</div>
            </div>
          </div>
        </div>
        """;

    [Fact]
    public void Parses_prices_brand_packaging_and_total_count()
    {
        var page = ProductSearchHtmlParser.Parse(SearchHtml);

        Assert.Equal(20, page.TotalCount);
        Assert.Equal(2, page.Products.Count);

        var small = Assert.Single(page.Products.Where(x => x.Id == "4949515"));
        Assert.Equal("Continente", small.Brand);
        Assert.Equal(1.89m, small.Price);
        Assert.Equal("emb. 1 kg", small.PackageDescription);
        Assert.Equal(1.89m, small.UnitPrice);
        Assert.Equal("kg", small.UnitPriceBasis);
        Assert.Equal("calculated_from_package", small.UnitPriceSource);
        Assert.Null(small.Available); // Hidden template text is not stock evidence.

        var large = Assert.Single(page.Products.Where(x => x.Id == "8488866"));
        Assert.Equal(8.69m, large.Price);
        Assert.Equal(1.74m, large.UnitPrice);
        Assert.Equal("kg", large.UnitPriceBasis);
        Assert.Equal("site", large.UnitPriceSource);
        Assert.StartsWith("https://www.continente.pt/produto/", large.Url);

        var cheapest = page.Products.Where(x => x.UnitPriceBasis == "kg")
            .OrderBy(x => x.UnitPrice).First();
        Assert.Equal("8488866", cheapest.Id);
    }

    [Fact]
    public void Converts_multi_pack_gr_and_ml()
    {
        const string html = """
            <div class="product-tile pid-2700058"
                 data-product-tile-impression='{"name":"Arroz pronto a comer","id":"2700058","price":1.55}'>
              <h2 class="pwc-tile--description">Arroz pronto a comer</h2>
              <p class="pwc-tile--quantity">emb. 2 x 125 gr</p>
              <span class="pwc-tile--price-primary">1,55€</span>
            </div>
            <div class="product-tile pid-1234567"
                 data-product-tile-impression='{"name":"Natas","id":"1234567","price":3.00}'>
              <h2 class="pwc-tile--description">Natas</h2>
              <p class="pwc-tile--quantity">emb. 3 x 200 ml</p>
              <span class="pwc-tile--price-primary">3,00€</span>
            </div>
            """;
        var page = ProductSearchHtmlParser.Parse(html);
        Assert.Equal(6.2m, page.Products[0].UnitPrice);
        Assert.Equal("kg", page.Products[0].UnitPriceBasis);
        Assert.Equal(5m, page.Products[1].UnitPrice);
        Assert.Equal("l", page.Products[1].UnitPriceBasis);
    }

    [Fact]
    public void Does_not_assume_roll_count_is_sheet_count()
    {
        const string html = """
            <div class="product-tile pid-1234567"
                 data-product-tile-impression='{"name":"Papel de cozinha","id":"1234567","price":2.99}'>
              <h2 class="pwc-tile--description">Papel de cozinha</h2>
              <p class="pwc-tile--quantity">emb. 2 rolos x 100 folhas</p>
              <span class="pwc-tile--price-primary">2,99€</span>
            </div>
            """;
        var paper = Assert.Single(ProductSearchHtmlParser.Parse(html).Products);
        Assert.Null(paper.UnitPrice);
        Assert.Null(paper.UnitPriceBasis);
    }

    [Fact]
    public void Visible_unavailable_badge_is_evidence_of_unavailability()
    {
        const string html = """
            <div class="product-tile pid-1234567"
                 data-product-tile-impression='{"name":"Natas","id":"1234567","price":0.89}'>
              <h2 class="pwc-tile--description">Natas</h2>
              <div class="dual-badge-unavailable-message">Indisponível</div>
            </div>
            """;
        var item = Assert.Single(ProductSearchHtmlParser.Parse(html).Products);
        Assert.False(item.Available);
    }
}
