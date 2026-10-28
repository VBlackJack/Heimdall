<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Avisos sobre componentes de terceros

*También disponible en inglés: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) y en francés: [THIRD-PARTY-NOTICES.fr.md](THIRD-PARTY-NOTICES.fr.md).*

Heimdall se distribuye bajo la licencia Apache 2.0 (véase [LICENSE](LICENSE)).
Redistribuye los componentes de terceros que se indican a continuación, cada uno
bajo su propia licencia. Este archivo abarca lo que se entrega al usuario. Los
componentes utilizados únicamente para compilar o probar Heimdall se enumeran
por separado al final y no se redistribuyen.

Todas las licencias indicadas aquí se han leído en el propio componente, no se
han deducido: las licencias de NuGet proceden del elemento `<license>` del
archivo `.nuspec` de cada paquete, y las de los componentes incorporados al
repositorio, de sus textos de licencia originales. Véase
[Cómo se verifica este archivo](#cómo-se-verifica-este-archivo).

## Componentes incorporados al repositorio

Estos componentes están incluidos en el repositorio y se distribuyen dentro del instalador.

| Componente | Versión | Editor | Licencia | Proyecto de origen |
|---|---|---|---|---|
| PuTTY `plink.exe` | Release 0.83 | Simon Tatham | MIT | https://www.chiark.greenend.org.uk/~sgtatham/putty/ |
| gsudo `gsudo.exe` | 2.5.1 | Gerardo Grignoli | MIT | https://github.com/gerardog/gsudo |
| draw.io embed | 31.4.5 | JGraph Ltd | Apache-2.0 | https://github.com/jgraph/drawio |
| Microsoft Edge WebView2 SDK | 1.0.2903.40 | Microsoft Corporation | Propietaria, redistribuible | https://developer.microsoft.com/microsoft-edge/webview2/ |

PuTTY tiene el copyright 1997-2026 de Simon Tatham. Solo se redistribuye
`plink.exe`, no el conjunto completo de herramientas PuTTY.

El árbol de draw.io situado en `src/Heimdall.App/Assets/drawio/` es un subconjunto
recortado de la distribución original; los elementos eliminados y los motivos
se documentan en [VENDORED.md](src/Heimdall.App/Assets/drawio/VENDORED.md).

### El único componente sin licencia aprobada por la OSI

Los tres ensamblados de WebView2 de `src/Heimdall.App/lib/webview2/`
(`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`,
`WebView2Loader.dll`) son componentes redistribuibles de Microsoft. Pueden
redistribuirse libremente según los términos de licencia del SDK de Microsoft
Edge WebView2, pero esa licencia es propietaria y no está aprobada por la OSI.
Todos los demás componentes que distribuye Heimdall tienen una licencia aprobada
por la OSI.

Se señala expresamente porque los programas de firma de código para proyectos de
código abierto preguntan si el proyecto contiene componentes propietarios, y
WebView2 es la única respuesta que corresponde dar en el caso de Heimdall.

## Paquetes NuGet redistribuidos con la aplicación

Referencias directas de los proyectos distribuidos que se encuentran en `src/`.

| Paquete | Versión | Licencia |
|---|---|---|
| AvalonEdit | 6.3.1.120 | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| FluentFTP | 54.2.0 | MIT |
| JsonSchema.Net | 7.0.4 | MIT |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | MIT |
| LibGit2Sharp | 0.31.0 | MIT |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.9 | MIT |
| Microsoft.Extensions.Caching.Memory | 10.0.9 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.9 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.9 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 |
| SSH.NET | 2026.0.0 | MIT |
| System.Management | 10.0.9 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.11 | MIT |
| ThemeForge.Theme | 2.1.0 | Apache-2.0 |
| YamlDotNet | 16.3.0 | MIT |

`ThemeForge.Theme` lo publica el autor de Heimdall y también utiliza la licencia Apache-2.0.

Contando las dependencias transitivas, la aplicación distribuida incluye 44
paquetes distintos. La tabla anterior enumera las referencias directas; la lista
completa de dependencias, incluidas las versiones resueltas al compilar, se
obtiene mediante:

```bash
dotnet list src/Heimdall.App/Heimdall.App.csproj package --include-transitive
```

## No redistribuidos: solo para compilación y pruebas

Los proyectos de `tests/` hacen referencia a estos paquetes, que nunca se
entregan en el equipo del usuario. Se enumeran para completar la información,
no como aviso de redistribución.

| Paquete | Versión | Licencia |
|---|---|---|
| FlaUI.Core | 5.0.0 | MIT |
| FlaUI.UIA3 | 5.0.0 | MIT |
| FluentAssertions | 6.12.2 | Apache-2.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.8.0 | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Xunit.StaFact | 1.1.11 | MS-PL |
| coverlet.collector | 6.0.4, 10.0.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |

## Cómo se verifica este archivo

Las licencias se leen en los componentes; nunca se dan por supuestas por su reputación:

- Paquetes NuGet: el elemento `<license type="expression">` del archivo `.nuspec`
  en la caché local de paquetes. `LibGit2Sharp` es anterior a ese elemento y
  declara `<license type="file">`, por lo que su licencia se leyó en el archivo
  `LICENSE.md` incluido en el paquete.
- `plink.exe` y `gsudo.exe`: la página de licencia del propio editor, contrastada
  con los metadatos de versión integrados en el binario.
- draw.io: la versión y la licencia registradas en `VENDORED.md`, junto con la
  información del proyecto de origen.

Revise este archivo siempre que se añada o elimine una dependencia, se actualice
a una nueva versión principal o se renueve un binario de `Assets/` o `lib/`.

Última verificación: 2026-08-31, respecto al commit `9c4241d6`.
