# Publicación en GitHub y Render

## 1. Antes de publicar

Los archivos `data/input/*.xlsx` y `data/generated/*.xlsx` están excluidos de
Git para que datos de clientes, teléfonos, saldos y el padrón no lleguen al
repositorio. Conserva una copia local segura de los Excel.

Genera el CRM antes de subir el corte:

```bash
python3 -m pip install -r scripts/requirements.txt
python3 scripts/generar_crm_referencias.py
```

## 2. Repositorio GitHub

Desde la carpeta raíz del proyecto crea un repositorio privado y publica la
rama `main`. Sube código, documentación y archivos de configuración, pero no
los Excel ni un archivo `.env`.

## 3. Servicio en Render

1. En Render selecciona **New > Blueprint** y conecta el repositorio.
2. Render detectará `render.yaml` y construirá el Dockerfile al recibir cada
   push a `main`.
3. Confirma el servicio `ascendra-agents-netcore` y su disco
   `/app/data`. El disco es necesario porque el sistema de archivos normal de
   Render se reinicia en cada despliegue.
4. En **Environment**, agrega el secreto
   `ASCENDRA_AGENT_CREDENTIALS_JSON`. No lo escribas en `render.yaml` ni en
   GitHub.
5. Despliega y confirma que `/health` responda correctamente.

El Blueprint ya declara las rutas de datos en el disco:

| Variable | Valor |
| --- | --- |
| `Data__InputPath` | `/app/data/input` |
| `Data__GeneratedPath` | `/app/data/generated` |
| `Security__DataProtectionPath` | `/app/data/keys` |
| `Security__AllowLocalPreview` | `false` |

## 4. Cargar o renovar un corte

Por medio de la Shell de Render o SFTP/SSH crea, si hace falta, las carpetas
`/app/data/input`, `/app/data/generated` y `/app/data/keys`. Sube a `input`:

- `usuarios_ascendra.xlsx`
- `agentes_activos.xlsx`
- `agents_collections.xlsx`
- `promesas_estatus.xlsx`
- `Descarga_cartera.xlsx`
- `Referencias.xlsx`

Genera el CRM en tu computadora y sube también
`CRM_Contactos.xlsx` a `/app/data/generated`. La aplicación detecta el nuevo
corte cada 45 segundos sin necesitar un nuevo despliegue.

## 5. Usuarios y contraseñas

El Excel `usuarios_ascendra.xlsx` es el padrón autorizado. Las contraseñas no
van en ese archivo: genera los hashes con:

```bash
python3 scripts/generar_credenciales.py --usuario nombre_de_usuario
```

El resultado es un JSON. Pégalo como valor de
`ASCENDRA_AGENT_CREDENTIALS_JSON` en Render y guarda los cambios con
**Save and deploy**. Para rotar una contraseña, vuelve a generar un hash para
la persona y reemplaza el valor completo del secreto.

El disco persistente requiere un servicio de pago de Render y no permite más
de una instancia de la aplicación. Es el intercambio necesario para conservar
los Excel entre despliegues.
