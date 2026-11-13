# Fondos y marca de Ascendra

Esta es la única carpeta operativa para las imágenes que usa el dashboard.
Cambia el archivo conservando exactamente su nombre y formato, y la aplicación
tomará la nueva versión sin modificar código.

| Archivo | Uso |
| --- | --- |
| `leaves-day.jpg` | Fondo principal en modo claro. |
| `galaxy.jpg` | Fondo principal en modo oscuro. |
| `login.jpg` | Fondo de la pantalla de acceso. |
| `xolo-wolf.png` | Logotipo del encabezado. |

Para ver el cambio localmente, sustituye el archivo y recarga el navegador. La
URL se versiona con la fecha del archivo para evitar que el navegador conserve
un fondo anterior en caché. Para Render, confirma el cambio de imagen en
GitHub y realiza un despliegue. Las copias en `src/.../wwwroot/assets` son
históricas y se excluyen de la publicación: no las edites.

La aplicación tampoco guarda en caché las pantallas, CSS o JavaScript propios:
al terminar un despliegue, basta recargar normalmente la página. No será
necesario abrir otro navegador ni borrar el historial.
