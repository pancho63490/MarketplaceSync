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
- El modo `--migrate-and-run` aplica migraciones y continúa con el servidor. Es el `ENTRYPOINT` predeterminado de la imagen para que Render Free no dependa de comandos personalizados ni encadenamiento mediante shell.
- El centro de pedidos incluye indicadores de ventas, búsqueda por pedido/comprador/producto/SKU, filtros por estado, cuenta y fechas, paginación y detalle protegido por organización. Esta capa reutiliza los pedidos sincronizados y no requiere una migración adicional.
- El inventario centralizado reutiliza `ProductVariant.Stock`, agrega un umbral de stock bajo y registra cada venta, reversión o ajuste manual en `InventoryMovement`. La conciliación usa una referencia única por estado de pedido/variante para no descontar dos veces al resincronizar.
- Los pedidos pagados descuentan automáticamente las variantes que coinciden por `seller_sku`; una cancelación o cambio de cantidad genera el movimiento compensatorio. Los SKU sin variante se registran en logs y no modifican otro producto por aproximación.
- La pantalla de inventario está aislada por organización e incluye existencias, ventas netas, alertas, sobreventas, ajustes manuales e historial enlazado al pedido.
- La migración `20261031030150_AddInventoryMovements` agrega el umbral con valor inicial 5, la tabla de movimientos y el índice único de idempotencia.
- La interfaz global usa una navegación lateral clara y compacta, topbar con acción rápida y estilos consistentes para tarjetas, formularios y tablas. El dashboard prioriza pedidos, ventas, catálogo, alertas, tareas operativas y una guía de configuración de cuatro pasos.
- La publicación en Mercado Libre permite sugerir categorías por título, seleccionar una sugerencia, validar IDs mexicanos `MLM`, cargar atributos obligatorios sin requerir token para el recurso público y enviar al API los valores elegidos. Los atributos de lista usan `value_id`, los libres usan `value_name` y marca/modelo se precargan cuando existen.
- Los botones parciales de sugerir categoría y cargar atributos omiten la validación completa del formulario (`formnovalidate` y clase `cancel`); de lo contrario, la validación cliente impedía solicitar una sugerencia precisamente porque `CategoryId` todavía estaba vacío.
- El módulo `Más vendidos MX` consulta el ranking oficial `BEST_SELLER` de Mercado Libre México por categoría, muestra las primeras 10 posiciones y completa nombre, imagen, precio y enlace cuando esos datos están disponibles.
- La categoría se selecciona automáticamente usando la más frecuente entre las publicaciones de la organización; también puede elegirse otra categoría `MLM` y una cuenta conectada. Las respuestas se guardan 15 minutos en memoria para reducir llamadas al API.
- Mercado Libre no publica un ranking universal de todo México ni la cantidad exacta de unidades vendidas: el ranking disponible se limita a una categoría. La interfaz lo comunica explícitamente para no presentar posiciones como volúmenes de venta.
- El detector de oportunidades ordena el Top 10 con una puntuación explicable de 0 a 100: demanda por posición (50%), precio frente a la mediana del ranking (25%) y competencia estimada por publicaciones activas de la categoría y saturación de marca (25%). Muestra cada componente, recomendación, precio mediano, nivel de competencia y si un ítem ya existe en el catálogo.
- La competencia es una señal aproximada, no un dato comercial exacto: Mercado Libre expone `total_items_in_this_category`, pero no ventas unitarias ni competencia individual por producto. La pantalla conserva esa advertencia.
- Cada oportunidad incluye `Preparar para publicar`. La acción vuelve a validar cuenta, categoría y pertenencia al Top 10 en el servidor, crea un producto `NeedsReview` y una publicación `Draft`, y abre el formulario de Mercado Libre con cuenta, categoría, precio, imagen y marca precargados. Nunca publica automáticamente: stock, proveedor, propiedad intelectual, atributos y margen deben confirmarse antes del envío.
- Los borradores de oportunidades se identifican por organización y `SourceProductId` para evitar duplicarlos. El ID del producto analizado no se guarda como `ExternalItemId` de la publicación propia, porque la publicación nueva debe recibir su propio ID de Mercado Libre.
- Al abrir un borrador con categoría guardada, el formulario consulta automáticamente los atributos obligatorios, precarga `BRAND` y `MODEL` (incluido `value_id` cuando coincide con una opción oficial) y muestra el avance real del checklist. El estado distingue atributos no cargados, incompletos y completos; el botón manual queda como reintento.
- El formulario incluye una calculadora de rentabilidad que consulta `sites/MLM/listing_prices` con el token de la cuenta, categoría, precio, moneda y tipo de publicación. Usa `sale_fee_amount` como comisión total sin volver a sumar `fixed_fee`, y combina costo unitario, envío, otros costos e impuestos configurables para mostrar utilidad, margen, precio mínimo y alerta de pérdida.
- La estimación aclara que logística, peso, situación fiscal y condiciones comerciales pueden cambiar el costo final. Los costos capturados no se persisten todavía y no modifican la publicación; sirven para decidir antes de enviarla.
- `.gitignore` excluye `.DS_Store`, archivos locales de configuración y variantes de `.env` (excepto un posible `.env.example`). Los `.DS_Store` que ya estaban rastreados no se eliminaron porque contienen cambios previos ajenos a esta tarea.
- La lista completa de variables de despliegue y el procedimiento de rotación está en `docs/deployment.md`; nunca se deben copiar valores reales a ese documento ni a `PROJECT_CONTEXT.md`.

## Verificación pendiente

- `dotnet build --no-restore` pasó con 0 errores y sin advertencias.
- Tras el ajuste de inicio de Render y la lectura de `DATABASE_URL`, `dotnet build --no-restore` pasó con 0 errores y sin advertencias. Falta validar el arranque con la URL real de la base PostgreSQL en Render.
- Tras rehacer el panel de estado por cuenta y agregar desconexión individual, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras agregar variantes, administración de SKU e integración de variaciones con Mercado Libre, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras eliminar la migración por HTTP, agregar el modo `--migrate` y limitar el dashboard de Hangfire a `Development`, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; `git diff --check` también pasó.
- Tras retirar los secretos versionados y habilitar User Secrets para desarrollo, `dotnet build --no-restore` pasó con 0 errores y sin advertencias; el escaneo del árbol de trabajo no encontró los tres valores retirados. Las integraciones incompletas no bloquean el arranque y se validan al utilizar sus endpoints.
- Tras ampliar el centro de pedidos y agregar su vista de detalle, `dotnet build MarketplaceSync.Web.csproj --no-restore` pasó con 0 errores y sin advertencias.
- La migración de inventario se generó con `dotnet-ef` 8.0.11 dentro del SDK oficial .NET 8, se aplicó sobre PostgreSQL local y se verificaron la tabla, el índice único, el valor predeterminado 5 y el arranque posterior.
- El rediseño del dashboard se validó con una cuenta y organización temporales: el registro respondió 302, el dashboard autenticado respondió HTTP 200 y renderizó métricas, tareas, configuración y productos; luego se eliminaron ambos registros temporales.
- El flujo de categorías se validó de extremo a extremo con datos temporales: el predictor devolvió `MLM1055`, la vista mostró la sugerencia y la carga de atributos devolvió los cinco campos obligatorios de celulares. El usuario, organización y producto temporales fueron eliminados después.
- La pantalla de más vendidos se validó con un usuario y organización temporales: respondió HTTP 200, conservó la categoría `MLM1055` y mostró el estado para conectar una cuenta. El proyecto compiló con 0 errores y 0 advertencias. La consulta real del ranking queda para producción porque la base local no contiene una cuenta Mercado Libre conectada.
- El detector de oportunidades se verificó con datos controlados: calculó la mediana, mantuvo todas las puntuaciones entre 0 y 100, ordenó por oportunidad y reconoció un ítem existente. La imagen Docker se reconstruyó y la aplicación inició correctamente; no requiere migración.
- El flujo para preparar una oportunidad compiló con 0 errores y 0 advertencias, la imagen Docker se reconstruyó y el contenedor inició correctamente. No requiere migración; la publicación real sigue necesitando confirmación explícita en el formulario de Mercado Libre.
- La carga automática de atributos y el indicador de preparación compilaron con 0 errores y 0 advertencias. No requieren migración.
- La calculadora compiló con 0 errores y 0 advertencias; el parser de la respuesta oficial se verificó con comisión porcentual, cargo fijo y comisión total, y Docker inició correctamente. La consulta real exige el token de una cuenta conectada y no requiere migración.
- Mantener la validación de migraciones nuevas sobre PostgreSQL local antes de aplicarlas en producción. Cuando el runtime local no coincida con .NET 8, usar `dotnet-ef` 8.0.11 dentro de la imagen oficial del SDK como se hizo con inventario.
- Completar pruebas manuales de registro, aislamiento entre organizaciones, importación y OAuth.
- Mover secretos de `appsettings.json` a variables de entorno y cifrar tokens en reposo antes de operar con vendedores reales.
- Rotar la contraseña local compartida, `Importer__Key` y `Ebay__MarketplaceDeletionVerificationToken` si los valores que antes estaban versionados llegaron a utilizarse. Configurar en Render solamente las integraciones que se habiliten.
- Configurar el despliegue para ejecutar `dotnet MarketplaceSync.Web.dll --migrate` como tarea previa y abortar la publicación si el comando falla. No volver a crear un endpoint HTTP para migraciones.

## Siguiente módulo

Publicar el stock central de cada variante hacia Mercado Libre con cola, reintentos e historial por publicación. Después agregar reservas y preparación de envíos sin mezclar el estado comercial del pedido con el estado logístico.

## Decisiones del modelo

- Los datos operativos pertenecen a `Organization`, no a una persona individual.
- `OrganizationMembership` asocia usuarios con organizaciones y define el rol.
- `Product.CreatedByUserId` y `MercadoLibreToken.ConnectedByUserId` son metadatos opcionales; el aislamiento se basa en `OrganizationId`.
- El importador externo usa `Importer:OrganizationId` cuando existan varias organizaciones.
