/* Dynamischer Ladeablauf (Warum das funktioniert):

1. Verträge (PluginContracts.dll)
   - Definiert IFeature / IEndpointModule / IPluginEndpointRegistry.
   - Wird im Default AssemblyLoadContext (ALC) geladen, damit Host und Plugins identische Schnittstellentypen teilen.
   - Plugins referenzieren diese DLL; der Host kann Plugin-Objekte sicher über Interfaces casten.

2. Ordner-Überwachung (PluginManager im Host / WebHost)
   - FileSystemWatcher überwacht /Plugins auf *.dll (Erstellen/Ändern/Umbenennen/Löschen).
   - Ereignisse werden entprellt (CancellationTokenSource), um Mehrfachladungen bei teilweisem Schreiben zu verhindern.

3. Sammelbarer Kontext (PluginLoadContext)
   - Für jede Plugin-DLL ein neuer AssemblyLoadContext(isCollectible: true).
   - Load(...) gibt für PluginContracts null zurück (erzwingt geteilte Default-Kopie) und lädt sonst Abhängigkeiten aus dem Plugin-Ordner.
   - Versionen von Abhängigkeiten können so koexistieren.

4. Laden der Assembly
   - DLL per File.Open (mit Share-Flags) oder Stream geöffnet; ctx.LoadFromStream(fs) vermeidet Sperren der Originaldatei.
   - Nur die Typen im neuen Kontext sind isoliert, PluginContracts-Typen bleiben geteilt.

5. Typ-Erkennung
   - asm.GetTypes() filtert auf konkrete Klasse, die IEndpointModule (Web) oder IFeature (Konsole) implementiert.
   - Falls keiner gefunden: Kontext sofort entladen (ctx.Unload()) und Warnung loggen.

6. Instanziierung
   - Activator.CreateInstance(type) erzeugt Instanz im Plugin-Kontext.
   - Cast zu IEndpointModule gelingt, da Interface aus gemeinsamer Default-Assembly stammt.

7. Registrieren von Endpunkten (Web)
   - module.Register(registry):
     registry.AddGet/AddPost erzeugt RouteEndpoint-Objekte.
     Für jeden Endpunkt:
       RequestDelegateFactory.Create(handler) baut den RequestDelegate.
       Routenmuster geparst, HttpMethodMetadata hinzugefügt.
       DisplayName auf "Plugin:<route>" gesetzt für Filterung/Dokumentation.
     DataSource löst ChangeToken aus → Routing wird aktualisiert.

8. Tracking
   - PluginManager speichert Handle {Context, Instance, Assembly, Pfad} in Dictionary nach vollem Pfad.
   - Ermöglicht gezieltes Entladen bei Änderungen/Löschen.

9. Dynamische Dokumentation
   - OpenAPI-JSON unter /openapi/v1.json (dynamisch gebaut aus aktiven Plugin-Endpunkten).
   - Anzeige über Scalar UI unter /scalar (statt Swagger).
   - Jeder Abruf zeigt aktuellen Plugin-Zustand.

10. Entladen / Neu laden
    - Bei Änderung/Löschung:
      a. Dispose der Instanz (Timer/Event-Abmeldungen usw.).
      b. Endpunkte aus DataSource entfernen (RemovePlugin).
      c. ctx.Unload() markiert Kontext als sammelbar.
      d. GC.Collect + WaitForPendingFinalizers + GC.Collect geben Speicher frei.
    - Neu laden in frischem Kontext (Schritte 4–8).

11. Sicherheitsanforderungen
    - Keine statischen Referenzen auf Plugin-Typen zurücklassen.
    - Dispose muss Ressourcen freigeben (Timer stoppen, Events lösen).
    - Bleiben Referenzen bestehen, wird Kontext nicht vollständig gesammelt.

12. Typ-Identität
    - Würde PluginContracts in jedem ALC separat geladen, wären Interface-Typen verschieden und Casts würden scheitern.
    - Rückgabe von null im Load-Override delegiert das Laden an den Default-Kontext → gemeinsame Identität.

Minimaler Pseudo-Ablauf (Web-Host):
    var ctx = new PluginLoadContext(pluginDir);
    using var fs = File.Open(path, ...);
    var asm = ctx.LoadFromStream(fs);
    var type = asm.GetTypes().First(t => typeof(IEndpointModule).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
    var module = (IEndpointModule)Activator.CreateInstance(type)!;
    module.Register(registry);
    _handles[path] = new Handle(ctx, module);

Entladen:
    module.Dispose();
    dataSource.RemovePlugin(module.Name);
    ctx.Unload();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

Konsole: dieselbes Muster mit IFeature.Start/Dispose.

Diese Datei stellt nur eine IEndpointModule-Implementierung bereit; der Host steuert den Ablauf.
*/
using PluginContracts;
using System.Security.Cryptography;

namespace RandomPlugin;

public sealed class RandomEndpoints : IEndpointModule
{
    public string Name => "random"; // Vom Host genutzt zum Gruppieren/Entfernen

    private readonly Random _r = new(); // Zustand nur im Plugin-Kontext

    public void Register(IPluginEndpointRegistry r)
    {
        // Dynamischer Überblick-Endpunkt
        r.AddGet("/random", (Func<object>)(() => new
        {
            guid = Guid.NewGuid(),
            value = _r.Next(),
            utc = DateTime.UtcNow
        }));

        // Zufallszahl innerhalb Grenzen
        r.AddGet("/random/int/{min:int}/{max:int}", (Func<int,int,object>)((min, max) =>
        {
            if (max <= min) return new { error = "max muss > min sein" };
            return new { min, max, value = _r.Next(min, max) };
        }));

        // GUID
        r.AddGet("/random/guid", (Func<object>)(() => new { value = Guid.NewGuid() }));

        // Kryptographische Bytes
        r.AddGet("/random/bytes/{n:int}", (Func<int, object>)(n =>
        {
            if (n < 1 || n > 1024) return new { error = "n muss 1..1024 sein" };
            var bytes = new byte[n];
            RandomNumberGenerator.Fill(bytes);
            return new { n, base64 = Convert.ToBase64String(bytes) };
        }));
    }

    public void Dispose()
    {
        // Keine Ressourcen – hier ggf. Timer/Event-Abmeldungen ergänzen
    }
}

// HINWEIS: Plugins können auch DB-Zugriffe oder externe APIs nutzen. Ressourcen in Dispose freigeben.
/* Eigene API-Endpunkte erstellen:
   1. Neues Class Library Projekt → Referenz auf PluginContracts.
   2. IEndpointModule implementieren (eindeutiger Name).
   3. In Register: r.AddGet / r.AddPost mit Mustern und Handlern.
   4. DLL bauen und in Plugins-Ordner kopieren → Endpunkte aktiv.
   5. DLL löschen/ersetzen → Endpunkte entfernt/aktualisiert.
   Regeln: Konflikte vermeiden, Aufräumen in Dispose, keine statischen Referenzen.

Siehe PokemonPlugin für umfangreiche Datenendpunkte.

Hinweis: Deutsche Pokémon-Daten verfügbar über Endpunkte /pokemon/de/... (siehe PokemonPlugin).
*/

// Upgrade-Hinweis: Alle Projekte auf TargetFramework net10.0 aktualisiert (Preview-SDK erforderlich).
