# Un navegador real, y una decisión real

El agente abre una página en un Chromium genuino en este escritorio —puede verlo—,
lee la estructura propia de la página en lugar de los píxeles, y sigue un enlace.

## En qué fijarse

**Se detendrá y le preguntará.** Abrir una dirección es el momento en que "traer una
página" puede convertirse en "enviar datos a alguna parte", y el destino puede haber
sido elegido por un texto que el agente leyó en una página anterior. Por eso lo
aprueba una persona. Este es el ejemplo que le muestra la barrera; los demás en su
mayoría la esquivan.

**Un clic no puede rodear esa barrera.** Hacer clic en un enlace que sale del sitio se
rechaza y se informa, en lugar de convertirse calladamente en una navegación que nadie
aprobó. Verá que ocurre en este ejemplo: el agente lo intenta, es rechazado, y pide
permiso como corresponde.

**El navegador no es el suyo.** El agente trabaja en su propio perfil, así que una
página hostil no puede alcanzar los sitios en los que usted ha iniciado sesión. Esa
separación es además lo único que funciona: un Chromium moderno se niega a ser
automatizado contra su perfil real, que es la misma conclusión por la misma razón.

## Lo que esto no hace

Una vez que una página está abierta, puede hablar con sus propios servidores como
puede hacerlo cualquier página web. La aprobación cubre a dónde VA el agente, no todo
lo que hace una página después de llegar allí. Un control que se sostenga de forma
continua está en construcción.
