# Ascendra · Agentes .NET

Migración progresiva de la interfaz Dash de Agentes a ASP.NET Core. Los Excel y el comportamiento de sus generadores Python se conservan sin depender de Dash.

## Estructura importante

- `src/Ascendra.Agents.Web`: aplicación Blazor, seguridad, UI y gráficas.
- `data/input`: mismos Excel fuente que utiliza Dash.
- `data/generated`: resultado de los scripts Python, incluyendo `CRM_Contactos.xlsx`.
- `scripts`: generador autónomo del CRM y generador local de hashes de acceso.
- `assets`: fondos y logotipo editables de Ascendra.
- `data/config`: plantillas autorizadas de WhatsApp.

## Ejecución local

1. Copia los Excel a `data/input` y ejecuta `python3 scripts/generar_crm_referencias.py`.
2. Configura `ASCENDRA_AGENT_CREDENTIALS_JSON` con el mismo secreto que utiliza la aplicación actual.
3. Ejecuta:

```bash
dotnet run --project src/Ascendra.Agents.Web
```

La vista de prueba local se habilita únicamente en desarrollo mediante `Security__AllowLocalPreview=true`; usa `preview` / `preview`. Nunca actives esta opción en Render.

## Render desde GitHub

El repositorio se conecta a Render como hoy. Render construye el `Dockerfile` al recibir un push a la rama configurada; no se construyen ni publican imágenes manualmente. Los Excel están ignorados por Git: Render los conserva en su disco persistente `/app/data`.

Configura como secretos de Render:

- `ASCENDRA_AGENT_CREDENTIALS_JSON`
- `Security__DataProtectionPath` sólo si se conecta un disco persistente para conservar sesiones entre despliegues.

Consulta el procedimiento completo y seguro en [docs/RENDER.md](docs/RENDER.md). Antes del cambio de dominio, ejecuta Dash y esta aplicación contra el mismo corte y valida: totales, alcance por agente, promesas, referencias y exportaciones.
