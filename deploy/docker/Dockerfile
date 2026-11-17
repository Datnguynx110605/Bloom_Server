FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "$PROJECT" -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
ARG ASSEMBLY
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV APP_DLL=${ASSEMBLY}.dll
ENTRYPOINT ["sh","-c","exec dotnet \"$APP_DLL\""]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS migrate-build
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet tool restore && \
    for svc in Identity Catalog Inventory Launch Order Payment Fulfillment Notification; do \
      lower=$(echo "$svc" | tr A-Z a-z); \
      dotnet restore "src/$svc/Bloom.$svc" && \
      dotnet ef migrations bundle --project "src/$svc/Bloom.$svc" --configuration Release \
        --output "/bundles/$lower" --force || exit 1; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrate
COPY --from=migrate-build /bundles /bundles
COPY deploy/docker/migrate.sh /migrate.sh
ENTRYPOINT ["/migrate.sh"]
