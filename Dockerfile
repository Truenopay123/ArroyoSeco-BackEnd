FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar archivos de solución y proyecto
COPY ["arroyoSeco.sln", "./"]
COPY ["arroyoSeco/arroyoSeco.API.csproj", "arroyoSeco/"]
COPY ["arroyoSeco.Application/arroyoSeco.Application.csproj", "arroyoSeco.Application/"]
COPY ["arroyoSeco.Domain/arroyoSeco.Domain.csproj", "arroyoSeco.Domain/"]
COPY ["arroyoSeco.Infrastructure/arroyoSeco.Infrastructure.csproj", "arroyoSeco.Infrastructure/"]

# Restaurar dependencias
RUN dotnet restore "arroyoSeco/arroyoSeco.API.csproj"

# Copiar todo el código
COPY . .

# Compilar y publicar
WORKDIR "/src/arroyoSeco"
RUN dotnet publish "arroyoSeco.API.csproj" -c Release -o /app/publish

# Imagen final
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

RUN apt-get update \
	&& apt-get install -y --no-install-recommends python3 python3-venv \
	&& rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY neurona-service /app/neurona-service
COPY start-with-neurona.sh /app/start-with-neurona.sh

RUN python3 -m venv /app/venv \
	&& /app/venv/bin/pip install --upgrade pip \
	&& /app/venv/bin/pip install --no-cache-dir -r /app/neurona-service/requirements.txt \
	&& chmod +x /app/start-with-neurona.sh

# Variables de entorno por defecto
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV NEURONA_INTERNAL_HOST=127.0.0.1
ENV NEURONA_INTERNAL_PORT=5001
ENV NEURONA_SERVICE_BASE_URL=http://127.0.0.1:5001
ENV NEURONA_SERVICE_TIMEOUT_SECONDS=30
ENV PORT=8080

EXPOSE 8080

ENTRYPOINT ["/bin/sh", "/app/start-with-neurona.sh"]
