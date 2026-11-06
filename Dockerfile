# =========================
# BUILD STAGE
# =========================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore MarketplaceSync.Web.csproj

RUN dotnet publish MarketplaceSync.Web.csproj -c Release -o /app/publish

# =========================
# RUNTIME STAGE
# =========================

FROM mcr.microsoft.com/dotnet/aspnet:8.0

# ImageMagick: fondo blanco y recorte de fotos (PhotoStudioService).
RUN apt-get update \
    && apt-get install -y --no-install-recommends imagemagick \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:10000

EXPOSE 10000

ENTRYPOINT ["dotnet", "MarketplaceSync.Web.dll", "--migrate-and-run"]
