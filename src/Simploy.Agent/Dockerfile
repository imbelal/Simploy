FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Simploy.Shared/Simploy.Shared.csproj src/Simploy.Shared/
COPY src/Simploy.Agent/Simploy.Agent.csproj src/Simploy.Agent/
RUN dotnet restore src/Simploy.Agent/Simploy.Agent.csproj
COPY . .
RUN dotnet publish src/Simploy.Agent/Simploy.Agent.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# The Agent drives the host Docker daemon via the mounted socket, so it needs the
# Docker CLI + compose plugin. It also clones Git repos, so it needs git.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates curl \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo $VERSION_CODENAME) stable" > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 8089
ENTRYPOINT ["dotnet", "Simploy.Agent.dll"]
