# Cloud image (plan D-05). Build context = repository root; the root .dockerignore keeps secrets, keys and
# env files out of the context. Base images are pinned by digest.

# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

# Project files first, so the restore layer is reused until a project file changes.
COPY global.json Directory.Build.props ./
COPY src/Shared/IndustrialIot.Shared.csproj src/Shared/
COPY src/Cloud/IndustrialIot.Cloud.csproj src/Cloud/
RUN dotnet restore src/Cloud/IndustrialIot.Cloud.csproj

COPY src/Shared/ src/Shared/
COPY src/Cloud/ src/Cloud/
RUN dotnet publish src/Cloud/IndustrialIot.Cloud.csproj -c Release -o /app --no-restore

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
COPY --from=build /app .

# Plain HTTP inside the container; compose publishes it on 127.0.0.1 only (no TLS proxy in the trial stack).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The non-root user that ships with the .NET images.
USER app
ENTRYPOINT ["dotnet", "IndustrialIot.Cloud.dll"]
