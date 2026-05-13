# Mercado Libre Integration

MarketplaceSync integrates with Mercado Libre for account connection, category support, attribute loading, user validation, and product publication.

## Main Controller

Mercado Libre functionality is currently handled mainly by:

- `MercadoLibreController`
- `ProductsController`

## OAuth Connection Flow

```mermaid
sequenceDiagram
    actor User as User
    participant App as MarketplaceSync
    participant ML as Mercado Libre OAuth
    participant DB as PostgreSQL

    User->>App: GET /MercadoLibre/Connect
    App->>ML: Redirect authorization request
    ML-->>App: Callback with code
    App->>ML: POST token request
    ML-->>App: access_token and refresh_token
    App->>DB: Save MercadoLibreToken
    App-->>User: Redirect to Status page
```

## Required Configuration

```json
{
  "MercadoLibre": {
    "ClientId": "your_client_id",
    "ClientSecret": "your_client_secret",
    "RedirectUri": "https://your-domain.com/MercadoLibre/Callback",
    "AuthUrl": "https://auth.mercadolibre.com.mx/authorization",
    "TokenUrl": "https://api.mercadolibre.com/oauth/token"
  }
}
```

## Important Endpoints

| Endpoint | Purpose |
|---|---|
| `/MercadoLibre/Status` | Shows latest stored Mercado Libre token status. |
| `/MercadoLibre/Connect` | Starts Mercado Libre OAuth authorization flow. |
| `/MercadoLibre/Callback` | Receives authorization code and exchanges it for tokens. |
| `/MercadoLibre/Me` | Calls `/users/me` to validate the current access token. |
| `/MercadoLibre/CategoryPredictor?title=...` | Uses Mercado Libre domain discovery by title. |
| `/MercadoLibre/CategoryAttributes?categoryId=...` | Loads category attributes from Mercado Libre. |
| `/MercadoLibre/Notifications` | Placeholder endpoint for Mercado Libre notifications. |

## Product Publication Flow

```mermaid
sequenceDiagram
    actor User as User
    participant App as ProductsController
    participant DB as PostgreSQL
    participant ML as Mercado Libre API

    User->>App: Open PublishToMercadoLibre page
    App->>DB: Load Product
    App-->>User: Show publication form
    User->>App: Submit publication form
    App->>DB: Load latest MercadoLibreToken
    App->>ML: POST https://api.mercadolibre.com/items
    ML-->>App: id, permalink, status
    App->>DB: Save MercadoLibreItemId, Permalink, Status
    App-->>User: Redirect to Products/Index
```

## Publication Payload

The current publication payload includes:

- `title`
- `category_id`
- `price`
- `currency_id`
- `available_quantity`
- `buying_mode`
- `listing_type_id`
- `condition`
- `pictures`
- `attributes`

## Current Limitations

1. Token refresh is not yet automated.
2. Mercado Libre API logic is partially inside controllers.
3. Attribute validation depends on user input.
4. Notifications endpoint exists but does not process notification payloads yet.
5. Error handling returns raw API content in some cases.

## Recommended Improvements

Create dedicated services:

```text
Services/MercadoLibreAuthService.cs
Services/MercadoLibreCatalogService.cs
Services/MercadoLibrePublishingService.cs
Services/MercadoLibreNotificationService.cs
```

Recommended responsibilities:

- `MercadoLibreAuthService`
  - Get valid token.
  - Refresh token when expired.
  - Store updated tokens.

- `MercadoLibreCatalogService`
  - Category prediction.
  - Attribute loading.
  - Category metadata.

- `MercadoLibrePublishingService`
  - Build publication payload.
  - Publish item.
  - Update product publication data.

- `MercadoLibreNotificationService`
  - Receive notifications.
  - Validate notification source.
  - Update local product state.
