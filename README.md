# OpenDrop Windows

OpenDrop Windows empfängt Dateien aus dem Browser und speichert sie auf dem Windows-PC. Das MVP besteht aus einem ASP.NET-Core-Server für .NET 10 und zwei Browserseiten. Die lokale Empfangsseite zeigt einen QR-Code, den Empfangsordner und die letzten 20 Empfangsbestätigungen der laufenden Sitzung.

## Windows-App und GitHub-Updates

Baue die Desktop-Variante mit dem .NET 10 SDK aus dem Projektordner:

```powershell
dotnet publish .\src\OpenDrop.Server -c Release -p:DesktopBuild=true -r win-x64 --self-contained true -o .\dist\win-x64
```

Ein Doppelklick auf `OpenDrop.Windows.exe` im Ausgabeordner startet den Empfangsserver und zeigt die lokale Empfangsseite im App-Fenster an. Das Fenster bleibt während des Empfangs geöffnet. Beim Schließen stoppt der Server. Die Desktop-Variante verwendet WinForms für dieses Startfenster.

Die App sucht bei jedem Start im Repository `Freddy-hubgit/OpenDrop` nach einem neueren stabilen GitHub-Release. Sie lädt das passende ZIP nach `%LOCALAPPDATA%\OpenDropWindows\Updates` und prüft es gegen die SHA-256-Angabe von GitHub. Wenn neben **Updates prüfen** ein Update bereitsteht, klickst du auf **Installieren**. OpenDrop prüft das ZIP erneut, installiert die Version in einem eigenen Ordner unter dem Benutzerprofil, beendet den bisherigen Server und startet die neue App.

Für das private Repository braucht die App die Prozessvariable `OPENDROP_GITHUB_TOKEN`. Verwende ein **Fine-grained Personal Access Token** mit Zugriff nur auf dieses Repository und **Contents: Read-only**. Gib es per verdeckter PowerShell-Abfrage ein und entferne die Variable nach dem App-Start wieder:

```powershell
$secret = Read-Host 'GitHub-Lesetoken' -AsSecureString
$env:OPENDROP_GITHUB_TOKEN = [System.Net.NetworkCredential]::new('', $secret).Password
try { Start-Process .\dist\win-x64\OpenDrop.Windows.exe } finally {
    Remove-Item Env:OPENDROP_GITHUB_TOKEN
    $secret.Dispose()
}
```

Der Release-Workflow baut und veröffentlicht bei einem Git-Tag wie `v1.1.0` ZIPs für Windows x64 und ARM64. Der Tag muss höher als die installierte Versionsnummer sein.

### Updatefunktion testen

Öffne auf GitHub **Actions → OpenDrop Update-Test → Run workflow** und wähle den Branch **main**. Der Workflow lädt mit Leserechten das Paket des aktuellen stabilen Releases über denselben Updater wie die App. Er prüft den Download aus der Sicht einer älteren Version, die Wiederverwendung des ZIPs und das Verhalten einer bereits aktuellen Version. Er veröffentlicht keinen Release.

Für einen Test im App-Fenster starte eine ältere Version mit einem GitHub-Lesetoken und klicke **Updates prüfen**. In der Werkzeugleiste erscheint der Status. Klicke bei einem bereitstehenden Update auf **Installieren** und prüfe nach dem Neustart die angezeigte Versionsnummer.

## Installation und lokaler Test

Installiere das [.NET 10 SDK](https://learn.microsoft.com/dotnet/core/install/windows) für Windows. CMake ist für das MVP entbehrlich.

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
```

Öffne danach PowerShell neu. Führe im Projektordner aus:

```powershell
dotnet --list-sdks
dotnet build .\OpenDrop.Windows.slnx
dotnet run --project .\src\OpenDrop.Server --no-launch-profile
```

1. Öffne [die Empfangsseite](http://127.0.0.1:5180/owner).
2. Klicke auf **Einladung im Browser öffnen**, wähle eine Datei und klicke auf **Übertragen**.
3. Prüfe die Bestätigung auf beiden Seiten und die Datei im Windows-Ordner **Downloads**. Die Empfangsseite zeigt den genauen Pfad an, auch wenn du Downloads verschoben hast.

Die Einladung gilt 15 Minuten und kann während dieser Zeit mehrfach genutzt werden. Wer den Link oder QR-Code hat, kann hochladen. Beim Neustart wechselt die Einladung. Der Server speichert bis zu 100 MiB pro Datei und verarbeitet jeweils einen Upload. Empfangene Dateien erhalten einen eindeutigen Präfix vor dem Originalnamen. Dateien werden ausschließlich gespeichert. Der Server liefert den Empfangsordner nicht über HTTP aus.

### Automatischer Test

Starte für den Test eine eigene Instanz mit einem leeren Ordner:

```powershell
$testInbox = Join-Path $PWD ("test-inbox-" + [guid]::NewGuid().ToString("N"))
dotnet run --project .\src\OpenDrop.Server --no-launch-profile -- --OpenDrop:Port=5181 "--OpenDrop:Inbox=$testInbox"
```

Führe in einem zweiten Terminal aus:

```powershell
python .\tests\smoke.py 5181
```

## Android und iOS im WLAN

Der lokale Test verwendet HTTP über die Loopback-Adresse. Für mobile Geräte aktiviert OpenDrop einen separaten HTTPS-Listener an der angegebenen privaten IPv4-Adresse. Die Empfangsseite bleibt über Loopback erreichbar.

1. Ermittle mit `ipconfig` die WLAN-IPv4-Adresse des PCs. Beide Geräte müssen sich gegenseitig im Netz erreichen können.
2. Verwende ein vorhandenes PFX-Zertifikat samt privatem Schlüssel und Zertifikatskette. Für die QR-Adresse muss das Zertifikat die IP-Adresse als SAN enthalten. Alternativ verwende einen DNS-Namen, der auf dem Mobilgerät auf die PC-Adresse auflöst und im Zertifikat steht. Der Browser muss der ausstellenden Zertifizierungsstelle vertrauen.
3. Setze die Werte und starte den Server. Setze `OpenDrop__LanHost` nur, wenn du einen DNS-Namen verwendest.

```powershell
$env:OpenDrop__LanIp = '192.168.1.42'
$env:OpenDrop__PfxPath = 'C:\Pfad\opendrop.pfx'
# Optional: $env:OpenDrop__LanHost = 'opendrop.example.com'
$secret = Read-Host 'PFX-Passwort' -AsSecureString
$env:OpenDrop__PfxPassword = [System.Net.NetworkCredential]::new('', $secret).Password
try {
    dotnet run --project .\src\OpenDrop.Server --no-launch-profile
} finally {
    Remove-Item Env:OpenDrop__PfxPassword -ErrorAction SilentlyContinue
    Remove-Item Env:OpenDrop__LanIp,Env:OpenDrop__LanHost,Env:OpenDrop__PfxPath -ErrorAction SilentlyContinue
}
```

Öffne am PC die Empfangsseite und scanne den QR-Code mit der Kamera des Mobilgeräts. Erscheint eine Zertifikatswarnung, prüfe Namen, Gültigkeit und Zertifikatskette vor einer Übertragung. Bei einem Timeout können Windows-Firewall oder WLAN-Client-Isolation den Zugriff verhindern. Die Anwendung erstellt keine Firewall-Regel und installiert keine Zertifikate oder Treiber.

## Geräte im WLAN finden

Aktiviere **Im WLAN sichtbar** auf beiden OpenDrop-Installationen. Die App verwendet UDP-Multicast auf Port 5189 und gibt dabei PC-Namen und optional die HTTPS-Adresse bekannt. Der Schalter ist beim Start aus. Geräte verschwinden nach ungefähr zehn Sekunden ohne Meldung.

Für Windows-zu-Windows erstellt die Desktop-App bei einer privaten WLAN-Adresse ein temporäres TLS-Zertifikat. Schalte die Sichtbarkeit ein und kopiere die Windows-Einladung. Der Sender wählt deinen PC und fügt den von dir direkt erhaltenen Link in der App ein. Die App prüft den im Link enthaltenen Zertifikatsfingerabdruck. Für andere Geräte bleibt der separat eingerichtete HTTPS-Modus mit einem vertrauenswürdigen Zertifikat verfügbar. Unter **Vollbild** blendet das App-Fenster den Windows-Rahmen aus.

### Verbindungsanfragen

Wenn beide Windows-Apps mindestens Version 1.5.0 verwenden, klickt der Sender beim gefundenen Gerät auf **Verbinden**. Vergleicht den sechsstelligen Code und wählt auf dem Empfänger **Annehmen** oder **Ablehnen**. Nach drei Ablehnungen werden weitere Anfragen derselben IP-Adresse für fünf Minuten abgewiesen. **Updates prüfen** wiederholt den automatischen GitHub-Check im App-Fenster.

### Freunde im WLAN

Beide Windows-Apps brauchen mindestens Version 1.6.0. Schaltet zum ersten Verbinden **Im WLAN sichtbar** ein. Der Sender wählt **Als Freund hinzufügen**. Der Empfänger vergleicht den angezeigten Code, markiert **Als Freund speichern** und nimmt die Anfrage an. Danach steht der Kontakt in beiden Freundeslisten.

Später kann die allgemeine Sichtbarkeit aus bleiben. Schaltet auf beiden Rechnern **Für Freunde sichtbar** ein. OpenDrop sendet dafür verschlüsselte Suchmeldungen im selben WLAN. Nur gespeicherte Freunde können die Meldungen zuordnen. Eine Dateiübertragung muss weiterhin einzeln angenommen werden. Beim App-Start sind beide Sichtbarkeitsschalter aus. Das Entfernen eines Freundes widerruft den lokalen Geräteschlüssel.

Der lokale Integrationstest benötigt aktives IPv4-WLAN und eine veröffentlichte Desktop-EXE. Er startet zwei getrennte Instanzen mit eigenen Testprofilen:

```powershell
python .\tests\friends_smoke.py C:\Pfad\zu\OpenDrop.Windows.exe
```

## AirDrop-Status

Die Schaltfläche **WLAN-Adapter prüfen** auf der Empfangsseite listet die von Windows erkannten WLAN-Adapter. Der Status unterscheidet zwischen einem fehlenden Adapter, einem Fehler beim Auslesen und ungeprüfter AWDL-Unterstützung. Das native AirDrop-Modul ist noch ausstehend.

## Projektstruktur und nächste Ausbaustufen

- `src/OpenDrop.Server` enthält HTTP-Endpunkte, Pairing, Ablage und Browser-Oberflächen.
- `tests/smoke.py` prüft den laufenden Server über HTTP.
- Als Nächstes erhält `src/OpenDrop.Desktop` die WinUI-3-Oberfläche für Serverstart, Einladungen und Empfangsstatus. Dafür kommen die [WinUI-3-Entwicklungswerkzeuge](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/create-your-first-winui3-app) hinzu.
- Danach folgen ein Test mit Android und iOS und eine Sendefunktion vom PC zum Mobilgerät.
- Ein separates AWDL/AirDrop-Modul setzt einen Machbarkeitsnachweis für den Realtek-Adapter unter Windows und Zugang zu einem physischen iPhone voraus. Die Unterstützung des originalen AirDrop-Menüs ist offen.
