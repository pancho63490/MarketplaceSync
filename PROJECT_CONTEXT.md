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
- `ProductVariant` agrega SKU único por organización, atributos flexibles almacenados como JSONB, código de barras, precio opcional, stock y estado. La vista de variantes permite agregar, editar y eliminar; las variantes ya publicadas no se pueden desactivar, borrar ni cambiar de atributos localmente.
- La publicación de Mercado Libre envía variantes con sus combinaciones de atributos, precio, stock y SKU; se conserva el ID externo por publicación/variante para identificar variaciones al actualizarlas.
- La migración `20261004090000_AddProductVariants` crea variantes e identificadores de variación por publicación. Debe aplicarse después de las tres migraciones anteriores y validarse en una base restaurada antes de Render.
- Se eliminó el endpoint HTTP `GET /Admin/RunMigrations`: las migraciones ya no pueden ejecutarse desde una URL ni mediante una llave enviada en el query string.
- Las migraciones se ejecutan ahora como una operación explícita de despliegue con `dotnet MarketplaceSync.Web.dll --migrate`. El proceso aplica `Database.MigrateAsync()` y termina sin iniciar el servidor web ni los trabajos de Hangfire. Debe ejecutarse como paso previo al inicio de la nueva versión y primero contra una base restaurada al validar migraciones nuevas.
- El dashboard de Hangfire en `/hangfire` solo se registra en el entorno `Development`. No existe esa ruta en staging/producción; para observabilidad de producción se deben usar logs y métricas hasta implementar un panel con autorización administrativa dedicada.
- `appsettings.json` ya no contiene contraseñas ni llaves compartidas: la conexión local, la llave del importador y el token de verificación de eBay quedaron como valores vacíos seguros. Los secretos reales deben existir solamente como variables de entorno o secretos administrados por la plataforma.
- El proyecto habilita .NET User Secrets con `UserSecretsId` para configuración local en `Development`; esos valores se guardan fuera del repositorio. Los comandos de configuración están en `docs/deployment.md`.
- Las integraciones son opcionales al arrancar: eBay, el importador y Mercado Libre validan sus credenciales únicamente cuando se usan. La llave del importador y el token de eBay requieren al menos 32 caracteres en sus endpoints, y el callback de Mercado Libre debe ser una URL HTTPS absoluta.
- Tanto el modo `--migrate` como el servidor web requieren solamente la configuración de PostgreSQL.
- El modo `--migrate-and-run` aplica migraciones y continúa con el servidor; se usa como Docker Command en Render Free para evitar encadenar comandos mediante shell.
- `.gitignore` excluye `.DS_Store`, archivos locales de configuración y variantes de `.env` (excepto un posible `.env.example`). Los `.DS_Store` que ya estaban rastreados no se eliminaron porque contienen cambios previos ajenos a esta tarea.
- La lista completa de variables de despliegue y el procedimiento de rotación está en `docs/deployment.md`; nunca se deben copiar valores reales a ese documento ni a `PROJECT_CONTEXT.md`.

## Verificación pendiente

- `dotnet build --no-restore` pasó con 0 errores y sin advertencias.
- Tras el ajuste de inicio de Render y la lectura de `DATABASE_URL`, `dotnet build --no-restore` pasó con 0 errores y sin advertencias. Falta validar el arranque con la URL real de la base PostgreSQL en Render.
- Tras rehacer el panel de estado por cuenta y agregar desconexión individual, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras agregar variantes, administración de SKU e integración de variaciones con Mercado Libre, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras eliminar la migración por HTTP, agregar el modo `--migrate` y limitar el dashboard de Hangfire a `Development`, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras retirar los secretos versionados y habilitar User Secrets para desarrollo, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; el escaneo del árbol de trabajo no encontró los tres valores retirados. Las integraciones incompletas no bloquean el arranque y se validan al utilizar sus endpoints.
- Validar las cuatro migraciones en orden sobre una base PostgreSQL de desarrollo restaurada antes de aplicar cambios a cualquier entorno persistente. `dotnet-ef` no está disponible en este entorno, así que no se pudo ejecutar la verificación automática del snapshot.
- Revisar la migración en un PostgreSQL de desarrollo/restauración antes de aplicarla a producción.
- Instalar o habilitar `dotnet-ef` para generar/verificar el modelo snapshot de EF; no estaba disponible en el entorno al momento de escribir este archivo.
- Completar pruebas manuales de registro, aislamiento entre organizaciones, importación y OAuth.
- Mover secretos de `appsettings.json` a variables de entorno y cifrar tokens en reposo antes de operar con vendedores reales.
- Rotar la contraseña local compartida, `Importer__Key` y `Ebay__MarketplaceDeletionVerificationToken` si los valores que antes estaban versionados llegaron a utilizarse. Configurar en Render solamente las integraciones que se habiliten.
- Configurar el despliegue para ejecutar `dotnet MarketplaceSync.Web.dll --migrate` como tarea previa y abortar la publicación si el comando falla. No volver a crear un endpoint HTTP para migraciones.

## Siguiente módulo

Validar las migraciones en PostgreSQL y probar OAuth, variantes y publicación en Render. Después implementar inventario centralizado y pedidos, con historial de sincronización por publicación.

## Decisiones del modelo

- Los datos operativos pertenecen a `Organization`, no a una persona individual.
- `OrganizationMembership` asocia usuarios con organizaciones y define el rol.
- `Product.CreatedByUserId` y `MercadoLibreToken.ConnectedByUserId` son metadatos opcionales; el aislamiento se basa en `OrganizationId`.
- El importador externo usa `Importer:OrganizationId` cuando existan varias organizaciones.
