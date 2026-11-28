# Ascendra · Agentes .NET

Migración progresiva de la interfaz Dash de Agentes a ASP.NET Core. Los Excel y el comportamiento de sus generadores Python se conservan sin depender de Dash.

## Estructura importante

- `src/Ascendra.Agents.Web`: aplicación Blazor, seguridad, UI y gráficas.
- `src/Ascendra.Agents.Domain`: identidades y reglas del padrón, sin dependencias de infraestructura.
- `src/Ascendra.Agents.Application`: casos de uso y contrato del repositorio de personas.
- `src/Ascendra.Agents.Infrastructure`: PostgreSQL, migraciones e importador inicial de headcount.
- `src/Ascendra.Agents.Import`: cargador de fuentes operativas a lotes PostgreSQL.
- `tests/Ascendra.Agents.Tests`: reglas, compatibilidad y pruebas de integración PostgreSQL.
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

El repositorio se conecta a Render como hoy. Render construye el `Dockerfile` al recibir un push a la rama configurada; no se construyen ni publican imágenes manualmente. Hay Excel versionados y el `Dockerfile` copia `data` a la imagen; verifica la configuración del entorno antes de asumir que proceden de un disco persistente.

Configura como secretos de Render:

- `ASCENDRA_AGENT_CREDENTIALS_JSON`
- `ConnectionStrings__Ascendra`, conexión PostgreSQL utilizada por los servicios existentes y las migraciones.
- `Security__DataProtectionPath` sólo si se conecta un disco persistente para conservar sesiones entre despliegues.

Consulta el procedimiento completo y seguro en [docs/RENDER.md](docs/RENDER.md). Antes del cambio de dominio, ejecuta Dash y esta aplicación contra el mismo corte y valida: totales, alcance por agente, promesas, referencias y exportaciones.

## Migración del padrón a PostgreSQL

La primera etapa incorpora `crm_agent_directory`, su historial y el registro de importación. `AgentDirectory__UsePostgres` permanece en `false` por defecto; la nueva fuente se habilita explícitamente por entorno después de validarla. Cartera, contactos, pagos y conciliación todavía utilizan sus fuentes anteriores.

Con `AgentDirectory__UsePostgres=true`, el primer arranque importa `Data__InputPath/headcount.xlsx` únicamente si el padrón está vacío y nunca se importó. La carga completa y su auditoría comparten transacción. Los siguientes arranques y accesos consultan PostgreSQL sin abrir ese Excel. Reponer un archivo antiguo no sobrescribe cambios ni reactiva personas.

Gerente y Director administran `/usuarios`: alta, nombre, alias, usuario, rol y estado. Las bajas conservan la persona y su historial. Cada edición exige la revisión que se leyó; una edición concurrente obliga a recargar. Los nombres completos se forman con nombre y apellidos; el nombre visible se conserva como alias histórico. Los IDs siguen siendo la llave.

Las credenciales permanecen en el secreto de configuración existente. Dar de alta o cambiar un usuario requiere que su clave de acceso exista también en ese secreto. La comprobación de sesión y las escrituras consultan el estado vigente; los circuitos Blazor revalidan identidad y rol cada 15 segundos. Una falla de PostgreSQL no habilita el acceso con una copia anterior del padrón.

Las migraciones de esquema se registran en `application_schema_migrations` al iniciar el servicio PostgreSQL, incluso con el selector del padrón desactivado. Son aditivas para este módulo. Cambiar el selector a `false` vuelve a leer el Excel: antes de una reversión después de operar en DB, concilia los cambios de personas para evitar recuperar accesos antiguos. Esta configuración no publica ni activa por sí sola el código en Render.

## Pruebas de la migración

`dotnet test` ejecuta las reglas y la compatibilidad; las pruebas PostgreSQL requieren `ASCENDRA_TEST_DATABASE`. Sólo se aceptan hosts locales y cada prueba crea y elimina una base con nombre aleatorio. El usuario local de pruebas necesita permiso para crear bases.

Ejemplo de entorno desechable:

```bash
docker run --detach --rm --name ascendra-directory-tests -p 127.0.0.1:55439:5432 -e POSTGRES_USER=ascendra_test -e POSTGRES_PASSWORD=local-test-only -e POSTGRES_DB=ascendra_directory_tests postgres:17-alpine
export ASCENDRA_TEST_DATABASE='Host=127.0.0.1;Port=55439;Database=ascendra_directory_tests;Username=ascendra_test;Password=local-test-only'
dotnet test --no-restore
docker stop ascendra-directory-tests
```

## Carga de fuentes operativas

El importador conserva cada archivo como un lote con su SHA-256, fila y hoja de origen. Crea tablas nuevas `crm_*`; las columnas de datos aceptan `NULL` y sus únicas claves obligatorias son técnicas. Para pagos conserva `anio`, `semana`, `semana_archivo`, `anio_iso` y `semana_iso`.

Primero se genera y revisa el plan; sólo después puede aplicarse el mismo plan. Si alguna fuente cambia entre ambos pasos, el importador se detiene sin escribir. La conexión se toma de `ASCENDRA_IMPORT_CONNECTION` y nunca se registra en archivos o salida:

```bash
dotnet run --project src/Ascendra.Agents.Import -- --manifest /ruta/fuentes.json --report /ruta/plan.json
ASCENDRA_IMPORT_CONNECTION='...' dotnet run --project src/Ascendra.Agents.Import -- --manifest /ruta/fuentes.json --report /ruta/resultado.json --apply --expected /ruta/plan.json
```

La carga inicial ya dejó lotes en PostgreSQL, pero Web continúa leyendo Excel. No habilitar una lectura desde `crm_*_vigentes` hasta completar los servicios y sus pruebas de paridad.
