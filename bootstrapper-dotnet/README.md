# M2M XML EDITOR Online Setup – Avalonia bootstrapper

R26-tól a Windows bootstrapper elsődleges implementációja C#/.NET + Avalonia UI.
A korábbi PowerShell bootstrapper referencia/fallback célból megmarad.

## Fejlesztői előfeltétel

- .NET 8 SDK a bootstrapper **fordításához**.
- A végfelhasználó gépén .NET, Java, Maven és Git nem szükséges.

## Build

```cmd
build-windows.cmd
```

## Self-contained Windows x64 publish

```cmd
publish-windows.cmd
```

Kimenet:

```text
..\dist\windows-x64\M2M-XML-EDITOR-Online-Setup.exe
```

R28-tól a publish kimenet egyetlen self-contained EXE. Az IzPack projekt és a bootstrapper konfiguráció beágyazott erőforrásként az EXE része.

## Fő funkciók

- GitHub stable/prerelease release-katalógus token nélkül, cache fallbackkel.
- SemVer downgrade-tiltás.
- Verziózott program- és adatkönyvtár.
- Egyszerre csak egy M2M XML EDITOR verzió futhat.
- Java 21, Maven és MinGit cache, újraletöltés nélkül, ha érvényes.
- GitHub release-forrás mindig frissen letöltve.
- H2 esetén explicit adat/config klónozás újabb release-hez.
- MySQL/PostgreSQL/Oracle/ismeretlen DB esetén automatikus configmásolás tiltott.
- Maven build, jpackage app-image, IzPack telepítő helyi előállítása és indítása.
- Az uninstall továbbra sem törölheti a verziózott adatkönyvtárakat.

## R26.2 – duplikált projektfa védelem

A build nem engedi a `bootstrapper-dotnet\bootstrapper-dotnet` véletlenül beágyazott forrásmappát. Ha ilyen könyvtár maradt egy korábbi kézi másolásból vagy kicsomagolásból, törölni kell a build előtt. A projektfájl az ilyen beágyazott forrásokat MSBuild/Avalonia szinten is kizárja.


## R29 – egységes, minimális kattintású telepítés

A Windows bootstrapper a teljes folyamatot saját Avalonia UI-ban tartja. A háttérben előállított IzPack installer unattended módban fut, ezért nem jelenik meg külön Swing wizard. A licenc megtekintése után explicit `Elfogadom` vagy `Elutasítom` döntés szükséges; a `Telepítés` gomb csak elfogadott licenc mellett aktiválható. Sikeres telepítés után a bootstrapper nem záródik be automatikusan: a befejező nézeten az „M2M XML EDITOR indítása” checkbox alapértelmezetten bejelölt, és a Bezárás gomb indítja el opcionálisan az alkalmazást, majd zárja be a bootstrappert.


## Proxy

A bootstrapper a telepítési folyamat előtt külön proxybeállító ablakot jelenít meg. Támogatott a Windows rendszerproxy, külön HTTP/HTTPS egyedi proxy és proxy nélküli működés. Az egyedi proxy beállítás megjegyezhető; a jelszó `%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\proxy.secret` fájlban, Windows DPAPI-val, felhasználóhoz kötve titkosítva tárolódik. A proxybeállítás a GitHub API, runtime-letöltések, Git és Maven műveletekre is továbbításra kerül.

### Proxy mögötti nagy fájlletöltések

A proxy kapcsolatellenőrzés a GitHub API mellett a Temurin metaadat API-t és a tényleges Temurin csomag letöltési hivatkozását is ellenőrzi. A runtime-letöltések megszakadás esetén legfeljebb négyszer újrapróbálhatók, és HTTP Range támogatás esetén a már letöltött `.part` állományból folytatódnak.


## R29.28 Portable Git proxy kompatibilitás

- A Portable Git credential helper és minden interaktív prompt tiltott a publikus GitHub clone során.
- A Git hálózati forgalom explicit HTTP/1.1.
- Windows rendszerproxy esetén a Git for Windows libcurl/SSPI `anyauth` proxyhitelesítést használja, amely Negotiate/NTLM környezetben a Windows aktuális bejelentkezési hitelesítő adatait használhatja.
- A Git runtime előkészítése után `git ls-remote ... HEAD` kapcsolatellenőrzés fut még a clone előtt.
- Egyedi proxy esetén külön HTTP és HTTPS proxy cím adható meg, illetve választható az azonos cím használata.

## R29.29

- A proxy képernyőn felhasználó által megadható, menthető proxy-kivétellista érhető el.
- A kivételek .NET, Portable Git (`NO_PROXY`) és Maven/JVM (`http.nonProxyHosts`) oldalon is érvényesülnek.
- Nincs beégetett szervezeti hoszt/domain kivétel.
- A meglévő `%USERPROFILE%\.m2\settings.xml` változatlanul marad; a bootstrapper saját Maven 3.9.16 példánya automatikusan ezt használja.
- A proxyablak középső része görgethető, az alsó gombsor fix, az alapmagasság 620 logikai pixel.

## R29.30

- Maven build előtt titokmentes hálózati diagnosztika készül.
- Naplózza a `MAVEN_OPTS`, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY` és `http.nonProxyHosts` effektív értékét.
- A meglévő `~/.m2/settings.xml` mirror/repository/pluginRepository céljaihoz kiírja a számított `DIRECT` vagy `PROXY` útvonalat.
- A settings.xml proxybejegyzései jelszó nélkül látszanak a naplóban.
- A bootstrapper JDK alapértelmezett `cacerts` truststore helye és az esetleges truststore-felülírás is naplózásra kerül.


## R29.31 TLS trust recovery

Maven PKIX/certificate_unknown hiba esetén a bootstrapper a Maven kimenetből feloldott HTTPS végpontról TLS kézfogással lekéri a szerver tanúsítványát. A tanúsítványt nem fogadja el automatikusan: a felhasználónak a Subject, Issuer, érvényesség és SHA-256 fingerprint megjelenítése után jóvá kell hagynia. A döntés host + port + fingerprint szerint kerül mentésre. A rendszer- és JDK truststore változatlan marad; a bootstrapper fingerprint-specifikus másolatot készít, `keytool -importcert` segítségével importál, `MAVEN_OPTS` trustStore felülírással egyszer újrapróbálja a buildet.


## R29.32 UI log batching

A hosszú Maven/IzPack build kimenet nem kerül többé soronként külön UI-dispatch feladatba. A napló megjelenítése kötegelt, a képernyőn tartott szöveg korlátozott, a sikeres befejező ablak megnyitásakor pedig az UI naplófrissítés leáll. A teljes naplófájl változatlanul megmarad. Bootstrapper verzió: `0.9.10`.


## R29.33 - Temurin közvetlen ZIP URL

A bootstrapper a JDK letöltéséhez már nem az Adoptium `binary/latest` átirányításos végpontját használja. Először az `assets/latest/{javaMajorVersion}` metaadat-végpontot hívja meg, majd a válasz `binary.package.link` mezőjében kapott közvetlen ZIP URL-ről tölti le a Temurin JDK-t. A Java főverzió továbbra is konfigurációból jön; jelenlegi értéke 21. A projekt fordítási targetje ettől függetlenül maradhat Java 17. Bootstrapper verzió: `0.9.11`.


## R29.34 - DPI-biztos ablakkezeles es licencelfogadas

A telepitesi folyamat fo ablakai maximalizalt allapotban indulnak, hogy 150%-os Windows DPI skalazas mellett se logjon ki a tartalom a kepernyorol. A licencablak tovabbra is kulon, meretezett modal ablak. A licenc megtekintese onmagaban mar nem eleg: a felhasznalonak explicit `Elfogadom` vagy `Elutasitom` dontest kell hoznia. Elutasitas vagy elfogadas nelkuli bezaras eseten a telepites nem folytathato. Elfogadas utan a fo gomb felirata `Telepites`. Bootstrapper verzio: `0.9.12`.

## R29.35 - Git release forrascache es tavoli osszehasonlitas

A bootstrapper a forras-repositoryt mostantol tartos, rovid utvonalu Git cache-ben tartja (`<cache>\g\repo`), ezert ugyanazt a release-t nem klonozza ujra veletlenszeru mappaba. A kivalasztott tag lokalis Git objektumazonositojat `git for-each-ref`, a tavoli objektumazonositot `git ls-remote --tags` segitsegevel hasonlitja ossze. Egyezeskor nincs forrasletoltes; elteres vagy hianyzo lokalis tag eseten csak a kivalasztott tag frissul. Build elott `git reset --hard` es `git clean -fdx` biztosit tiszta working tree-t. Bootstrapper verzio: `0.9.13`.

## R29.36 - opcionális bundled runtime build

A `build-windows.cmd --bundle-runtime-components` és `publish-windows.cmd --bundle-runtime-components` build közben tölti le/cache-eli és a bootstrapperbe ágyazza a konfigurált Eclipse Temurin JDK, Apache Maven és Portable Git csomagokat. A végfelhasználói futáskor ezek a meglévő runtime cache-be kerülnek, ezért nincs külön runtime hálózati letöltés. Paraméter nélküli build esetén a korábbi online működés változatlan. A bundled build SHA-256 manifestet és third-party licencanyagot is készít. Bootstrapper verzió: `0.9.14`.

### R29.37 / bootstrapper 0.9.15 - bundled runtime cache/proxy javitas

A bundled build eloszor a sajat `.build/runtime-cache` konyvtarat hasznalja. Ha ott nincs csomag, automatikusan atveszi a korabbi bootstrapper `%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\downloads` cache-ebol a Temurin, Maven es Portable Git csomagokat. A Temurin metadata API csak akkor hivodik meg, ha ervenyes JDK ZIP egyik cache-ben sincs. A buildkori HTTP kliens a Windows rendszerproxyjat es az aktualis felhasznalo alapertelmezett hitelesitesi adatait hasznalja. Proxy/tuzfal altal visszaadott nem ZIP tartalom eseten rovid, celzott hiba jelenik meg. Bootstrapper verzio: `0.9.15`.

## R29.38 - MinGit runtime

A Git runtime Git for Windows MinGit 2.55.0.5. A `MinGit-2.55.0.5-64-bit.zip` a meglevo ZIP-kezelovel kerul kicsomagolasra, igy a Portable Git `.7z.exe` onkicsomagolo folyamat megszunt. Online es bundled build egyarant MinGitet hasznal. Bootstrapper verzio: `0.9.16`.


## R29.39 - Bundled third-party licencek

Bundled build esetén a Temurin, Maven és MinGit ténylegesen beágyazott licenc-/NOTICE-/legal anyaga a bootstrapper felületéről is megnyitható. A fő licencsávban és a licencelfogadó dialógusban külön `Harmadik féltől származó licencek` gomb jelenik meg. Bootstrapper verzió: `0.9.17`.


## R29.40 - online és embedded EXE név

A publish kimenet neve a build módtól függ:

- online: `M2M-XML-EDITOR-Online-Setup.exe`
- `--bundle-runtime-components`: `M2M-XML-EDITOR-Embedded-Setup.exe`

Bootstrapper verzió: `0.9.18`.

## R29.41 – licencelfogadás

A third-party licencek kizárólag a felugró licencablakból érhetők el. Bundled/embedded buildben két külön checkbox szükséges az M2M XML EDITOR, illetve a beágyazott third-party komponensek licencfeltételeinek elfogadásához. A `Tovább a telepítéshez` gomb csak mindkettő bejelölése után aktív. Bootstrapper verzió: `0.9.19`.
