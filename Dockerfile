# Un Dockerfile para los 4 servicios (N-4004): --build-arg SERVICE=Catalog|Orders|Payments|Ticketing
#   docker build --build-arg SERVICE=Orders -t ticketflow-orders .
# Multi-stage: el SDK (~900 MB) compila; la imagen final solo lleva el runtime de ASP.NET.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/ src/
# shortcut: restore y publish en una sola capa (cualquier cambio en src/ re-restaura);
# separar los .csproj en una capa previa si el build de imágenes empieza a doler.
RUN dotnet publish "src/Services/${SERVICE}/TicketFlow.${SERVICE}.Api" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG SERVICE
WORKDIR /app
COPY --from=build /app .
# 0.0.0.0: dentro del contenedor "localhost" no es alcanzable desde afuera (D-0023).
ENV SERVICE_HOST=0.0.0.0 \
    SERVICE_DLL=TicketFlow.${SERVICE}.Api.dll
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet $SERVICE_DLL"]
