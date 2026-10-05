# MarketplaceSync — Contexto del proyecto

## Objetivo

Convertir MarketplaceSync en un ERP omnicanal para vendedores, tomando UpSeller como referencia funcional. El desarrollo avanza por módulos, primero con Mercado Libre México y una base segura para organizaciones, catálogo, publicaciones, inventario y pedidos.

## Estado actual

- Aplicación ASP.NET Core MVC sobre .NET 8, PostgreSQL y Entity Framework Core.
- Autenticación con ASP.NET Core Identity.
- Se agregó el modelo `Organization` y la membresía `OrganizationMembership` con roles `Owner`, `Admin` y `Operator`.
- El registro pide nombre del negocio y crea usuario, organización y membresía Owner en una transacción.
- Productos y conexiones de Mercado Libre pasan a ser propiedad de una organización.
- Las pantallas/acciones de productos y el dashboard filtran por organización.
- El OAuth valida el parámetro `state`; las renovaciones procesan cada token que está por expirar.
- La API privada de importación asigna los productos a la única organización no heredada o requiere `Importer:OrganizationId` si hay varias.
- Se agregó la migración `20260925120000_AddOrganizationsAndTenantOwnership`, que crea organizaciones para usuarios anteriores y asigna filas sin propietario al espacio heredado.
- Se agregó `MarketplaceAccount` como identidad del canal separada de las credenciales OAuth; `MercadoLibreToken` conserva credenciales y puede asociarse a la cuenta. La conexión OAuth crea/actualiza la cuenta y desconectarla actualiza su estado.
- Se agregó la migración `20260929120000_AddMarketplaceAccounts`, que crea cuentas para conexiones Mercado Libre existentes y vincula sus tokens cuando hay ID externo.
- `Product` conserva el catálogo y los datos de origen; las publicaciones se gestionan en `MarketplacePublication`, asociadas opcionalmente a una cuenta. La pantalla de publicación selecciona la cuenta, reutiliza sus datos por cuenta y actualiza el artículo existente al volver a publicar.
- Se agregó la migración `20260929130000_SplitMarketplacePublicationFromProduct`, que copia los campos heredados de Mercado Libre a publicaciones y luego retira esos campos del catálogo. Si hay varias cuentas y no se puede inferir la cuenta correcta de una publicación heredada, conserva la publicación sin atribución para revisión.
- En `Development`, Hangfire usa almacenamiento en memoria; en otros entornos conserva PostgreSQL. Identity y los datos de negocio siguen requiriendo PostgreSQL.
- La configuración de PostgreSQL acepta `ConnectionStrings__DefaultConnection` o la URL `DATABASE_URL` de Render, además del valor de `appsettings.json`. La URL de Render se convierte a formato Npgsql.
- El servicio web escucha en `0.0.0.0:$PORT` cuando Render define `PORT`.
- El registro del trabajo recurrente de Hangfire ya no se ejecuta durante la construcción de la aplicación; se reintenta en segundo plano cada 30 segundos si PostgreSQL no está disponible, para que el sitio pueda iniciar.
- La pantalla de estado de Mercado Libre consulta `MarketplaceAccount` por organización, muestra estado y vencimiento de credenciales sin revelar tokens, y permite desconectar una cuenta específica. La desconexión desactiva todos sus tokens y actualiza el estado de la cuenta.

## Verificación pendiente

- `dotnet build --no-restore` pasó con 0 errores y sin advertencias.
- Tras el ajuste de inicio de Render y la lectura de `DATABASE_URL`, `dotnet build --no-restore` pasó con 0 errores y sin advertencias. Falta validar el arranque con la URL real de la base PostgreSQL en Render.
- Tras rehacer el panel de estado por cuenta y agregar desconexión individual, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Validar las tres migraciones en orden sobre una base PostgreSQL de desarrollo restaurada antes de aplicar cambios a cualquier entorno persistente.
- Revisar la migración en un PostgreSQL de desarrollo/restauración antes de aplicarla a producción.
- Instalar o habilitar `dotnet-ef` para generar/verificar el modelo snapshot de EF; no estaba disponible en el entorno al momento de escribir este archivo.
- Completar pruebas manuales de registro, aislamiento entre organizaciones, importación y OAuth.
- Mover secretos de `appsettings.json` a variables de entorno y cifrar tokens en reposo antes de operar con vendedores reales.

## Siguiente módulo

Validar las migraciones en PostgreSQL y probar OAuth/desconexión de cuentas en Render. Después implementar variantes/SKU, inventario centralizado y pedidos, con historial de sincronización por publicación.

## Decisiones del modelo

- Los datos operativos pertenecen a `Organization`, no a una persona individual.
- `OrganizationMembership` asocia usuarios con organizaciones y define el rol.
- `Product.CreatedByUserId` y `MercadoLibreToken.ConnectedByUserId` son metadatos opcionales; el aislamiento se basa en `OrganizationId`.
- El importador externo usa `Importer:OrganizationId` cuando existan varias organizaciones.
