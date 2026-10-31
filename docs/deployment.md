# Deployment configuration

MarketplaceSync does not store production credentials in the repository. Configure secrets in Render (or the selected hosting platform) as environment variables.

## Database configuration

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | PostgreSQL URL supplied by Render. Alternatively use `ConnectionStrings__DefaultConnection`. |
| `PORT` | Web port supplied by Render. |

## Optional integration variables

| Variable | Purpose |
|---|---|
| `Ebay__ClientId` | eBay application client ID. |
| `Ebay__ClientSecret` | eBay application client secret. |
| `Ebay__MarketplaceDeletionVerificationToken` | Verification token for the eBay account-deletion endpoint; use at least 32 random characters. |
| `Importer__Key` | Shared key sent in the `X-Importer-Key` header; use at least 32 random characters. |
| `Importer__OrganizationId` | Required when more than one active non-legacy organization exists. |
| `MercadoLibre__ClientId` | Mercado Libre application ID. |
| `MercadoLibre__ClientSecret` | Mercado Libre application secret. |
| `MercadoLibre__RedirectUri` | Absolute HTTPS OAuth callback registered with Mercado Libre. |

Integration credentials do not block application startup. Each integration validates its own configuration when used. Keep the non-secret endpoint defaults from `appsettings.json` unless the provider requires different URLs.

## Local development

The project enables .NET User Secrets in `Development`, so local credentials remain outside the repository. Configure only the integrations you are actively using. A typical setup is:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=marketplace_sync;Username=YOUR_USER;Password=YOUR_PASSWORD"
dotnet user-secrets set "Ebay:ClientId" "YOUR_CLIENT_ID"
dotnet user-secrets set "Ebay:ClientSecret" "YOUR_CLIENT_SECRET"
dotnet user-secrets set "Ebay:MarketplaceDeletionVerificationToken" "A_RANDOM_TOKEN_OF_AT_LEAST_32_CHARACTERS"
dotnet user-secrets set "Importer:Key" "A_RANDOM_KEY_OF_AT_LEAST_32_CHARACTERS"
dotnet user-secrets set "MercadoLibre:ClientId" "YOUR_CLIENT_ID"
dotnet user-secrets set "MercadoLibre:ClientSecret" "YOUR_CLIENT_SECRET"
dotnet user-secrets set "MercadoLibre:RedirectUri" "https://YOUR_HOST/MercadoLibre/Callback"
```

Do not copy real values into `appsettings.json`. Use `dotnet user-secrets list` to verify which keys exist, but do not paste that output into issues, logs, or Codex conversations because it can reveal values.

## Deployment sequence

1. Configure the database and the variables for the integrations you enable. Never place their values in Git, logs, screenshots, or documentation.
2. Back up or restore the target database when validating new migrations.
3. Run `dotnet MarketplaceSync.Web.dll --migrate` as a pre-deploy task. The command must finish successfully before starting the new release.
4. Start the web service with `dotnet MarketplaceSync.Web.dll`.
5. Confirm login, organization isolation, OAuth connection, import authentication, and publication using non-production marketplace data first.

The `--migrate` command and a normal web start require only database configuration. Optional integrations return a clear configuration error when their credentials are missing or invalid.

The Docker image starts with `--migrate-and-run`, so free Render web services apply pending migrations automatically before starting the web server. Leave Render's Docker Command empty so the image uses its default entrypoint.

## Credential rotation

If a credential is exposed or committed:

1. Revoke or regenerate it at the provider.
2. Update the hosting platform secret.
3. Redeploy and verify the affected integration.
4. Review logs and provider activity for unexpected use.

Changing a file later does not remove a secret from Git history. Previously committed credentials must be treated as exposed even after the current file is cleaned.
