# Puntero y teclado en un escritorio real

El agente abre una aplicación, escribe en ella y guarda un archivo — en el escritorio
XFCE que tiene delante, no en una copia headless.

## En qué fijarse

**Hace clic en el centro de elementos con nombre, no en píxeles adivinados.** La
percepción viene del propio árbol de accesibilidad del escritorio, así que el agente
pide "el botón Save" y obtiene su recuadro exacto. Cuando el árbol no puede describir
algo —un lienzo, ciertos iconos—, una captura de pantalla y un modelo de visión son el
recurso de reserva, no la opción por defecto. Ese orden es la razón por la que esto
funciona con un modelo local pequeño, y por la que, por defecto, nada de lo que hay en
su pantalla sale de la máquina.

**Escribir puede detenerse y preguntar.** Las pulsaciones de teclas son lo más
arriesgado que un agente puede hacer en un escritorio: el texto leído en la pantalla
podría inducirlo a teclear un comando o un secreto. Puede conceder una autorización
con límite de tiempo que le permita escribir sin preguntar cada vez, que es una
decisión que toma una sola vez en lugar de un aviso que descarta veinte veces.

**Puede quitarle el ratón.** Nada le deja fuera. El agente es otro ocupante de esta
sesión, no su dueño.
