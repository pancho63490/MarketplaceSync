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
- El historial de mercado guarda una captura diaria por organización y categoría con las 10 posiciones, precio, marca, imagen y puntuación de oportunidad. La restricción única diaria evita duplicados tanto entre consultas manuales como ante ejecuciones concurrentes.
- Hangfire ejecuta `capture-mercadolibre-ranking-history` diariamente a las 12:15 UTC para las 12 categorías más usadas de cada organización con una cuenta conectada. Una consulta manual también guarda la captura del día para iniciar el historial inmediatamente.
- La pantalla de tendencias permite periodos de 7, 30 y 90 días, grafica la evolución del Top 5 y compara la última captura con la anterior para mostrar ascensos, descensos, entradas, salidas y cambios de precio.
- La migración `20261101155154_AddMarketRankingHistory` crea `MarketRankingSnapshots`, `MarketRankingSnapshotItems`, sus relaciones, el índice diario único y los índices de consulta.
- Los resultados `USER_PRODUCT` del ranking pueden pertenecer a otros vendedores; Mercado Libre entrega su ID y posición, pero puede negar la ficha en `/user-products/{id}`. El servicio intenta entonces relacionar el ID mediante `products/search`; si tampoco existe coincidencia, marca la ficha como privada, reduce su puntuación y bloquea la creación de un borrador genérico.
- La captura del día se actualiza al volver a consultar en lugar de duplicarse. Esto permite reemplazar fichas genéricas del historial por datos enriquecidos después de mejorar la resolución o cuando Mercado Libre vuelve a exponerlos.
- `.gitignore` excluye `.DS_Store`, archivos locales de configuración y variantes de `.env` (excepto un posible `.env.example`). Los `.DS_Store` que ya estaban rastreados no se eliminaron porque contienen cambios previos ajenos a esta tarea.
- La lista completa de variables de despliegue y el procedimiento de rotación está en `docs/deployment.md`; nunca se deben copiar valores reales a ese documento ni a `PROJECT_CONTEXT.md`.
- **Radar de compra** (`PurchasingController`, `PurchaseRadarService`, sección "Comprar en EE.UU."): decide qué traer de EE.UU. antes de publicar. Usa 21 días del historial de Más vendidos para calcular una demanda de 0 a 100 (constancia en el Top 45%, posición 35%, tendencia 20%), busca la oferta en eBay EE.UU. (nuevo, precio fijo, ubicado en EE.UU.) y calcula la utilidad vendiendo al precio actual del mercado con `CrossBorderPricingService.CalculateProfitAtPrice`.
- La búsqueda en eBay se arma con `SourceSearchQueryBuilder`: marca y números de parte primero, traducción por diccionario de términos comunes (autopartes y generales) y, si existe `Anthropic:ApiKey`, traducción con Claude. La coincidencia se puntúa por número de parte, marca y palabras; por debajo de 40 no se acepta y el candidato queda "Por revisar" para vincular el link manualmente.
- Estados del candidato: `Suggested` (comprar), `NoSource`, `LowMargin`, `Discarded`, `Purchased` (en camino) y `Received`. "Ya lo compré" crea producto, SKU `RC-{id}` con stock 0 y borrador de publicación; "Recibido" suma el stock con un `InventoryMovement` `PurchaseReceipt` idempotente. La pestaña "Volver a comprar" usa las ventas propias de 30 días para cubrir 21 días de stock. Se exporta una lista de compras CSV para el viaje al Paso.
- Hangfire ejecuta `refresh-purchase-radar` diariamente a las 12:45 UTC, después de la captura del ranking. La migración `20261104175627_AddPurchaseCandidates` crea `PurchaseCandidates`; el snapshot del modelo ahora también incluye `CrossBorderSettings`, que faltaba.
- `SourcePriceMonitorService` (cada 4 horas) procesa productos en secuencia (antes usaba `Task.WhenAll` sobre un mismo `DbContext`), no pausa publicaciones con inventario propio, reactiva con un máximo de 5 piezas, no cambia el precio de publicaciones con variaciones, usa el token de la cuenta de cada publicación y solo actualiza el estado local si Mercado Libre confirma. Se eliminó el registro duplicado de `ScraperClient`.

- La IA usa la API de Claude (Anthropic) en lugar de Gemini: `ClaudeClient` es compartido por `ClaudeProductAiEnricherService` y `SourceSearchQueryBuilder`. Se configura con `Anthropic:ApiKey` (o `ANTHROPIC_API_KEY`) y `Anthropic:Model` (predeterminado `claude-haiku-5-5`). Ante cualquier error devuelve null y cada servicio usa su respaldo por reglas.
- El menú lateral y el de celular se generan de una sola lista en `_Layout.cshtml`, agrupada en Principal, Vender (Pedidos, Productos, Inventario), Comprar en EE.UU. (Radar de compra, Más vendidos MX, Costeo) y Configuración (Cuentas de Mercado Libre). Se quitó "Importar por enlace" del menú porque repetía el botón "Importar producto" de la barra superior; el menú de celular antes era una copia manual a la que le faltaban secciones.
- **UX (nov 2026):** los avisos de éxito/error se muestran en todas las pantallas desde `_Layout` (`Views/Shared/_Flash.cshtml`); las vistas que dibujan los suyos marcan `Context.Items["FlashRendered"]`. Antes Productos y el Dashboard no mostraban ningún aviso tras una acción.
- La lista de Productos se rehízo: búsqueda (título, marca, parte, SKU, ID de ML), filtros por estado en español (Borrador, Por revisar, Listo, Publicado, Pausado, Sin stock en fuente), stock propio por SKU, una acción principal según el estado y el resto en un menú "⋯". El subtítulo de la barra superior ahora es por pantalla (`ViewData["Subtitle"]`).
- Actualizar desde la fuente ya no sobrescribe título ni descripción (solo los completa si están vacíos), para no perder ediciones ni el texto generado con IA.
- El Dashboard agrega "Listos para publicar", "Qué comprar en EE.UU." y "Compras en camino", con enlaces filtrados. Más vendidos MX acepta texto libre ("bomba de agua") y lo convierte en categoría con `domain_discovery`; muestra nombres de categoría en lugar de IDs.
- **RockAuto:** no tiene API pública y su `robots.txt` bloquea el acceso automatizado, así que no se integra por scraping. En el Radar, "Por revisar" permite pegar un link de RockAuto (u otra tienda) con el precio en USD capturado a mano; eBay sigue leyéndose por API.
- **Rediseño visual (nov 2026):** sistema de diseño en `wwwroot/css/theme.css` (se carga después de Bootstrap y `app.css`). Paleta papel frío / tinta azul marino / ámbar de señalización solo para la acción principal; tipografía Archivo variable (ancho y peso) servida desde `wwwroot/fonts` (licencia OFL incluida), sin dependencia de Google Fonts. Títulos de página en Archivo expandido (`.page-title`). Se eliminaron los ~300 líneas de estilos inline del layout y las etiquetas en mayúsculas; los colores fijos de las vistas se mapearon a variables (`--ink`, `--denim`, `--signal`, `--ok`, `--bad`, etc.).
- El layout ya no tiene barra superior en escritorio: el título vive en cada pantalla, "Importar producto" y la cuenta están en el menú lateral. En celular hay una barra con marca, "Importar" y un menú lateral (offcanvas) generado de la misma lista. Las pantallas de acceso no muestran el menú.
- El inicio se llama **Hoy**: saludo con el nombre del negocio, lista de pendientes ordenada por urgencia (pedidos pendientes, SKU agotados, compras en camino, productos listos o por preparar, stock bajo, qué comprar), pasos iniciales solo si faltan, y números de los últimos 30 días. El menú usa nombres de tarea: "Qué comprar" y "Costos de cruce".
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
- La migración del historial se generó con `dotnet-ef` 8.0.11, se aplicó sobre PostgreSQL local y se verificaron ambas tablas, el índice diario único, el arranque y el registro del trabajo recurrente. La vista se probó con dos capturas temporales y mostró correctamente subida, bajada, producto nuevo, salida y gráfica; todos los datos temporales fueron eliminados.
- La recuperación de `USER_PRODUCT` se verificó con una respuesta controlada: ante un 403 en la ficha privada, el buscador oficial devolvió nombre y marca del catálogo y el resultado quedó habilitado. El proyecto compiló con 0 errores y 0 advertencias.
- Se corrigió la actualización de capturas diarias en `MarketRankingHistoryService.CaptureAsync` para sincronizar los ítems en el lugar (*in-place*) en vez de `RemoveRange` + `BuildItems`, evitando la violación de restricción única (`IX_MarketRankingSnapshotItems_MarketRankingSnapshotId_ExternalId`) en PostgreSQL.
- Se corrigió la gestión de memoria en `MercadoLibreBestSellerService.GetTopAsync` liberando de forma segura `categoryTask` si `highlights` falla antes de completarse.
- Se implementó el modelo `CrossBorderSettings` y la migración `20261104163500_AddCrossBorderSettings` para guardar por organización el tipo de cambio USD/MXN, porcentaje de cruce/impuestos, cuotas fijas, margen neto objetivo, ganancia mínima, costo de Mercado Envíos, comisión MeLi y retenciones de plataforma. La propiedad de navegación `Organization` incluye `[ValidateNever]` y es anulable para evitar que `ModelState.IsValid` falle en el guardado.
- Se implementó `CrossBorderPricingService` y su interfaz `ICrossBorderPricingService`, integrados con `ProductsController.PublishToMercadoLibre` para prellenar automáticamente el precio y costo puesto en Juárez (`LandedCost`) cuando se importan productos en USD de eBay, Amazon o AliExpress.
- Se creó `CrossBorderController` y la vista interactiva `Views/CrossBorder/Index.cshtml` con un simulador en tiempo real que desglosa precio base, cruce aduanal, envío, comisiones y ganancia neta.
- Se implementó `IProductAiEnricherService` para procesar productos crudos con IA (o motor de respaldo por reglas si no hay API key configurada), generando títulos optimizados ≤ 60 caracteres, descripciones limpias en texto plano, extracción de marca/modelo, mapeo de atributos requeridos de MeLi y evaluación de riesgo de propiedad intelectual.
- La compilación `dotnet build --no-restore` pasó con 0 errores y 0 advertencias.
- El Radar de compra compiló con 0 errores (SDK .NET 8, paquetes locales). Se verificaron con datos controlados el cálculo de demanda/tendencia, el filtro de fichas no disponibles, la puntuación de coincidencia con eBay y que `CalculateProfitAtPrice` coincide con `CalculatePricing`. La cadena completa de migraciones, incluida `AddPurchaseCandidates`, se aplicó sin errores sobre un PostgreSQL 16 vacío, y la app arrancó y mostró el radar y el menú nuevo (escritorio y celular). Falta probar con credenciales reales de eBay y de Claude.
- Mantener la validación de migraciones nuevas sobre PostgreSQL local antes de aplicarlas en producción. Cuando el runtime local no coincida con .NET 8, usar `dotnet-ef` 8.0.11 dentro de la imagen oficial del SDK como se hizo con inventario.
- Completar pruebas manuales de registro, aislamiento entre organizaciones, importación y OAuth.
- Mover secretos de `appsettings.json` a variables de entorno y cifrar tokens en reposo antes de operar con vendedores reales.
- Rotar la contraseña local compartida, `Importer__Key` y `Ebay__MarketplaceDeletionVerificationToken` si los valores que antes estaban versionados llegaron a utilizarse. Configurar en Render solamente las integraciones que se habiliten.
- Configurar el despliegue para ejecutar `dotnet MarketplaceSync.Web.dll --migrate` como tarea previa y abortar la publicación si el comando falla. No volver a crear un endpoint HTTP para migraciones.

## Modelo de Negocio: Dropshipping y Arbitraje Transfronterizo (El Paso ➔ Cd. Juárez ➔ Mercado Libre)

- **Fuentes de abastecimiento:** Caza de ofertas y extracción de productos en eBay, Amazon (EE.UU.) y AliExpress.
- **Ruta logística y aduanal:** Recepción/compra en El Paso, TX y cruce e importación a Ciudad Juárez, Chih., para despacho y venta nacional a través de Mercado Libre México.
- **Estructura de costos e impuestos de importación para autopartes / refacciones:**
  - **IGI (Arancel):** 0% si cuenta con certificado de origen T-MEC (EE.UU./Canadá). Si proviene de terceros países (China/Asia como en AliExpress o genéricos de eBay), la tasa general oscila entre 5% y 15% según fracción arancelaria (capítulos 84, 85 y 87 TIGIE).
  - **DTA (Derecho de Trámite Aduanero):** 8 al millar (0.8%) sobre valor en aduana (o cuota fija/exento con T-MEC).
  - **IVA Fronterizo:** Tasa preferencial del 8% bajo el Decreto de Estímulos Fiscales de la Región Fronteriza Norte (o 16% general/comercializadora).
  - **Honorarios de cruce / comercializadora:** Tarifa flat de cruce fronterizo (habitualmente 12% a 18% del valor de factura o tarifa fija por peso/volumen).
  - **Retenciones de plataforma digital (Mercado Libre):** Con RFC persona física: 1% ISR + 8% IVA. Sin RFC: hasta 36% (20% ISR + 16% IVA).
  - **Fórmula de Landed Cost (Costo puesto en Juárez):**
    `Costo_Base_MXN = (Precio_Compra_USD * Tipo_Cambio) + Costo_Envio_USA_USD + Impuestos_Cruce (Arancel + DTA + IVA + Honorarios)`
  - **Fórmula de Precio Venta Sugerido (Maximizador de ganancia):**
    `Precio_Venta = (Costo_Base_MXN + Costo_Envio_MeLi + Utilidad_Deseada_MXN) / (1 - (Tasa_Comision_MeLi + Tasa_Retencion_Impuestos))`

## UpSeller con IA — Automatización de Publicaciones

- **Objetivo:** Publicación desatendida/piloto automático estilo UpSeller, eliminando la carga manual de atributos de Mercado Libre.
- **Flujo:** Extracción de producto ➔ Detección de categoría MLM ➔ Descarga de esquema de atributos ➔ Enriquecimiento con IA (mapeo a `value_id` oficial, título SEO ≤ 60 chars, descripción limpia, filtro de marca/BPP) ➔ Cálculo automático de precio con margen neto ➔ Publicación automática con Hangfire.

## Siguiente módulo

1. Extractor y cazador de ofertas para Amazon y AliExpress (junto con el de eBay existente) para importar fichas crudas completas (imágenes, bullet points, especificaciones).
2. Acción/botón "Enriquecer y Publicar con IA" que conecte `ProductExtractorService` ➔ `MercadoLibreCategoryService` ➔ `ClaudeProductAiEnricherService` ➔ `CrossBorderPricingService` ➔ `MercadoLibreService` en piloto automático con Hangfire.
3. Publicar el stock central de cada variante hacia Mercado Libre con cola, reintentos e historial por publicación.

## Decisiones del modelo

- Los datos operativos pertenecen a `Organization`, no a una persona individual.
- `OrganizationMembership` asocia usuarios con organizaciones y define el rol.
- `Product.CreatedByUserId` y `MercadoLibreToken.ConnectedByUserId` son metadatos opcionales; el aislamiento se basa en `OrganizationId`.
- El importador externo usa `Importer:OrganizationId` cuando existan varias organizaciones.
