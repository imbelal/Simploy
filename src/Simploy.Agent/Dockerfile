FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Simploy.Shared/Simploy.Shared.csproj src/Simploy.Shared/
COPY src/Simploy.Agent/Simploy.Agent.csproj src/Simploy.Agent/
RUN dotnet restore src/Simploy.Agent/Simploy.Agent.csproj
COPY . .
RUN dotnet publish src/Simploy.Agent/Simploy.Agent.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8089
ENTRYPOINT ["dotnet", "Simploy.Agent.dll"]
