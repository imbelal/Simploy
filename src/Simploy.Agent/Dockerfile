FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Simploy.Shared/Simploy.Shared.csproj src/Simploy.Shared/
COPY src/Simploy.Agent/Simploy.Agent.csproj src/Simploy.Agent/
RUN dotnet restore src/Simploy.Agent/Simploy.Agent.csproj
COPY . .
RUN dotnet publish src/Simploy.Agent/Simploy.Agent.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# The Agent drives the host Docker daemon via the mounted socket (needs Docker CLI
# + compose plugin) and clones Git repos (needs git). Detect the distro (ubuntu /
# debian) so the correct Docker apt repo is used for the base image.
RUN . /etc/os-release \
    && export ID="${ID:-ubuntu}" \
    && export CODENAME="${VERSION_CODENAME:-stable}" \
    && apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates curl \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID $CODENAME stable" > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin docker-buildx-plugin \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 8089
ENTRYPOINT ["dotnet", "Simploy.Agent.dll"]
