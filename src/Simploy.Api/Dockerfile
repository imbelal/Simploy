FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
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
