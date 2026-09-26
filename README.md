# M2M XML EDITOR Online Setup

Windows x64 bootstrapper az **M2M XML EDITOR** online telepítéséhez.

A projekt különálló telepítőprojekt: nem része az M2M XML EDITOR alkalmazás forrásának. A feladata, hogy a felhasználó gépén a kiválasztott GitHub release forrásából reprodukálható módon elkészítse és telepítse az alkalmazást úgy, hogy a végfelhasználónak ne kelljen külön Java, Maven, Git vagy .NET környezetet telepítenie.

## Mit csinál a bootstrapper?

A Windows alkalmazás C# / .NET 8 / Avalonia alapú, és a teljes telepítési folyamatot vezérli:

1. adatbiztonsági figyelmeztetést jelenít meg;
2. bekéri a telepítés hatókörét: **Csak nekem** vagy **Minden felhasználónak**;
3. bekéri vagy visszatölti a proxybeállítást, és igény szerint ellenőrzi a GitHub-kapcsolatot;
4. lekéri a publikus GitHub release-eket, és kiválaszthatóvá teszi a stabil/prerelease verziókat;
5. saját cache-ből biztosítja a szükséges **Temurin JDK 21**, **Apache Maven** és **MinGit** környezetet;
6. valódi Git clone-t készít az `m2m-xml-editor` repositoryból, majd checkoutolja a kiválasztott release taget;
7. a repository gyökeréből futtatja:

   ```text
   build-versioned.cmd -DskipTests
   ```

8. a létrejött Spring Boot JAR-ból `jpackage` segítségével Windows app-image-et készít;
9. az app-image-ből IzPack telepítőt állít elő;
10. az IzPack telepítőt háttérben, automatikus módban futtatja;
11. ellenőrzi a telepítés eredményét, létrehozza a Windows eltávolítási bejegyzést, majd sikeres befejező dialógust mutat.

A telepített termék hivatalos verziószáma mindig a **kiválasztott GitHub release/tag**. A `build-versioned.cmd` saját verzióelemzése a Java/Maven build része, de nem írja felül a telepítési release verzióját.

## Fő funkciók

- Windows 10/11 x64 támogatás.
- Stabil és prerelease GitHub release választás.
- Ugyanaz a release újratelepíthető; régebbi verzióra visszalépés tiltott.
- Több M2M XML EDITOR verzió egymás mellett telepíthető.
- Egyszerre csak egy M2M XML EDITOR verzió futhat.
- `Csak nekem` telepítés adminisztrátori jog nélkül.
- `Minden felhasználónak` telepítés UAC-emeléssel.
- H2 esetén opcionális korábbi adat/config másolás.
- MySQL/PostgreSQL/Oracle esetén nincs automatikus konfigurációmásolás.
- Az alkalmazás konfigurációját az M2M XML EDITOR saját `setup.html` oldala végzi.
- Az eltávolítás csak a programfájlokat törli; a felhasználói adat- és konfigurációs könyvtárak megmaradnak.
- Régi, IzPack registry-listeneres telepítések felismerése és kompatibilis eltávolítása.
- Magyar licenc és magyar felület.
- Részletes telepítési napló.
- Proxy módok: Windows rendszerproxy, egyedi proxy vagy proxy nélküli kapcsolat.
- Az egyedi proxy cím/felhasználónév megjegyezhető; a jelszó Windows DPAPI-val, az aktuális Windows felhasználóhoz kötve titkosítva tárolható.
- A kiválasztott proxy a GitHub API, a JDK/Maven/Git letöltések, a Git clone és a Maven build hálózati műveleteire is érvényes.

## Proxy beállítások

A proxybeállító képernyő a telepítési hatókör kiválasztása után, még a GitHub release-lista lekérése előtt jelenik meg. Három mód választható:

- **Windows rendszerproxy** – a bootstrapper a Windows/.NET rendszerproxy-beállítását használja;
- **Egyedi proxy** – külön HTTP és HTTPS proxy URL adható meg; alapértelmezetten ugyanaz a cím használható mindkettőhöz. Opcionális felhasználónév és jelszó is megadható;
- **Proxy nélkül** – a bootstrapper közvetlen hálózati kapcsolatot használ.

A **Kapcsolat ellenőrzése** gomb a GitHub API és a Temurin tényleges letöltési útvonalát vizsgálja a kiválasztott beállítással. A MinGit elérését a Git runtime előkészítése után a bootstrapper külön, valódi `git ls-remote` hívással ellenőrzi.

A **Proxy beállítások megjegyzése** jelölőnégyzet bekapcsolásakor a nem érzékeny beállítások ide kerülnek:

```text
%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\settings.json
```

Az egyedi proxy jelszava nem kerül a JSON-ba. A jelszó külön fájlban, Windows DPAPI-val, az aktuális Windows felhasználóhoz kötve titkosítva tárolódik:

```text
%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\proxy.secret
```

Ha a megjegyzés nincs bekapcsolva, a bootstrapper a korábban mentett proxybeállítást és titkot törli. A jelszó nem kerül a bootstrapper naplójába.

A hálózati kompatibilitás érdekében a bootstrapper a .NET `HttpClient` kéréseket és a MinGit HTTP-forgalmát explicit **HTTP/1.1** protokollra rögzíti. A publikus GitHub repositoryhoz a MinGit credential helper ki van kapcsolva (`credential.helper=`), az interaktív Git/GCM promptok tiltottak. Windows rendszerproxy módban a Git for Windows libcurl/SSPI proxyhitelesítést használja `anyauth` módban, így a Negotiate/NTLM vállalati proxyk a Windows aktuális bejelentkezési hitelesítő adatait használhatják.

## Telepítési könyvtárak

### Csak nekem

```text
Program: %LOCALAPPDATA%\Programs\M2M XML EDITOR\<release>
Adatok:  %LOCALAPPDATA%\M2M-XML-EDITOR\<release>
Cache:   %LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup
Registry: HKCU
```

### Minden felhasználónak

```text
Program: %ProgramFiles%\M2M XML EDITOR\<release>
Adatok:  %ProgramData%\M2M-XML-EDITOR\<release>
Cache:   %ProgramData%\M2M-XML-EDITOR-Online-Setup
Registry: HKLM
```

A tartós adat- és konfigurációs könyvtárakat az eltávolító nem törli.

## Projektstruktúra

```text
bootstrapper-dotnet/   C#/.NET 8/Avalonia Windows bootstrapper
installer-project/     IzPack telepítőprojekt és erőforrások
bootstrapper/          korábbi PowerShell referencia/fallback implementáció
docs/                  részletes fejlesztési dokumentáció
```

A Windows produkciós megoldás a `bootstrapper-dotnet` projekt.

## Fejlesztői előfeltételek

A bootstrapper fordításához szükséges:

- Windows x64;
- .NET 8 SDK;
- Windows PowerShell 5.0 vagy újabb.

A bootstrapper buildjéhez nem kell külön Java, Maven vagy Git. Az IzPack projekt és a konfiguráció a végleges EXE-be beágyazott erőforrásként kerül.

## Fejlesztői build

A `bootstrapper-dotnet` könyvtárból:

```bat
build-windows.cmd
```

A script:

- ellenőrzi a .NET 8 SDK-t;
- előállítja a beágyazott installer erőforrásokat;
- `Release` konfigurációban lefordítja az Avalonia bootstrappert.

Közvetlenül is fordítható:

```bat
dotnet build M2M.XmlEditor.Bootstrapper.csproj -c Release
```

Ebben az esetben a projekt MSBuild targetje Windows alatt automatikusan meghívja a `prepare-embedded-assets.ps1` scriptet.

## Publikálás

A kiadható Windows x64 bootstrapper elkészítéséhez a `bootstrapper-dotnet` könyvtárból:

```bat
publish-windows.cmd
```

A publish:

- `Release` konfigurációt használ;
- `win-x64` célplatformra készül;
- self-contained;
- single-file;
- tömörített;
- tartalmazza a natív library-ket;
- nem ad ki PDB fájlokat;
- ellenőrzi, hogy a publish könyvtárban csak egyetlen EXE maradt.

A kész állomány:

```text
dist\windows-x64\M2M-XML-EDITOR-Online-Setup.exe
```

## Mit kell átadni a felhasználónak?

**Csak ezt az egy fájlt:**

```text
M2M-XML-EDITOR-Online-Setup.exe
```

A végfelhasználónak nem kell átadni és nem kell telepítenie:

- .NET runtime-ot;
- Java/JDK-t;
- Mavent;
- Gitet;
- IzPack JAR-t;
- konfigurációs JSON-t;
- installer-project könyvtárat;
- PowerShell scriptet.

A bootstrapper a szükséges build runtime-okat első használatkor letölti és saját cache-ben kezeli, ezért a telepítéshez internetkapcsolat szükséges.

## Fontos működési szabályok

- A forrás mindig a kiválasztott GitHub release tagből készül.
- A clone rövid munkakönyvtárban és `core.longpaths=true` beállítással történik.
- Az alkalmazás fordítása mindig a repository saját `build-versioned.cmd` scriptjén keresztül történik.
- A telepített verzió neve a GitHub release/tag.
- A bootstrapper az IzPack GUI-t nem mutatja; a telepítést háttérben végzi.
- Sikeres telepítés után a bootstrapper létrehozza az asztali és a verzióspecifikus Start menü parancsikont.
- Sikeres telepítés után külön befejező dialógus jelenik meg, ahonnan az M2M XML EDITOR opcionálisan elindítható.
- A felhasználói adatok eltávolításkor megmaradnak.

## Aktuális technológiai baseline

- .NET 8
- Avalonia 12.1.2
- IzPack 5.2.6
- Temurin JDK 21
- Apache Maven 3.9.16
- MinGit 2.55.0.5
- Windows x64

## Build elotti Avalonia kompatibilitasi ellenorzes

A `build-windows.cmd` a `dotnet build` elott automatikusan futtatja a `validate-axaml-compatibility.ps1` ellenorzest. A vizsgalat az alkalmazott Avalonia 12.1.2 baseline ismert inkompatibilis vagy elavult AXAML mintait mar a forditas elott jelzi, fajl- es sorszam megadasaval. Az ellenorzes nem helyettesiti a tenyleges `dotnet build` futtatasat.


## R29.25 proxy navigáció

A proxybeállítás elfogadása után a proxyablak bezáródik, és csak ezután nyílik meg a fő telepítőablak. A főablak indítási hibája külön hibaüzenetként jelenik meg.

## R29.29 – proxy kivételek és Maven settings

A proxy képernyőn külön megadható és menthető a proxy-kivétellista. A bootstrapper ezt .NET, MinGit (`NO_PROXY`) és Maven/JVM (`http.nonProxyHosts`) oldalon is alkalmazza. Nincs beégetett szervezeti kivétel. A meglévő `%USERPROFILE%\.m2\settings.xml` változatlanul marad és a bootstrapper saját Maven 3.9.16 példánya automatikusan ezt használja. A proxy ablak tartalma görgethető, az alsó gombsor fixen látható marad.

## R29.30 Maven hálózati diagnosztika

A Maven build előtt a bootstrapper titokmentesen naplózza a tényleges proxykörnyezetet, a `http.nonProxyHosts`/`NO_PROXY` értékeket, a felhasználói `~/.m2/settings.xml` mirror/repository céljait, valamint minden Maven célhoz a számított `DIRECT` vagy `PROXY` útvonalat. A bootstrapper JDK truststore helye is megjelenik a naplóban. A céges `settings.xml` változatlan marad.


## R29.31 – interaktív TLS tanúsítványelfogadás Maven PKIX hiba esetén

Ha a Maven `PKIX path building failed` / `certificate_unknown` hibát kap egy HTTPS repository vagy mirror elérésekor, a bootstrapper a Maven kimenetéből azonosítja a hibás TLS végpontot, lekéri a szerver által ténylegesen bemutatott X.509 tanúsítványt, és megjeleníti a szerver, Subject, Issuer, érvényesség és SHA-256 fingerprint adatokat. A felhasználó csak explicit `Megbízom és folytatom` választással engedélyezheti a továbblépést.

Az elfogadás host + port + SHA-256 fingerprint alapján kerül mentésre. Tanúsítványcsere esetén a fingerprint eltér, ezért a program ismét megerősítést kér. A bootstrapper nem módosítja az operációs rendszer tanúsítványtárát és nem írja át a letöltött Temurin JDK eredeti `cacerts` fájlját. Fingerprint-specifikus saját truststore-másolat készül a bootstrapper runtime cache-ben, a tanúsítványt a JDK saját `keytool` programja importálja, majd a Maven build egyszer automatikusan újraindul ezzel a truststore-ral. A TLS-kezelési szolgáltatás nem Windows certificate store-ra épül; a `keytool` elérési útját operációs rendszer szerint oldja fel, így a megoldás Linux irányú későbbi portolásnál is újrahasznosítható.


## R29.32 – reszponzív befejező ablak

A build kimenetének UI-megjelenítése kötegelt és korlátozott lett. A sikeres telepítés befejező ablaka előtt az UI naplófrissítés leáll, ezért nagy Maven/IzPack kimenet után sem torlódhat fel annyi UI-feladat, hogy a `Bezárás` gomb ne legyen kattintható. A teljes diagnosztikai napló továbbra is fájlba kerül.


### R29.33
A Temurin JDK letöltés kétlépcsős: Adoptium assets metadata API -> `binary.package.link` közvetlen ZIP URL. A .NET bootstrapper továbbra is Java 21-et használ build runtime-ként; a projekt Java targetje ettől függetlenül lehet 17.


## R29.34 - DPI-biztos ablakkezeles es licencelfogadas

A telepitesi folyamat fo ablakai maximalizalt allapotban indulnak, hogy 150%-os Windows DPI skalazas mellett se logjon ki a tartalom a kepernyorol. A licencablak tovabbra is kulon, meretezett modal ablak. A licenc megtekintese onmagaban mar nem eleg: a felhasznalonak explicit `Elfogadom` vagy `Elutasitom` dontest kell hoznia. Elutasitas vagy elfogadas nelkuli bezaras eseten a telepites nem folytathato. Elfogadas utan a fo gomb felirata `Telepites`. Bootstrapper verzio: `0.9.12`.

## R29.35 - Git release forrascache es tavoli osszehasonlitas

A bootstrapper a forras-repositoryt mostantol tartos, rovid utvonalu Git cache-ben tartja (`<cache>\g\repo`), ezert ugyanazt a release-t nem klonozza ujra veletlenszeru mappaba. A kivalasztott tag lokalis Git objektumazonositojat `git for-each-ref`, a tavoli objektumazonositot `git ls-remote --tags` segitsegevel hasonlitja ossze. Egyezeskor nincs forrasletoltes; elteres vagy hianyzo lokalis tag eseten csak a kivalasztott tag frissul. Build elott `git reset --hard` es `git clean -fdx` biztosit tiszta working tree-t. Bootstrapper verzio: `0.9.13`.

## R29.36 - opcionális bundled runtime build

A Windows bootstrapper buildje két runtime terjesztési módot támogat. A `bootstrapper-dotnet\build-windows.cmd` továbbra is a meglévő online módot készíti. A `bootstrapper-dotnet\build-windows.cmd --bundle-runtime-components` buildkor letölti vagy cache-ből felhasználja, majd beágyazza a Temurin JDK, Apache Maven és Portable Git csomagokat és a kapcsolódó licencanyagokat. A végfelhasználói telepítéskor a runtime komponensek ilyenkor nem töltődnek le hálózatról. Bootstrapper verzió: `0.9.14`.

### R29.37 / bootstrapper 0.9.15 - bundled runtime cache/proxy javitas

A bundled build eloszor a sajat `.build/runtime-cache` konyvtarat hasznalja. Ha ott nincs csomag, automatikusan atveszi a korabbi bootstrapper `%LOCALAPPDATA%\M2M-XML-EDITOR-Online-Setup\downloads` cache-ebol a Temurin, Maven es Portable Git csomagokat. A Temurin metadata API csak akkor hivodik meg, ha ervenyes JDK ZIP egyik cache-ben sincs. A buildkori HTTP kliens a Windows rendszerproxyjat es az aktualis felhasznalo alapertelmezett hitelesitesi adatait hasznalja. Proxy/tuzfal altal visszaadott nem ZIP tartalom eseten rovid, celzott hiba jelenik meg. Bootstrapper verzio: `0.9.15`.

## R29.38 - MinGit runtime

A Git runtime a teljes Portable Git helyett Git for Windows MinGit 2.55.0.5 csomagot hasznal. Az online es bundled mod ugyanazt a `MinGit-2.55.0.5-64-bit.zip` csomagot hasznalja, amelyet a bootstrapper sajat ZIP-kezelese bont ki; kulon onkicsomagolo folyamat nem indul. Bootstrapper verzio: `0.9.16`.


## R29.39 - Bundled third-party licencek

Bundled build esetén a Temurin, Maven és MinGit ténylegesen beágyazott licenc-/NOTICE-/legal anyaga a bootstrapper felületéről is megnyitható. A fő licencsávban és a licencelfogadó dialógusban külön `Harmadik féltől származó licencek` gomb jelenik meg. Bootstrapper verzió: `0.9.17`.


## R29.40 - online és embedded EXE név

A publish kimenet neve a build módtól függ:

- online: `M2M-XML-EDITOR-Online-Setup.exe`
- `--bundle-runtime-components`: `M2M-XML-EDITOR-Embedded-Setup.exe`

Bootstrapper verzió: `0.9.18`.
