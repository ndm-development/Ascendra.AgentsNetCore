# Scripts operativos

Los scripts se ejecutan fuera de Render, en la computadora donde se reciben los
Excel. No dependen de Dash ni cargan secretos desde el repositorio.

## CRM de referencias

Con `Referencias.xlsx` y `Descarga_cartera.xlsx` en `data/input`, ejecuta:

```bash
python3 -m pip install -r scripts/requirements.txt
python3 scripts/generar_crm_referencias.py
```

Se genera `data/generated/CRM_Contactos.xlsx` de manera atómica. El resultado
reproduce el cruce de la aplicación Dash: conserva una fila por referencia,
agrega teléfonos exclusivos de Cartera como origen `Cartera`, asocia nombre,
producto y saldo, y marca activo/inactivo según la cartera vigente.

Para usar archivos fuera de las carpetas estándar:

```bash
python3 scripts/generar_crm_referencias.py \
  --referencias /ruta/Referencias.xlsx \
  --cartera /ruta/Descarga_cartera.xlsx \
  --salida /ruta/CRM_Contactos.xlsx
```

## Credenciales de usuarios

El padrón `data/input/usuarios_ascendra.xlsx` define quién puede entrar y su
rol. Las contraseñas se guardan únicamente en la variable secreta de Render
`ASCENDRA_AGENT_CREDENTIALS_JSON`.

Para crear el hash de una persona:

```bash
python3 scripts/generar_credenciales.py --usuario joally
```

Para generar las credenciales de todos los usuarios activos del padrón:

```bash
python3 scripts/generar_credenciales.py --todos-activos
```

El script solicita cada contraseña sin mostrarla y entrega un único JSON. Copia
ese resultado directamente como valor de `ASCENDRA_AGENT_CREDENTIALS_JSON` en
Render. No lo guardes en un archivo ni lo subas a GitHub.
