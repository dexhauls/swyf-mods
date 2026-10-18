# Mody pro Scam With Your Friends

Neoficiální fanouškovské mody do hry **Scam With Your Friends** (Steam). S vývojáři hry nesouvisí.

**Návod s ukázkou:** https://dexhauls.github.io/swyf-mods/

## Stažení

Všechno je v [nejnovějším vydání](https://github.com/dexhauls/swyf-mods/releases/latest).

| Soubor | Co to je |
|---|---|
| `SWYF-start.zip` | Pro nováčky: SWYF Mod Hub + mod Lepší čeština v jednom zipu |
| `SWYF-Hub.exe` | SWYF Mod Hub: instaluje, zapíná a odebírá mody |
| `CzechSpeech-1.0.1.swyfmod` | **Lepší čeština**: hra ti rozumí česky přes velký Whisper model (šepot, křik, drmolení) |
| `DevMenuUnlock-1.1.0.swyfmod` | **Vývojářské menu**: odemkne skryté dev menu hry (F8, jen když hostuješ) |
| `Emotes-1.1.0.swyfmod` | **Emoty**: na B kolo emotů místo drbání v zadku ([vydání v1.1](https://github.com/dexhauls/swyf-mods/releases/tag/v1.1)) |
| `Arcade-1.0.0.swyfmod` | **Arkáda**: Pac-Man, Had, Tetris a Pong na počítači v kanceláři ([vydání v1.1](https://github.com/dexhauls/swyf-mods/releases/tag/v1.1)) |

## Instalace

1. Stáhni `SWYF-start.zip`, rozbal ho a spusť `SWYF-Hub.exe`.
   Kdyby Windows ukázal modré okno: **Další informace → Přesto spustit** (program nemá digitální podpis).
2. Zavři hru a v hubu klikni na **Nainstalovat základ** (jen poprvé).
3. Dvojklikni na soubor `.swyfmod` (nebo ho přetáhni do hubu) a klikni na **Nainstalovat**.
4. Spusť hru přes Steam.

Mody se pak dají zapínat a vypínat v hubu i za běhu hry. **Odinstalovat vše** v hubu vrátí hru do původního stavu.

## Jak to funguje

Hub nainstaluje do hry [BepInEx](https://github.com/BepInEx/BepInEx) s úpravou pro ořezané Unity hry a vlastní Mod Manager, který načítá mody ze složky `Mods` za běhu hry. Lepší čeština používá [whisper.cpp](https://github.com/ggerganov/whisper.cpp) s modelem OpenAI Whisper large-v3 (nebo turbo), který si hub stáhne z Hugging Face s kontrolou SHA256.

## Zdrojový kód

Ve složce [`src/`](src/): SWYF Mod Hub (`Installer`), Mod Manager, oprava ořezané hry (`StrippedFix`), šablona nového modu (`StarterMod`) a mody Lepší čeština, Vývojářské menu, Emoty a Arkáda. Jak se sestavují a jak funguje formát `.swyfmod`, popisuje [`src/README.md`](src/README.md) a [`src/MOD-FORMAT.md`](src/MOD-FORMAT.md). Projekty se kompilují proti DLL hry z vlastní instalace (nejsou tu).

