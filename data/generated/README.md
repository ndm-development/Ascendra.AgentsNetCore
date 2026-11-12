# Archivos generados por Python

Deposita aquí `CRM_Contactos.xlsx`, generado con
`python3 scripts/generar_crm_referencias.py` a partir de `Referencias.xlsx` y
`Descarga_cartera.xlsx`.

El script es independiente de Dash y reproduce su cruce de referencias.
Este Excel se publica junto con los archivos fuente en el disco persistente de
Render; no se sube a GitHub.

La aplicación usa primero este CRM para Referencias y WhatsApp; si no está disponible, puede realizar una consulta de respaldo con `Referencias.xlsx` y la cartera vigente.
