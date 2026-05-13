# Application Flows

This document describes the main functional flows of MarketplaceSync.

## 1. Create Product From URL

```mermaid
sequenceDiagram
    actor User as User
    participant View as CreateFromUrl View
    participant PC as ProductsController
    participant EX as ProductExtractorService
    participant DET as MarketplaceDetectorService
    participant EB as EbayApiService
    participant DB as PostgreSQL

    User->>View: Enters product URL
    View->>PC: POST /Products/CreateFromUrl
    PC->>EX: ExtractAsync(sourceUrl)
    EX->>DET: DetectMarketplace(sourceUrl)
    DET-->>EX: AMAZON / EBAY / MERCADOLIBRE / UNKNOWN

    alt eBay URL
        EX->>EB: ExtractFromUrlOrSearchAsync(sourceUrl)
        EB-->>EX: ExtractedProductInfo
    else Other marketplace
        EX-->>PC: Basic detected product data
    end

    PC->>DB: Insert Product
    PC-->>View: Redirect to Products/Index
```

## 2. Marketplace Detection Flow

```mermaid
flowchart TD
    A[Source URL] --> B{Contains amazon.?}
    B -- Yes --> C[AMAZON]
    B -- No --> D{Contains ebay.?}
    D -- Yes --> E[EBAY]
    D -- No --> F{Contains mercadolibre.?}
    F -- Yes --> G[MERCADOLIBRE]
    F -- No --> H[UNKNOWN]
```

## 3. Product Extraction Flow

```mermaid
flowchart TD
    A[ProductExtractorService.ExtractAsync] --> B[Detect marketplace]
    B --> C{Marketplace is eBay?}

    C -- Yes --> D[EbayApiService.ExtractFromUrlOrSearchAsync]
    D --> E{Legacy Item ID found?}
    E -- Yes --> F[GetItemByLegacyIdAsync]
    E -- No --> G[SearchFirstItemAsync]
    F --> H[Map API response]
    G --> H

    C -- No --> I[Return basic detected product]
    I --> J{Marketplace}
    J -- Amazon --> K[Amazon pending official extraction]
    J -- MercadoLibre --> L[Mercado Libre detected]
    J -- Unknown --> M[Generic detected product]

    H --> N[ExtractedProductInfo]
    K --> N
    L --> N
    M --> N
```

## 4. Prepare Product for Mercado Libre

```mermaid
sequenceDiagram
    actor User as User
    participant PC as ProductsController
    participant DB as PostgreSQL

    User->>PC: POST /Products/PrepareForMercadoLibre/{id}
    PC->>DB: Load Product
    PC->>PC: Set default Mercado Libre values
    PC->>PC: Calculate price from source price
    PC->>PC: Copy source stock if available
    PC->>PC: Set Status = NeedsReview
    PC->>DB: Save changes
    PC-->>User: Redirect to Edit page
```

## 5. Mercado Libre OAuth Flow

```mermaid
sequenceDiagram
    actor User as User
    participant MLC as MercadoLibreController
    participant MLA as Mercado Libre Auth
    participant DB as PostgreSQL

    User->>MLC: GET /MercadoLibre/Connect
    MLC->>MLA: Redirect with client_id and redirect_uri
    MLA-->>MLC: Callback with authorization code
    MLC->>MLA: Exchange code for token
    MLA-->>MLC: access_token and refresh_token
    MLC->>DB: Save or update token
    MLC-->>User: Redirect to /MercadoLibre/Status
```

## 6. Publish Product to Mercado Libre

```mermaid
sequenceDiagram
    actor User as User
    participant View as PublishToMercadoLibre View
    participant PC as ProductsController
    participant DB as PostgreSQL
    participant ML as Mercado Libre API

    User->>View: Reviews title, category, price, stock, attributes
    View->>PC: POST /Products/PublishToMercadoLibre
    PC->>DB: Load Product
    PC->>DB: Load latest MercadoLibreToken
    PC->>ML: POST /items
    ML-->>PC: id, permalink, status
    PC->>DB: Update Product with Mercado Libre data
    PC-->>User: Redirect to Products/Index
```

## 7. Product State Flow

```mermaid
stateDiagram-v2
    [*] --> Draft: Product created from source URL
    Draft --> NeedsReview: Prepared for Mercado Libre
    NeedsReview --> Published: Published successfully
    NeedsReview --> OutOfStock: Source stock is zero or below
    Published --> NeedsReview: Source refreshed or product edited
```

## Current Product States

| State | Description |
|---|---|
| `Draft` | Product was created from a source URL but has not been prepared for Mercado Libre. |
| `NeedsReview` | Product has Mercado Libre values and should be reviewed before publication. |
| `Published` | Product was published to Mercado Libre. |
| `OutOfStock` | Product source stock is zero or unavailable. |
