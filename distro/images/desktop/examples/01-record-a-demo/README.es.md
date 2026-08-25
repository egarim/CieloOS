# Vea al agente grabarse trabajando

El agente inicia una grabación de pantalla de esta sesión, hace una tarea pequeña en
el escritorio, detiene la grabación y le deja un MP4 en `~/recordings`.

## En qué fijarse

**Aparece en la esquina un indicador rojo RECORDING.** Está ahí porque usted podría
acercarse a un escritorio que ya se está grabando, y no debería depender de que
alguien se lo advierta. Si alguna vez falta, la máquina lo dice en la línea de
auditoría en lugar de seguir grabando calladamente.

**La grabación es un archivo, y nada más que un archivo.** Se queda en su carpeta
personal. Nada la envía a ninguna parte, y no hay ningún comando en esta máquina que
pueda hacerlo — deliberadamente, porque una grabación son miles de capturas de
pantalla.

**Rechaza un archivo corto.** Si la pantalla cambia de tamaño a mitad de la captura
—lo que ocurre cuando alguien conecta un visor—, el grabador informa de un fallo en
lugar de entregarle un material que se cortó a la mitad sin decir nada. Por debajo, la
herramienta que usa termina con éxito en ese caso, así que la máquina comprueba la
duración codificada en vez de creerle.

## Después

El MP4 está en `~/recordings`, descargable desde el panel en **Desks** → **Home**.
Editarlo
para convertirlo en un tutorial narrado —capítulos, subtítulos, anotaciones— es lo
siguiente que se está construyendo.
