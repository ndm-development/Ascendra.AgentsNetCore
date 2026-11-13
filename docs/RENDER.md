# Publicación en GitHub y Render

## 1. Datos dentro del repositorio

Este proyecto se publica con sus archivos de datos dentro de las carpetas
`data/input` y `data/generated`, igual que el dashboard Dash. Render recibe
esos Excel en cada despliegue; no usa disco persistente ni carga manual de
archivos.

Antes de cada corte, actualiza los Excel en `data/input` y genera el CRM:

```bash
python3 -m pip install -r scripts/requirements.txt
python3 scripts/generar_crm_referencias.py
```

Después publica los cambios de datos junto con el código:

```bash
git add data
git commit -m "Actualiza corte de datos"
git push
```

Mantén el repositorio **privado**, ya que los archivos contienen datos de
clientes, teléfonos, saldos y el padrón de usuarios.

## 2. Repositorio GitHub

Desde la carpeta raíz del proyecto crea un repositorio privado y publica la
rama `main`. Sube código, documentación, configuración y las carpetas `data`.
No subas archivos `.env` ni secretos.

## 3. Servicio en Render

1. En Render selecciona **New > Blueprint** y conecta el repositorio.
2. Render detectará `render.yaml` y construirá el Dockerfile al recibir cada
   push a `main`.
3. En **Environment**, agrega el secreto
   `ASCENDRA_AGENT_CREDENTIALS_JSON`. No lo escribas en `render.yaml` ni en
   GitHub.
4. Despliega y confirma que `/health` responda correctamente.

La aplicación usa estas rutas incluidas en la imagen:

| Carpeta | Contenido |
| --- | --- |
| `data/input` | Archivos fuente del corte |
| `data/generated` | `CRM_Contactos.xlsx` generado |

## 4. Actualizar un corte

Reemplaza en GitHub (o en tu copia local antes de hacer `push`) los siguientes
archivos en `data/input`:

- `usuarios_ascendra.xlsx`
- `agentes_activos.xlsx`
- `agents_collections.xlsx`
- `promesas_estatus.xlsx`
- `Descarga_cartera.xlsx`
- `Referencias.xlsx`

Genera el CRM en tu computadora y guarda también
`CRM_Contactos.xlsx` en `data/generated`. Haz `commit` y `push`; Render
reconstruirá la imagen con el nuevo corte. No hay pasos adicionales en Render.

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
