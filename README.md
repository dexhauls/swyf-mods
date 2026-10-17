# Mody pro Scam With Your Friends

Neoficiální fanouškovské mody do hry **Scam With Your Friends** (Steam). S vývojáři hry nesouvisí.

**Návod s ukázkou:** https://claude.ai/artifact/44qPxWrhUXsrW3oBmKKaYS

## Stažení

Všechno je v [nejnovějším vydání](https://github.com/dexhauls/swyf-mods/releases/latest).

| Soubor | Co to je |
|---|---|
| `SWYF-start.zip` | Pro nováčky: SWYF Mod Hub + mod Lepší čeština v jednom zipu |
| `SWYF-Hub.exe` | SWYF Mod Hub: instaluje, zapíná a odebírá mody |
| `CzechSpeech.swyfmod` | **Lepší čeština**: hra ti rozumí česky přes velký Whisper model (šepot, křik, drmolení) |
| `DevMenuUnlock.swyfmod` | **Vývojářské menu**: odemkne skryté dev menu hry (F8, jen když hostuješ) |

## Instalace

1. Stáhni `SWYF-start.zip`, rozbal ho a spusť `SWYF-Hub.exe`.
   Kdyby Windows ukázal modré okno: **Další informace → Přesto spustit** (program nemá digitální podpis).
2. Zavři hru a v hubu klikni na **Nainstalovat základ** (jen poprvé).
3. Dvojklikni na soubor `.swyfmod` (nebo ho přetáhni do hubu) a klikni na **Nainstalovat**.
4. Spusť hru přes Steam.

Mody se pak dají zapínat a vypínat v hubu i za běhu hry. **Odinstalovat vše** v hubu vrátí hru do původního stavu.

## Jak to funguje

Hub nainstaluje do hry [BepInEx](https://github.com/BepInEx/BepInEx) s úpravou pro ořezané Unity hry a vlastní Mod Manager, který načítá mody ze složky `Mods` za běhu hry. Lepší čeština používá [whisper.cpp](https://github.com/ggerganov/whisper.cpp) s modelem OpenAI Whisper large-v3 (nebo turbo), který si hub stáhne z Hugging Face s kontrolou SHA256.
