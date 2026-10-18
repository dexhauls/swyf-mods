# Modování Scam With Your Friends

Hra: Unity 6000.3.10 (Mono), síťování přes Mirror. Modloader: **BepInEx 5.4.23.5** + Harmony.

## Projekty

| Složka | Co to je | Kam se sestaví |
|---|---|---|
| `StarterMod/` | Prázdný mod (plugin), šablona pro nové mody. | `Mods/` |
| `DevMenuUnlock/` | Odemkne vestavěné vývojářské menu, jen když hostuješ (F8), a překreslí ho moderně: ikony, přepínače, sbalovací sekce, hledání, čeština. | `Mods/` |
| `CzechSpeech/` | Lepší rozpoznávání češtiny: velký Whisper model z `Mods/CzechSpeech`, vynucená čeština, beam search, vyrovnání hlasitosti (šepot/křik), filtr nesmyslů. Funguje v jakémkoli lobby (mění jen tvůj mikrofon). | `Mods/` (+ model do `Mods/CzechSpeech/`) |
| `Arcade/` | **Arkáda**: aplikace na herním počítači (Kolkata OS) s hrami Pac-Man, Had, Tetris, Cihličky a Pong. Aplikaci přidává Harmony patchem do `KolkataOsController` (bez změny herních souborů), kreslí do pixelového plátna v UI Toolkitu, rekordy ukládá do PlayerPrefs. Hry jdou otestovat mimo hru: `tools/ArcadeTest` (bot + PNG snímky). | `Mods/Arcade/` |
| `Emotes/` | **Emoty**: na klávese drbání v zadku (B, `Shortcuts/ButtScratch`) kolo emotů: podržet, myší vybrat, pustit; kolečko myši = stránka. Herní emoty (katalog `SkinManager`, i zamčené) jdou přes `CmdPlayEmote`, takže je vidí všichni. Navíc animace, které hra má, ale nenabízí (`Standing Greeting`, `Salute`, `Praying`…): patch `PlayerEmoterNetworking.FindClip`, fungují jen když má mod i host (bez něj se přehrají jen tobě). Tanečky navíc: soubory `*.bin` v `Mods/Emotes` (Mixamo FBX → `tools/mixamo2dance.py`, Unity humanoid `.anim` → `tools/UnityBake` v batch režimu Unity), hudba `Mods/Emotes/music/<tanec>.mp3` jako 3D zvuk, který se ztiší při voice chatu. `private/` (cizí obsah) se nebalí ani nepublikuje. | `Mods/Emotes/` |
| `StrippedFix/` | Preloader patcher: oprava ořezané hry (viz níže). Neměnit. | `BepInEx/patchers/StrippedFix/` |
| `ModManager/` | **SWYF Mod Manager**: jediný plugin, který BepInEx načte sám. Načítá mody ze složky `Mods` a za běhu je zapíná, vypíná a aktualizuje. | `BepInEx/plugins/SwyfModManager/` |
| `Installer/` | **SWYF Mod Hub** (`SWYF-Hub.exe`): nainstaluje základ + Mod Manager, pak instaluje / zapíná / odebírá mody `.swyfmod`. | `dist/` |
| `pack-mod.ps1` | Zabalí mod (`<mod>/mod.json` + DLL) do `dist/mods/<id>-<verze>.swyfmod`. | `dist/mods/` |
| `dist/` | Pro kamarády: **`SWYF-Hub.exe`** (pošli jednou) a mody v `dist/mods/*.swyfmod` (posílej zvlášť). | — |
| `docs/` | Stránka s návodem: https://dexhauls.github.io/swyf-mods/ (GitHub Pages, nahrává `build-github-page.ps1`) a kopie na https://claude.ai/artifact/44qPxWrhUXsrW3oBmKKaYS. `build-downloads.ps1` sestaví `SWYF-start.zip`. Soubory ke stažení jsou ve vydáních https://github.com/dexhauls/swyf-mods/releases; po novém vydání uprav odkazy `releases/download/<tag>/` v `docs/index.html`. | — |

Formát modů a pravidla pro načítání za běhu: **[MOD-FORMAT.md](MOD-FORMAT.md)**.

### SWYF Mod Hub

- WPF na .NET Framework 4.8 (je v každých Windows 10/11), jeden exe, bez UAC. Uvnitř má jen základ (`payload.zip`): doorstop, BepInEx, neořezané BCL, StrippedFix a Mod Manager.
- Kroky: **Hra** (najde přes Steam) → **Základ** (jen poprvé nebo při novější verzi, hra musí být zavřená) → **Knihovna modů**. V knihovně jdou mody zapínat a vypínat (soubor `Mods/<id>/disabled`) a odebírat. Nový se přidá přetažením, tlačítkem nebo dvojklikem na `.swyfmod`. Velké soubory z `downloads` stáhne s kontrolou SHA256.
- Po instalaci základu se hub zkopíruje do `%LOCALAPPDATA%\SWYF Mod Hub`, zaregistruje příponu `.swyfmod` (HKCU) a vytvoří zástupce v nabídce Start.
- Když hra běží, hub to ukazuje („změny se projeví hned“) a čte `BepInEx/swyf-modmanager-status.json`, aby u modů svítilo „Běží ve hře“ nebo „Chyba při načtení“.
- Testovací režim: `SWYF-Hub.exe --game "<složka>" [mod.swyfmod]` (přeskočí hledání a neregistruje příponu).
- Build: `dotnet build ModSource/Installer/SwyfInstaller.csproj -c Release` sestaví i StrippedFix a Mod Manager (bez kopírování do hry) a zabalí je.
- Exe není podepsané, takže Windows SmartScreen může napsat „Systém Windows ochránil počítač“ → **Další informace → Přesto spustit**.

Sestavení (Mod Manager DLL nezamyká, takže hra může běžet):

```
dotnet build ModSource/StarterMod/StarterMod.csproj -c Release
```

Po buildu se DLL sama zkopíruje do `Mods` a běžící hra si ji díky Mod Manageru hned načte znovu (bez restartu). Log: `BepInEx/LogOutput.log` (a konzole, která se otevře se hrou — **nezavírat**, zavře i hru).

Patche patří do `StarterMod/Patches.cs` (je tam vzor). Kód hry je hlavně v `Assembly-CSharp.dll` a `ScriptsAssDef.dll`; hra přibaluje `.pdb`, takže dekompilace (ILSpy / `ilspycmd`) je čitelná.

## Emotes 1.1.0: novinky

- **Chůze v tanci podle animace**: tanečky s pohybem posouvají postavu tak, jak jde animace (`MovableDances.cs`, `GaitTrack.cs`, `FootPlanting.cs`). Každý tanec má vlastní nastavení (rychlost, krok, otáčení…) v `DanceTuning.cs`.
- **Fyzika skoků a bruslení**: u poskakujících tanců postava pokračuje v pohybu i přes doskoky a rychlost pruží s každým skokem (`HopCarry`, `HopPush`); na bruslích se odráží, po puštění kláves dlouho dojíždí a kolečka drží do strany, takže zatáčí s bruslemi (`SkatePhysicsPatch` v `MovableDances.cs`).
- **Brusle u Roller Vibes**: při tanci se na nohy nasadí brusle (`RollerSkates.cs`), model je vlastní z Blenderu (`tools/skate_model.py` → `files/skate.bin`).
- **Pohup kamery do beatu**: kamera se při tanci jemně pohupuje v rytmu hudby (`DanceCamera.cs`), tempo (70–180 BPM) se zjišťuje z hudby v `DanceMusic.cs` (offline nástroj `tools/beat_detect.py`).
- **Synchronizace tance s hudbou**: hudba se plánuje přes `AudioSettings.dspTime`, takže animace a smyčka hudby tanečku běží spolu.

## DevMenuUnlock: moderní vzhled

- Herní logiku menu nechává hře (`NetworkingDevMenu.DrawMenu`), Harmony mění jen kreslení: okno, `DrawSectionHeader`, `DrawMenuButton`, `DrawToggleRow` a skrývá „holé“ `GUILayout` volání ve sbalených sekcích.
- **I Unity moduly jsou ořezané** (`UnityEngine.IMGUIModule`, `TextRenderingModule`, `Unity.InputSystem`): chybí `GUI.TextField`, `GUI.Label(Rect, string)`, systémová písma, `InputSystem.DisableDevice`… Kompiluje se proti ořezaným DLL, takže co chybí, odhalí už kompilátor.
- Ikony jsou předem vyrenderované ze Segoe Fluent Icons do `Icons.bin` (vložené do DLL). Po přidání ikony do `MenuLabels.cs` spusť `powershell -File ModSource/DevMenuUnlock/tools/GenerateIcons.ps1`.
- Klikání má vlastní logiku (stisk i puštění na stejném řádku), ne `GUI.Button`. Při psaní do hledání se ztlumí herní klávesy přes `GameInputBindings`.
- Config `BepInEx/config/dex.swyf.devmenuunlock.cfg`: `ToggleKey`, `ModernUi` (false = původní menu), `CzechLabels`, `UiScale`. Kdyby nové kreslení spadlo, mod se sám přepne na původní menu a chybu zapíše do logu.

## CzechSpeech: rozpoznávání češtiny

Hra přepisuje mikrofon přes Whisper (whisper.cpp ~1.7, GPU přes Vulkan): `WhisperVcPipeline` (ScriptsAssDef) → `WhisperManager` (com.whisper.unity). Výchozí model `StreamingAssets/Whisper/ggml-base.bin` (148 MB) je v češtině slabý, pro angličtinu má hra zvlášť `ggml-base.en.bin`. Jazyk bere z Audio nastavení (`CallLanguage`), prompt maže, dekóduje greedy.

Modely (do `Mods/CzechSpeech/`, mod je hledá tam, herní soubory nemění):

| Config `Model` | Soubor | Velikost | SHA256 |
|---|---|---|---|
| `large-v3` (výchozí) | [ggml-large-v3.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin) | 3 095 033 483 B | `64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2` |
| `turbo` (slabší PC) | [ggml-large-v3-turbo.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin) | 1 624 555 275 B | `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69` |

Když zvolený model chybí, mod vezme nejlepší dostupný; bez modelu nechá herní ggml-base, jen s lepším nastavením.

Co mod dělá (Harmony):
- `WhisperManager.InitModel`: hlavní (vícejazyčný) manager přesměruje na model z `Mods/CzechSpeech`, zapne GPU a beam search a **vypne flash attention**. Hra ji má zapnutou, ale na jejím Vulkan buildu whisper.cpp zpomalí enkodér ~10× (large-v3 na 5070 Ti: 2 s → 17 s na větu). Anglický `*.en.bin` nechá být.
- `WhisperWrapper.InitFromFileAsync`: hra čte model do `byte[]`, což nad 2 GB nejde (large-v3 má 3 GB). Mod volá `whisper_init_from_file_with_params` přímo; když selže, načte herní model.
- `WhisperManager.UpdateParams`: jazyk `cs` (config `Language`, prázdné = podle hry), úvodní prompt, beam 5 pro hotovou větu, greedy pro živý náhled (`FastPartials`), potlačení neřečových tokenů.
- `GetRecognizer` / `UseMoonshine`: s vynuceným jazykem jde všechno přes velký model.
- `WhisperManager.GetTextAsync`: vyrovná hlasitost promluvy (šepot zesílí, křik ztlumí, bez clippingu).
- `OnEnable`: práh řeči (`SpeechThreshold`); výchozí 0 = herní 0.002. Nižší hodnota chytí tišší šepot, ale musí zůstat nad šumem místnosti (u uživatele má šum medián ~0.0008–0.0012), jinak věta nikdy neskončí.
- `NormalizeTranscript`: vyhodí typické halucinace („Titulky vytvořil JohnyX“, „Děkuji za sledování“, samotné „Děkujeme.“ nebo holou URL, smyčky opakovaných slov, ozvěnu promptu).

Prompt je schválně popisná věta, kterou nikdo neřekne nahlas („Přepis telefonního hovoru v češtině s diakritikou a interpunkcí.“). Na šumu ji Whisper občas zopakuje a ozvěna se pak zahodí. Původní prompt z běžných frází („Ahoj“, „No jo“) se ozýval jako falešné věty.

Config `BepInEx/config/dex.swyf.czechspeech.cfg`. `Debug.LogTranscripts = true` zapisuje do `LogOutput.log` rozpoznané věty, čas Whisperu a nativní log whisper.cpp (backend, časy enkodéru a dekodéru).

Ověřeno 17. 10. 2026 (RTX 5070 Ti): ve hře 15 vět od hlasu Jakub (normálně, šeptem, křikem s clippingem) přes skutečnou `WhisperVcPipeline`. Všechny přepsané správně, hotový text 1,4–2,1 s po konci řeči. Mimo hru (stejná `libwhisper.dll`): ggml-base 45–64 % chybných slov, large-v3 s modem jen zápis číslic („33“ místo „třicet tři“), 0,9–1,5 s na větu; turbo ~0,35 s a o něco víc chyb. large-v3 zabere ~3,1 GB VRAM a ~3,9 GB commitnuté RAM (fyzicky jen ~0,1 GB).

## Proč to není „jen nainstaluj BepInEx“

Hra je sestavená s agresivním ořezáváním kódu (managed stripping). Z `mscorlib` chybí metody, které BepInEx a Harmony potřebují (`Module.GetPEKind`, `Assembly.LoadFile`, `MethodBody.GetILAsByteArray`…). Řešení:

1. `BepInEx/unstripped_corlib/` — neořezané BCL knihovny (`mscorlib`, `System*`, `Mono.Security`, `netstandard`) zkopírované z **Gamble With Your Friends** (stejný vývojář, Unity 6000.3.6, stejné Mono). `doorstop_config.ini` → `dll_search_path_override = BepInEx\unstripped_corlib`.
2. Plné BCL ale nesedí k ořezaným třídám hry (např. `Mirror.SyncSet<T>` neimplementuje celé `ISet<T>`) → `TypeLoadException`. Patcher `StrippedFix` při každém startu doplní chybějící členy rozhraní / abstraktních tříd jako stuby, které hází `NotSupportedException` (hra je nikdy nevolá). Aktuálně opravuje Mirror, Newtonsoft.Json, UniTask a Unity.InputSystem.

## Když se něco rozbije

- **Po aktualizaci hry** zkontroluj `BepInEx/LogOutput.log`. Kdyby hra přešla na jinou verzi Unity, můžou neořezané knihovny přestat sedět — vzít novější z jiné hry / Unity editoru stejné verze (Windows, `unityjit-win32`).
- **Soubor `preloader_*.log` v kořeni hry** = BepInEx preloader spadl a hra běží bez modů. V souboru je důvod.
- **Vypnout mody úplně:** v `doorstop_config.ini` dát `enabled = false`.
- **Odinstalace:** smazat `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`.

## Multiplayer

Mody mění jen tvého klienta. Úpravy herní logiky (Mirror) obvykle potřebují, aby mod měl host (a často i ostatní). Používej je v lobby s kamarády, ne ve veřejných hrách.
