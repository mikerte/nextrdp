FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/NextRdp.ControlPlane/NextRdp.ControlPlane.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY LICENSE NOTICE TRADEMARKS.md THIRD-PARTY-NOTICES.md /licenses/
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD curl --fail --silent http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "NextRdp.ControlPlane.dll"]
