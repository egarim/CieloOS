# Ejemplos

Cuatro cosas que esta máquina sabe hacer, y que puede ejecutar en lugar de leer
sobre ellas.

Cada carpeta contiene un README breve y un `example.json`. El JSON *es* el ejemplo:
nombra las superficies que usa y el objetivo que persigue, y el agente lo ejecuta a
través del mismo bus verificado por políticas que todo lo demás. Aquí no hay ninguna
vía especial para demostraciones: si un ejemplo funciona, la máquina funciona.

| | qué demuestra |
|---|---|
| `01-record-a-demo` | El agente hace una tarea **y se graba a sí mismo haciéndola**. Obtiene un MP4. |
| `02-drive-the-desktop` | Puntero y teclado sobre un escritorio XFCE real, apoyados en el árbol de accesibilidad y no en píxeles adivinados. |
| `03-research-on-the-web` | Un navegador real, guiado por la estructura de la propia página — y **una solicitud de aprobación**, porque salir de un sitio es una decisión. |
| `04-build-a-spreadsheet` | Un documento producido por un agente, abierto en ONLYOFFICE, en un formato que Excel lee. |

## Cómo ejecutar uno

Abra el panel, vaya a **Examples** y pulse Run. El agente trabaja en *esta* sesión,
así que puede verlo ocurrir en el escritorio que tiene delante, y recuperar el ratón
en cualquier momento.

## Cuente con que le pregunten

El ejemplo 03 se detiene y le pide permiso a mitad de camino. Eso no es una aspereza:
es el objetivo. Algunas acciones de esta máquina son decisiones, y el agente no las
toma solo. Ver una ocurrir le dice más sobre cómo funciona la máquina que este
párrafo.

## Si un ejemplo falla

Dígalo, y conserve la salida. Estos ejemplos son además la forma en que una
instalación nueva demuestra que funciona en su hardware: un fallo aquí es un error
real en esta máquina, no una demostración a la que haya que engatusar.
