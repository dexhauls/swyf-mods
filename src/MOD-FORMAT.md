# Formát modu `.swyfmod` a Mod Manager

## Jak to funguje

- **SWYF Mod Hub** (`dist/SWYF-Hub.exe`) při prvním spuštění nainstaluje do hry základ (BepInEx + oprava ořezané hry) a povinný plugin **Mod Manager** (`BepInEx/plugins/SwyfModManager`). Tohle je jediný krok, kdy hra nesmí běžet.
- Mody se pak posílají zvlášť jako soubor **`.swyfmod`**. Hub ho otevře dvojklikem nebo přetažením a rozbalí do `Mods/<id>/`.
- **Mod Manager** ve hře hlídá složku `Mods` a mody **načítá, vypíná, aktualizuje a odebírá za běhu**, bez restartu hry. DLL čte do paměti, takže soubory nezamyká a hub je může přepsat i za běhu hry.

## Balíček `.swyfmod`

Je to obyčejný ZIP:

```
mod.json          popis modu (viz níže)
files/...         cokoli, co patří do Mods/<id>/ (DLL, obrázky, configy…)
```

`mod.json`:

```json
{
  "id": "DevMenuUnlock",
  "name": "Vývojářské menu",
  "name_en": "Developer Menu",
  "version": "1.1.0",
  "author": "Dex",
  "description": "Krátký popis česky.",
  "description_en": "Short description in English.",
  "icon": "E90F",
  "tags": ["F8", "Jen pro hosta"],
  "tags_en": ["F8", "Host only"],
  "dll": "DevMenuUnlock.dll",
  "downloads": [
    {
      "id": "large-v3",
      "name": "Model large-v3",
      "name_en": "large-v3 model",
      "description": "Nejlepší čeština (3,1 GB).",
      "description_en": "Best Czech accuracy (3.1 GB).",
      "url": "https://…",
      "sha256": "…",
      "size": 3095033483,
      "path": "models/ggml-large-v3.bin",
      "default": true
    }
  ]
}
```

- `id`: jen `[A-Za-z0-9_-]`, slouží i jako název složky `Mods/<id>/`.
- `icon`: hex kód ikony ze Segoe Fluent Icons.
- `dll`: hlavní DLL ve `files/`.
- `downloads` (volitelné): velké soubory, které hub stáhne (s kontrolou SHA256) do `Mods/<id>/<path>`. Kamarád si v hubu vybere, které chce. `default: true` = předvybrané.
- `settings` (volitelné, hub 1.1.2+): volby, které hub ukáže na kartě modu jako segmentový přepínač. Každá volba je jeden klíč v BepInEx configu:
  ```json
  "settings": [{
    "name": "VÝPOČET ŘEČI", "name_en": "SPEECH RUNS ON",
    "file": "BepInEx/config/dex.swyf.czechspeech.cfg", "section": "Model", "key": "Device", "default": "auto",
    "options": [
      { "value": "auto", "name": "Automaticky", "hint": "Tooltip…" },
      { "value": "gpu", "name": "Grafika", "clears": [ "gpu-selhala.txt" ] },
      { "value": "cpu", "name": "Procesor" }
    ]
  }]
  ```
  `file` je relativně ke složce hry. Hub přepíše jen ten jeden řádek (chybějící soubor, sekci nebo klíč založí, zbytek souboru nechá). `clears` jsou soubory v `Mods/<id>/`, které hub při výběru volby smaže. `hint` se ukáže jako tooltip. Mody čtou config při startu hry, takže změna platí od dalšího spuštění.
- `name_en`, `description_en`, `tags_en` (volitelné, i u položek v `downloads` jde `name_en` a `description_en`, v `settings` `name_en` a `hint_en`): anglické texty. Hub i Mod Manager se řídí jazykem hry: čeština nebo slovenština = české pole, jinak anglické. Když anglické pole chybí, ukáže se české.

## Nainstalovaný mod

```
Mods/<id>/mod.json
Mods/<id>/<dll> (+ další soubory, stažené modely…)
Mods/<id>/disabled      ← když existuje, mod je vypnutý (hub ho jen vytvoří/smaže)
```

Volné `*.dll` přímo v `Mods/` se načtou taky, ale balíček stejného modu (složka s `mod.json`) má vždy přednost, i když je vypnutý. Vývoj: projekt s `mod.json` se po buildu sám zkopíruje do `Mods/<název>/` a běžící hra si ho hned načte. Projekt bez `mod.json` se zkopíruje jako volná DLL.

Mody, které něco dělají jen při startu hry (např. CzechSpeech mění načítání Whisper modelu), se za běhu načtou taky, ale tahle část se projeví až po restartu hry. Uveď to v `description`.

## Pravidla pro autory modů (aby šly zapínat/vypínat za běhu)

1. Plugin je normální BepInEx `BaseUnityPlugin` s `[BepInPlugin(guid, name, version)]`.
2. Mod se může načíst **kdykoli**, nejen při startu hry. Co se ve hře stalo před jeho načtením (např. načtení modelu, vytvoření UI), si musí udělat znovu sám.
3. V `OnDestroy()` po sobě uklidit: `harmony.UnpatchSelf()`, zničit vlastní objekty, uvolnit nativní prostředky, vrátit změny ve hře. Manager navíc zavolá `Harmony.UnpatchID(guid)`, takže Harmony ID = GUID pluginu.
4. Své soubory hledat přes `Path.Combine(BepInEx.Paths.GameRootPath, "Mods", "<id>")`, **ne** přes `Assembly.Location` (načtený z paměti je prázdný).
5. Config přes `Config.Bind` funguje jako obvykle (`BepInEx/config/<guid>.cfg`).
6. Aktualizace za běhu: Mono by pro stejné jméno assembly vrátilo starou verzi kódu, proto ji Mod Manager (od 1.0.1) při opětovném načtení přejmenuje na `<jméno>-rN` (v logu „loading the update as …“). Nespoléhej na jméno vlastní assembly ani na statický stav sdílený mezi verzemi.

## Zabalení

```
powershell -File ModSource/pack-mod.ps1 -ModDir ModSource/DevMenuUnlock
```

Skript vezme `ModSource/<mod>/mod.json` a sestavenou DLL a vytvoří `ModSource/dist/mods/<id>-<version>.swyfmod`.
