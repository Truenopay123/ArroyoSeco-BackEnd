# Deploy guide - Vercel (Front) + Railway (API + PostgreSQL)

## URLs de produccion
- Frontend: https://turismoarroyoseco.vercel.app
- Backend: https://arroyoseco-api-production-fcae.up.railway.app

## Cambios ya preparados en el codigo
- CORS de backend en produccion habilitado para el frontend de Vercel.
- AppUrls del backend configuradas con URLs productivas.
- environment.production.ts del frontend apuntando al backend productivo.

## 1) Railway - Backend + PostgreSQL

### Servicio API
- Proyecto: Railway
- Runtime: Nixpacks (ya existe `nixpacks.toml`)
- Puerto: Railway inyecta `PORT` automaticamente
- Dominio: `https://arroyoseco-api-production-fcae.up.railway.app`
- La neurona Flask ahora puede arrancar dentro del mismo servicio Railway en `127.0.0.1:5001`

### Base de datos
- Crear plugin PostgreSQL en Railway.
- Conectar plugin al servicio backend.
- Railway crea `DATABASE_URL` automaticamente (usar esa, no hardcodear credenciales).

### Variables obligatorias (backend)
Estas variables son necesarias para operar en produccion de forma correcta y segura:

- `ASPNETCORE_ENVIRONMENT=Production`
- `DATABASE_URL=<railway-postgres-url>`
- `Jwt__Key=<clave-larga-y-secreta-min-32-chars>`
- `Jwt__Issuer=arroyoSeco`
- `Jwt__Audience=arroyoSeco-client`
- `AppUrls__FrontendBaseUrl=https://turismoarroyoseco.vercel.app`
- `AppUrls__BackendBaseUrl=https://arroyoseco-api-production-fcae.up.railway.app`
- `NEURONA_SERVICE_BASE_URL=http://127.0.0.1:5001`
- `NEURONA_SERVICE_TIMEOUT_SECONDS=30`
- `EMAIL_SMTP_HOST=smtp-relay.brevo.com`
- `EMAIL_SMTP_PORT=587`
- `EMAIL_ENABLE_SSL=true`
- `EMAIL_SMTP_USERNAME=<usuario-brevo-smtp>`
- `EMAIL_SMTP_PASSWORD=<password-o-api-key-smtp-brevo>`
- `EMAIL_FROM=<email-remitente-verificado-en-brevo>`
- `EMAIL_FROM_NAME=Arroyo Seco`
- `MercadoPago__AccessToken=<TEST-... o APP_USR-... segun entorno>`

### Variables recomendadas (backend)
- `SeedAdmin__Email=<admin@tu-dominio.com>`
- `SeedAdmin__Password=<password-fuerte-admin>`
- `MercadoPago__CurrencyId=ARS`
- `MercadoPago__WebhookSecret=<secreto-webhook-mp>`

## 2) Vercel - Frontend Angular

### Configuracion del proyecto
- Root directory: `ArroyoSeco-FrontEnd`
- Framework: Angular
- Build command: `npm run build`
- Output directory: `dist/arroyo-seco/browser`

Nota: `vercel.json` ya tiene `outputDirectory` y `rewrites` para SPA.

### Variables en Vercel
Actualmente el frontend usa `src/environments/environment.production.ts` (valor ya seteado a Railway), por lo que no requiere variable de entorno obligatoria para URL del API.

Si cambias de backend en el futuro, actualiza:
- `src/environments/environment.production.ts`

## 3) Checklist de puesta en marcha
- Crear PostgreSQL en Railway y vincular a la API.
- Cargar todas las variables obligatorias.
- Verificar que el servicio use Nixpacks y no tenga Start Command manual distinto a `start-with-neurona.sh`.
- Deploy backend y verificar `GET /health`.
- Probar la neurona con `POST /api/neurona/calcular-cambio`.
- Deploy frontend en Vercel (root en carpeta FrontEnd).
- Probar registro/login, confirmacion email y reset password.
- Probar flujo de pago de Mercado Pago (crear preferencia y webhook).

## 4) Verificaciones rapidas post-deploy
- Salud API: `GET https://arroyoseco-api-production-fcae.up.railway.app/health`
- Swagger: `https://arroyoseco-api-production-fcae.up.railway.app/swagger`
- Neurona: `POST https://arroyoseco-api-production-fcae.up.railway.app/api/neurona/calcular-cambio`
- CORS desde front productivo: login/register sin errores de navegador.
- Correo Brevo: prueba de confirmacion y recuperacion de password.

## 5) Notas importantes
- No uses claves de prueba en produccion (JWT, Brevo, Mercado Pago).
- Si no configuras `MercadoPago__WebhookSecret`, el backend acepta webhook sin validar firma (funciona, pero menos seguro).
- `DATABASE_URL` es la fuente principal en Railway y tiene prioridad en la app.

## 6) Troubleshooting Railway (error SDK / DLL no existe)
Si Railway muestra mensajes como:
- `The application 'out/arroyoSeco.API.dll' does not exist`
- `No .NET SDKs were found`

No suele ser falta real de SDK. Casi siempre es comando de inicio incorrecto.

Configura en Railway:
- Root Directory: `ArroyoSeco-BackEnd`
- Builder: `Nixpacks`
- Build Command: dejar vacío para que Railway use `nixpacks.toml`
- Start Command: dejar vacío para que Railway use `nixpacks.toml`

Importante:
- Si tienes Build/Start Command personalizados en Railway UI, elimínalos para que se respeten los cambios en `nixpacks.toml`.
- El mensaje `No .NET SDKs were found` aparece también cuando el DLL de arranque no existe. No implica necesariamente que falte instalar .NET.

## 7) Neurona en Railway

La forma más simple para tu caso actual es ejecutar Flask y .NET dentro del mismo servicio Railway:

- Railway expone solo el puerto público del backend .NET.
- Flask corre internamente en `127.0.0.1:5001`.
- El backend llama a Flask usando `NEURONA_SERVICE_BASE_URL=http://127.0.0.1:5001`.

Pasos:

1. Haz push de estos cambios al repo conectado a Railway.
2. En Railway, entra al servicio `arroyoseco-api`.
3. En Settings:
	- Builder: `Nixpacks`
	- Root Directory: el del backend si lo tienes separado
	- Build Command: vacío
	- Start Command: vacío
4. En Variables, agrega o confirma:
	- `NEURONA_SERVICE_BASE_URL=http://127.0.0.1:5001`
	- `NEURONA_SERVICE_TIMEOUT_SECONDS=30`
5. Redeploy.

Si el deploy falla por memoria o tiempo al instalar TensorFlow, la alternativa recomendada es separar la neurona en un segundo servicio Railway Python y apuntar el backend a esa URL interna.
