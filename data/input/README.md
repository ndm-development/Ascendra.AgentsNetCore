# Archivos fuente

Esta carpeta reúne los archivos intercambiables de las dos áreas de Ascendra.
Conserva los nombres y columnas para que las aplicaciones puedan iniciar sin
ajustes de código.

## Consulta de referencias

- `Referencias.xlsx`
- `Descarga Cartera.xlsx`

Ejecuta desde la raíz del proyecto:

```bash
python3 scripts/generar_crm_referencias.py
```

El script crea `generated/CRM_Contactos.xlsx` a partir de Referencias y Descarga
Cartera. El resultado incluye:

- `Resumen`: conteos de control sin datos de contacto.
- `Registros`: `CLIENTE_UNICO`, teléfono, nombre de referencia, parentesco,
  origen, estatus y los datos disponibles de Cartera. Cada fila de
  Referencias se conserva como un registro. Los teléfonos exclusivos de
  Cartera se guardan como origen `Cartera`.

El archivo `Referencias.xlsx` debe tener una fila por referencia y estas
columnas: `CLIENTE_UNICO`, `Telefono 1`, `Referencia` y `Parentesco`.

No edites las hojas ni cambies sus nombres. Genera un archivo nuevo y publica
el archivo de `generated` completo para conservar el cruce.

## Dashboard de agentes

Estos cuatro archivos tienen el mismo formato y nombre que la versión de
Management. Reemplázalos completos al actualizar la información y reinicia el
servicio para cargar el nuevo corte:

- `agents_collections.xlsx`: pagos con agente, fecha, producto, semana,
  cliente y recuperación. Puede incluir `Plan_de_Pago`,
  `IdPromesa_Asignada` y `Tipo_Asignacion`.
- `promesas_estatus.xlsx`: promesas con sus montos, fechas, tipo, agente y
  estatus.
- `clientes_asignados.xlsx`: cartera vigente con `CLIENTE_UNICO`,
  `ESTATUS_PLAN` y `FIDIAPAGO`. `FIDIAPAGO` define el día programado que se
  muestra y filtra en Recurrencia.
- `agentes_activos.xlsx`: padrón vigente con las columnas `A. PATERNO`,
  `A. MATERNO` y `NOMBRE`. Determina el filtro **Estatus del agente** de
  Cobranza y Cumplimiento de promesas. Un gestor ausente del padrón se muestra
  como inactivo.

El dashboard de agentes excluye automáticamente las filas cuyo agente sea
`Orgánico` (incluye variantes sin acento). No es necesario eliminarlas del
Excel de origen.

## Historial de planes en Referencias

Reemplaza diariamente este archivo completo y conserva exactamente su nombre:

- `PLANES_DEMOGRAFICOS_S39D2 ESTRATEGIA TAU.xlsx`

La aplicación lee la hoja `BASE` y requiere las columnas `CU`, `Folio_Plan` y
`ESTATUS_PLAN`. Al consultar una cuenta en Referencias, muestra sus planes
generados, cumplidos, incumplidos, vigentes y cancelados. La probabilidad
histórica de cumplimiento se calcula como cumplidos entre cumplidos,
incumplidos y cancelados; los vigentes permanecen visibles como conteo separado.
La vista detecta el archivo actualizado en el siguiente ciclo de consulta,
sin regenerar `CRM_Contactos.xlsx`.
