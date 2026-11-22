# Guía de implementación de Ascendra

Esta guía reduce cambios repetidos y evita revisar toda la aplicación cuando
se incorpora una función nueva. La regla es sencilla: cada función tiene su
contrato, su servicio, sus permisos, su migración y su interfaz; la pantalla
no consulta directamente la base de datos.

## 1. Antes de crear una función

Definir en una frase qué registra o consulta, qué rol puede hacerlo y qué
datos requieren historial. Después localizar sólo estas cinco piezas:

| Necesidad | Punto único |
| --- | --- |
| Modelo o solicitud de captura | `Domain/Models.cs` |
| Regla y acceso a datos | `Services/<Nombre>Service.cs` |
| Cambio en PostgreSQL | `Infrastructure/DatabaseMigrations.cs` |
| Permiso | `Domain/OperationalPermissions.cs` |
| Pantalla | `Components/Pages/` y `Components/Shared/` |

## 2. Base de datos sin cambios manuales

Cada cambio nuevo de estructura se agrega como una entrada con identificador
único y SQL seguro de repetir en `DatabaseMigrations.All`. Al iniciar,
Ascendra aplica sólo las migraciones que no existan en
`application_schema_migrations`.

Ejemplo:

```csharp
new("2026-12-01-01-nueva-tabla", """
    CREATE TABLE IF NOT EXISTS nueva_tabla (...);
    CREATE INDEX IF NOT EXISTS ix_nueva_tabla_fecha ON nueva_tabla (fecha DESC);
    """),
```

No se deben añadir cambios nuevos al bloque histórico `SchemaSql`; ese bloque
queda para instalaciones desde cero. Los servicios son quienes contienen el
SQL de lectura y escritura de su propia función.

## 3. Permisos y operaciones sensibles

Los nombres de roles y las reglas se concentran en
`OperationalPermissions`. Las páginas usan el atributo `Authorize` y los
servicios validan de nuevo el permiso antes de escribir. Así, agregar un
nuevo rol o una facultad sólo exige cambiar una regla, no recorrer pantallas.

Para confirmaciones de eliminar, cancelar o publicar, usar
`Components/Shared/ConfirmationDialog.razor` en vez de repetir un modal.

## 4. Operaciones con historial

Cuando una entidad puede editarse o eliminarse, conservar una tabla de
historial con: identificador de la entidad, acción, fecha/hora, usuario,
datos anteriores y datos nuevos. La operación y el registro de historial se
guardan en una sola transacción dentro del servicio. `promesas_historial` es
el modelo de referencia.

## 5. Lista mínima de verificación

1. Modelo y validaciones en `Domain`.
2. Servicio con reglas y transacción si modifica datos.
3. Migración idempotente con índice necesario.
4. Regla de acceso en `OperationalPermissions`.
5. Página delgada que sólo llama al servicio y componentes compartidos.
6. Compilar y revisar que la acción no esté disponible para roles no
   autorizados.

Con este patrón, una función futura se evalúa por módulo, no por toda la
aplicación, y los cambios de esquema, permisos, auditoría y diálogos se
resuelven siempre del mismo modo.
