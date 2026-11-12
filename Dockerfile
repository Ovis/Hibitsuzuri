FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Hibitsuzuri.Web/Hibitsuzuri.Web.csproj src/Hibitsuzuri.Web/
RUN dotnet restore src/Hibitsuzuri.Web/Hibitsuzuri.Web.csproj
COPY src/Hibitsuzuri.Web/ src/Hibitsuzuri.Web/
RUN dotnet publish src/Hibitsuzuri.Web/Hibitsuzuri.Web.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
WORKDIR /app
RUN mkdir /data && chown 1654:1654 /data && chmod 700 /data
COPY --from=build /publish/ ./
COPY LICENSE ./LICENSE
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__Directory=/data \
    DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "Hibitsuzuri.Web.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Hibitsuzuri.Web.dll"]
