FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# Npgsql's SASL fallback dynamically loads libgssapi_krb5 on Linux when the
# password auth path fails; without it the loader throws and the real auth
# error is hidden. Install the lib so drift between API and postgres surfaces
# as 28P01 (clear) instead of "Cannot load library ...".
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Simploy.Shared/Simploy.Shared.csproj src/Simploy.Shared/
COPY src/Simploy.Api/Simploy.Api.csproj src/Simploy.Api/
RUN dotnet restore src/Simploy.Api/Simploy.Api.csproj
COPY . .
RUN dotnet publish src/Simploy.Api/Simploy.Api.csproj -c Release -o /app/publish
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Simploy.Api.dll"]
