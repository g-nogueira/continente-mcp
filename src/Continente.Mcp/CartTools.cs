using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Continente.Mcp;

[McpServerToolType]
public sealed class CartTools(ContinenteClient continente)
{
    [McpServerTool(
        Name = "get_cart",
        Title = "Get Continente cart",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets the authenticated user's current Continente shopping cart, including quantities, units, and totals.")]
    public Task<CartSummary> GetCart(CancellationToken cancellationToken) =>
        continente.GetCartAsync(cancellationToken);

    [McpServerTool(
        Name = "set_product_units",
        Title = "Set product units",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Sets the absolute number of units of a product already present in the Continente cart. For weighted products with an alternative unit, converts units to the primary quantity automatically.")]
    public Task<CartMutationResult> SetProductUnits(
        [Description("Continente product ID, for example 7174691.")] string productId,
        [Description("Desired absolute number of units. Use 0 to remove the product.")] int units,
        CancellationToken cancellationToken) =>
        continente.SetUnitsAsync(productId, units, cancellationToken);

    [McpServerTool(
        Name = "add_product",
        Title = "Add product to cart",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Adds a Continente product to the cart by product ID. Quantity is expressed in the product's primary sale unit. Product search is not yet implemented.")]
    public Task<CartMutationResult> AddProduct(
        [Description("Continente product ID.")] string productId,
        [Description("Quantity in the product's primary sale unit. Defaults to 1.")] decimal quantity = 1m,
        CancellationToken cancellationToken = default) =>
        continente.AddProductAsync(productId, quantity, cancellationToken);

    [McpServerTool(
        Name = "remove_product",
        Title = "Remove product from cart",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Removes a product from the Continente cart by product ID. If it is already absent, the operation succeeds without changing the cart.")]
    public Task<CartMutationResult> RemoveProduct(
        [Description("Continente product ID to remove.")] string productId,
        CancellationToken cancellationToken) =>
        continente.RemoveProductAsync(productId, cancellationToken);
}
