# Continente MCP

An unofficial Model Context Protocol server for managing a shopping cart on [Continente Online](https://www.continente.pt/).

> This project uses private web endpoints used by continente.pt. It is not an official Continente API and may break when Continente changes its storefront.

## Current scope

The server exposes a Streamable HTTP MCP endpoint at:

```
/mcp
```

Current tools:

| Tool | Purpose |
| --- | --- |
| `get_cart` | Read the current cart and totals |
| `search_products` | Search Continente products and return IDs, names, and URLs |
| `set_product_units` | Set the absolute number of units of an existing cart item |
| `add_product` | Add a product by Continente product ID |
| `remove_product` | Remove a product by Continente product ID |

Weighted products are represented using Continente's primary quantity and alternative sale unit. For example, a kiwi may have a primary quantity of `0.48 kg` and a secondary quantity of `4 un`. `set_product_units` performs that conversion automatically.


## Authentication to Continente

The server performs the same login flow observed on continente.pt:

1. Establish a Salesforce Commerce Cloud storefront session.
2. Submit the username to `login.continente.pt/api/username`.
3. Forward the temporary identity state returned in `x-set-cookie` through `x-cookie`.
4. Validate the password.
5. Generate PKCE (`S256`) and obtain an authorization code.
6. Exchange the authorization code + verifier through Continente's `Account-Login` endpoint.
7. Reuse the authenticated storefront cookie session for cart calls.

If Continente requires CAPTCHA or another unsupported interactive authentication step, the login fails instead of trying to bypass it.

## Configuration

Do not commit credentials.

Environment variables:

```bash
Continente__Email=you@example.com
Continente__Password=your-password
```

Optional:

```bash
Continente__DeviceId=0123456789abcdef0123456789abcdef
```

If `Continente__DeviceId` is omitted, a random 32-character hexadecimal device ID is generated for the process.

## Run locally

Requires .NET 10:

```bash
export Continente__Email='you@example.com'
export Continente__Password='...'

dotnet run --project src/Continente.Mcp
```

The ASP.NET server exposes:

```
GET  /health
POST /mcp
```

The C# MCP SDK uses stateless Streamable HTTP transport.

### Docker

```bash
docker build -t continente-mcp .

docker run --rm -p 8080:8080 \
  -e Continente__Email='you@example.com' \
  -e Continente__Password='...' \
  continente-mcp
```

Then the MCP URL is:

```
http://localhost:8080/mcp
```

## Test with MCP Inspector

Start the server, then connect an MCP client/Inspector to the Streamable HTTP endpoint:

```
http://localhost:8080/mcp
```

Start with `get_cart` before testing write tools.

## Connecting to ChatGPT

ChatGPT needs an HTTP-accessible MCP endpoint (or an appropriate secure tunnel). The endpoint should remain private while developing.

This server currently authenticates **to Continente**, but does **not yet authenticate callers of the MCP server itself**.

That distinction matters:

```
ChatGPT/client
      |
      |  MCP authentication      <-- not implemented yet
      v
continente-mcp
      |
      |  Continente credentials  <-- implemented
      v
continente.pt
```

Do **not** expose the current server unauthenticated on the public internet: anyone who can reach `/mcp` could otherwise read or modify the configured Continente cart.

For a direct public ChatGPT integration with private data/write actions, the MCP server should be protected using the MCP OAuth 2.1 authorization flow. For private development, use a private network or supported secure MCP tunneling instead.

## Known API behavior

### Cart

Base:

```
https://www.continente.pt/on/demandware.store/Sites-continente-Site/default/
```

Endpoints currently used:

```
Search-ShowAjax
Cart-Get
Cart-AddProduct
Cart-UpdateQuantity
Cart-RemoveProductLineItem
Account-Login
```

`Cart-Get` returns both `uuid` and `UUID`. Cart mutation endpoints use the uppercase `UUID` value as the product-line-item UUID.

For a normal unit product, Continente's own frontend sends an update similar to:

```
quantity=2
step=1
dimension=undefined
```

For a product whose primary unit is kilograms and whose alternative unit is individual pieces, it sends values such as:

```
quantity=0.48
step=0.12
dimension=un
```

Deposits (SDR) are separate basket line items and are managed automatically by Continente when the related product quantity changes.

## Next work

- Verify `add_product` and `remove_product` end-to-end through the MCP server.
- Add MCP-side OAuth before any public deployment.
- Add integration tests that can run against a dedicated Continente test account/session.
