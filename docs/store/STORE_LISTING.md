# Microsoft Store listing — IsleBar

Copy each block into Partner Center › your app › Store listings › (language). Fields marked *required* must be filled for
every language you add. Screenshots (1-terminal … 10-dark.png, 1920×1080) and the trailer (trailer.mp4) for each language are in `dist\store\<lang>\` (made from the films; not committed).

> Trademarks: "Claude Code" and "Codex" appear only in descriptions, as products IsleBar works with — never as search terms
> (Store policy on third-party names in metadata). Add to every description's end if certification asks: "Claude and Claude Code
> are trademarks of Anthropic PBC; OpenAI and Codex are trademarks of OpenAI. IsleBar is not affiliated with either."

## Shared fields (Partner Center › Properties / Age ratings / Pricing)

| Field | Value |
|---|---|
| Category | Productivity (subcategory: none) — alternative: Utilities & tools |
| Privacy policy URL | https://ds3owl.github.io/islebar/privacy.html |
| Website | https://ds3owl.github.io/islebar/ |
| Support contact | https://github.com/ds3owl/islebar/issues |
| Copyright | Copyright © 2026 ds3owl |
| Additional license terms | Open source under the MIT License — source: https://github.com/ds3owl/islebar |
| System requirements | Windows 11 (x64) · minimum: taskbar search box shown as a box or an icon |
| Age rating (IARC questionnaire) | No violence, sexual content, gambling, profanity, user-generated content shared between users, or purchases of digital goods → expected rating **3+ / Everyone** |
| Pricing | **Free** (owner's decision 10-04) — no in-app purchases, no ads |
| Markets | All |

### Restricted capabilities — justification (Partner Center asks for these)

- **runFullTrust**: IsleBar is a desktop (Win32/WinUI 3) app. It places its search box over the taskbar's own search box and
  reads system state (media sessions, downloads, notifications the user opts into), which requires a full-trust process.
- ~~unvirtualizedResources~~: **refused by certification 10-07 and removed** from 0.1.2. The bar and the hook CLI share
  the package's AppData / HKCU view anyway; the Explorer module now lives in `%USERPROFILE%\.islebar\tap`, and the
  supervisor starts the bar so that what it launches (Claude Code, Codex, apps, links) runs outside the package.

### Uninstall note (add to the description's end or the support page)
> Before uninstalling, turn off "Claude Code / Codex connection" in IsleBar's settings — the Store gives apps no uninstall
> step, and Claude Code / Codex would otherwise keep calling IsleBar's command after it is gone.

### Notes for certification (Partner Center › Submission options)

> IsleBar replaces the look of the Windows 11 taskbar search box: it draws its own box in the same place and the real one
> keeps working underneath. No account or sign-in is needed. To test: launch IsleBar; a pill appears where the taskbar
> search box is. Click it and type a question (opens a terminal running Claude Code if it is installed; otherwise press Tab
> to search files or the web). Type "25m" and Enter to start a timer. Play music in any media app to see it on the pill.
> Right-click the pill for timers and "Close search bar". Crash reports are off unless the user turns them on in settings.
>
> Optional "Hide the real search box" (Settings, **off by default**): when the user turns it on, IsleBar uses the documented
> XAML diagnostics API (InitializeXamlDiagnosticsEx — the same approach TranslucentTB, already in the Store, uses to style the
> taskbar) to set the opacity of the taskbar's own search box to 0, so it never shows through behind IsleBar's box. Nothing
> else in Explorer is changed; the box is restored as soon as the setting is turned off or IsleBar exits (including a crash).
> If this optional feature is not acceptable, we can ship the Store package without it. Once turned on, the small helper
> stays loaded in that Explorer session (it is staged in %USERPROFILE%\.islebar\tap, a plain folder Explorer can read) and does nothing while IsleBar is not running.
>
> Other system behaviour, all for the taskbar pill: IsleBar's window is placed inside the taskbar (SetParent) next to the search
> box; it reads the taskbar's layout with UI Automation to find that spot; while an agent task has just finished it uses
> low-level keyboard/mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) only to notice *that* the user clicked or typed in that
> terminal — key values are never read or stored — and a mouse hook to close an open notice on an outside click. When the
> user clicks its "Connect" card (undone with a switch in settings) it adds its status command to Claude Code's / Codex's hook
> settings in the user profile, and marks its own working folder (%USERPROFILE%\ClaudeBar) as trusted for Claude Code. Programs
> started from the pill (the agent terminal, files, links) are launched outside the package so their own data is not
> redirected into IsleBar's container. It starts with Windows through the package's StartupTask, which the user can turn off in Settings › Apps › Startup.

---

## English (en-us)

**Product name**: IsleBar

**Short description**: Your Windows 11 taskbar, alive. See when Claude Code or Codex is working, needs you, or is done — plus music, timers, file search and downloads, right in the taskbar search box.

**Description** *(required)*:
IsleBar turns the Windows 11 taskbar search box into a live island.

Ask Claude Code or Codex straight from the taskbar, then look away: the box tells you when your AI agent is working, when it needs your answer (orange), and when it is done (green). No more checking terminals.

The same box shows what else is going on — the song that is playing with its album art, a countdown you started by typing "25m", the download you're waiting for, your headphones connecting. Press Tab to search every file on your PC instantly, or the web.

Everything happens where the search box already is. Nothing floats over your screen, and the box goes back to normal when you close IsleBar.

Open source (MIT). Light and dark mode. Ten languages. Private by default: nothing about you leaves your PC.

What you need: Windows 11. Claude Code or Codex for the AI features, Everything (voidtools) for file search. Music, timers, downloads and notices work without them.

**Features**:
- Ask Claude Code or Codex from the taskbar
- See working · needs you · done at a glance
- Music with album art and playback controls
- Timers, pomodoro and stopwatch — type "25m"
- Instant file search (with Everything) and web search
- Browser downloads while they run, and a "done" when they finish
- Headphones, charging, focus and calendar notices
- Light and dark mode, ten languages

**Search terms** (max 7): taskbar · search box · AI agent · timer · pomodoro · music · widget

**What's new**: First release.

---

## 한국어 (ko-kr)

**제품 이름**: IsleBar

**짧은 설명**: 작업표시줄이 살아납니다. Claude Code·Codex가 작업 중인지, 확인이 필요한지, 끝났는지 작업표시줄 검색창에서 바로 보세요. 음악·타이머·파일 검색·다운로드까지.

**설명**:
IsleBar는 Windows 11 작업표시줄의 검색창을 살아 있는 "섬"으로 바꿔 줍니다.

작업표시줄에서 바로 Claude Code나 Codex에게 물어보고 다른 일을 하세요. AI가 작업 중일 때, 답이 필요할 때(주황), 끝났을 때(초록)를 검색창이 알려 줍니다. 터미널을 계속 들여다볼 필요가 없습니다.

같은 자리에서 지금 재생 중인 음악과 앨범 커버, "25m"이라고 입력해 시작한 타이머, 기다리던 다운로드, 연결된 헤드폰까지 보여 줍니다. Tab을 누르면 PC의 모든 파일이나 웹을 바로 검색합니다.

모든 것이 원래 검색창 자리에서 일어납니다. 화면을 가리는 창은 없고, IsleBar를 끄면 검색창이 원래대로 돌아옵니다.

오픈소스(MIT). 라이트·다크 모드. 10개 언어. 기본적으로 개인정보는 PC 밖으로 나가지 않습니다.

필요한 것: Windows 11. AI 기능은 Claude Code 또는 Codex, 파일 검색은 Everything(voidtools)이 있어야 합니다. 음악·타이머·다운로드·알림은 없어도 됩니다.

**기능**:
- 작업표시줄에서 Claude Code·Codex에게 질문
- 작업 중 · 확인 필요 · 완료를 한눈에
- 앨범 커버와 재생 버튼이 있는 음악 표시
- 타이머·뽀모도로·스톱워치 — "25m" 입력
- 즉시 파일 검색(Everything)과 웹 검색
- 브라우저 다운로드 진행 표시와 완료 알림
- 헤드폰·충전·집중·일정 알림
- 라이트·다크 모드, 10개 언어

**검색어**: 작업표시줄 · 검색창 · AI 에이전트 · 타이머 · 뽀모도로 · 음악 · 위젯

**새 기능**: 첫 출시.

---

## 日本語 (ja-jp)

**製品名**: IsleBar

**短い説明**: タスクバーが、生きている。Claude Code や Codex が作業中か、確認待ちか、完了したかをタスクバーの検索ボックスで。音楽・タイマー・ファイル検索・ダウンロードも。

**説明**:
IsleBar は Windows 11 のタスクバーの検索ボックスを、生きた「アイランド」に変えます。

タスクバーから Claude Code や Codex に質問して、ほかの作業に戻りましょう。AI が作業中のとき、あなたの返事を待っているとき(オレンジ)、完了したとき(グリーン)を検索ボックスが知らせます。ターミナルを見張る必要はもうありません。

同じ場所に、再生中の曲とアルバムアート、「25m」と入力して始めたタイマー、待っているダウンロード、接続したヘッドホンも表示。Tab キーで PC 内のすべてのファイルや Web をすぐに検索できます。

すべては検索ボックスがあった場所で起こります。画面を覆うウィンドウはなく、IsleBar を閉じれば元の検索ボックスに戻ります。

無料のオープンソース。ライト/ダークモード。10 言語対応。プライバシー重視:あなたの情報が PC の外に出ることはありません。

必要なもの:Windows 11。AI 機能には Claude Code または Codex、ファイル検索には Everything(voidtools)。音楽・タイマー・ダウンロード・通知はなくても動作します。

**機能**:
- タスクバーから Claude Code / Codex に質問
- 作業中・確認待ち・完了がひと目でわかる
- アルバムアートと再生ボタン付きの音楽表示
- タイマー・ポモドーロ・ストップウォッチ —「25m」と入力
- 瞬時のファイル検索(Everything)と Web 検索
- ブラウザーのダウンロード中の表示と完了のお知らせ
- ヘッドホン・充電・集中・予定の通知
- ライト/ダークモード、10 言語

**検索キーワード**: タスクバー · 検索ボックス · AI エージェント · タイマー · ポモドーロ · 音楽 · ウィジェット

**新機能**: 初回リリース。

---

## 简体中文 (zh-cn)

**产品名称**: IsleBar

**简短描述**: 让任务栏活起来。在任务栏搜索框里直接看到 Claude Code 或 Codex 是在工作、等你确认还是已完成——还有音乐、计时器、文件搜索和下载。

**描述**:
IsleBar 把 Windows 11 任务栏的搜索框变成一座会动的"小岛"。

直接在任务栏向 Claude Code 或 Codex 提问,然后去做别的事。AI 正在工作、需要你回答(橙色)、已经完成(绿色)时,搜索框都会告诉你。不必再盯着终端。

同一个位置还会显示正在播放的歌曲和专辑封面、输入"25m"启动的计时器、正在下载的文件、刚连上的耳机。按 Tab 即可瞬间搜索电脑上的所有文件或网页。

一切都发生在原来搜索框的位置,不会有窗口挡住屏幕;关闭 IsleBar 后搜索框恢复原样。

免费开源。浅色与深色模式。10 种语言。默认保护隐私:你的信息不会离开电脑。

需要:Windows 11。AI 功能需要 Claude Code 或 Codex,文件搜索需要 Everything(voidtools)。音乐、计时器、下载和通知无需它们。

**功能**:
- 在任务栏向 Claude Code / Codex 提问
- 工作中 · 待确认 · 完成,一目了然
- 带专辑封面和播放按钮的音乐显示
- 计时器、番茄钟、秒表——输入"25m"
- 即时文件搜索(Everything)与网页搜索
- 浏览器下载进行中显示与完成提示
- 耳机、充电、专注和日程通知
- 浅色/深色模式,10 种语言

**搜索词**: 任务栏 · 搜索框 · AI 助手 · 计时器 · 番茄钟 · 音乐 · 小组件

**新增内容**: 首次发布。

---

## 繁體中文 (zh-tw)

**產品名稱**: IsleBar

**簡短描述**: 讓工作列活起來。在工作列搜尋框直接看到 Claude Code 或 Codex 是在工作、等你確認還是已完成——還有音樂、計時器、檔案搜尋與下載。

**描述**:
IsleBar 把 Windows 11 工作列的搜尋框變成一座會動的「小島」。

直接在工作列向 Claude Code 或 Codex 提問,然後去做別的事。AI 正在工作、需要你回覆(橘色)、已經完成(綠色)時,搜尋框都會告訴你。不必再盯著終端機。

同一個位置也會顯示正在播放的歌曲與專輯封面、輸入「25m」啟動的計時器、正在下載的檔案、剛連上的耳機。按 Tab 即可立即搜尋電腦上的所有檔案或網頁。

一切都發生在原本搜尋框的位置,不會有視窗擋住畫面;關閉 IsleBar 後搜尋框恢復原狀。

免費開源。淺色與深色模式。10 種語言。預設保護隱私:你的資訊不會離開電腦。

需要:Windows 11。AI 功能需要 Claude Code 或 Codex,檔案搜尋需要 Everything(voidtools)。音樂、計時器、下載與通知不需要它們。

**功能**:
- 在工作列向 Claude Code / Codex 提問
- 工作中 · 待確認 · 完成,一目了然
- 附專輯封面與播放按鈕的音樂顯示
- 計時器、番茄鐘、碼錶——輸入「25m」
- 即時檔案搜尋(Everything)與網頁搜尋
- 瀏覽器下載進行中顯示與完成提示
- 耳機、充電、專注與行程通知
- 淺色/深色模式,10 種語言

**搜尋詞**: 工作列 · 搜尋框 · AI 助理 · 計時器 · 番茄鐘 · 音樂 · 小工具

**新功能**: 首次發行。

---

## Français (fr-fr)

**Nom du produit**: IsleBar

**Description courte**: Votre barre des tâches, vivante. Voyez si Claude Code ou Codex travaille, vous attend ou a terminé — et aussi la musique, les minuteurs, la recherche de fichiers et les téléchargements, dans la zone de recherche.

**Description**:
IsleBar transforme la zone de recherche de la barre des tâches de Windows 11 en une « île » vivante.

Posez votre question à Claude Code ou Codex depuis la barre des tâches, puis passez à autre chose : la zone vous indique quand votre agent IA travaille, quand il attend votre réponse (orange) et quand il a terminé (vert). Plus besoin de surveiller le terminal.

Au même endroit : la chanson en cours avec sa pochette, le minuteur lancé en tapant « 25m », le téléchargement que vous attendez, vos écouteurs qui se connectent. Appuyez sur Tab pour chercher instantanément tous les fichiers de votre PC, ou le Web.

Tout se passe là où se trouvait déjà la zone de recherche. Aucune fenêtre ne couvre l'écran, et tout redevient normal quand vous fermez IsleBar.

Gratuit et open source. Modes clair et sombre. Dix langues. Confidentiel par défaut : rien sur vous ne quitte votre PC.

Prérequis : Windows 11. Claude Code ou Codex pour les fonctions IA, Everything (voidtools) pour la recherche de fichiers. Musique, minuteurs, téléchargements et notifications fonctionnent sans eux.

**Fonctionnalités**:
- Interrogez Claude Code ou Codex depuis la barre des tâches
- En cours · attend · terminé, en un coup d'œil
- Musique avec pochette et commandes de lecture
- Minuteurs, pomodoro et chronomètre — tapez « 25m »
- Recherche de fichiers instantanée (Everything) et recherche Web
- Téléchargements du navigateur en cours, puis « terminé » à la fin
- Notifications d'écouteurs, de charge, de concentration et d'agenda
- Modes clair et sombre, dix langues

**Termes de recherche**: barre des tâches · recherche · agent IA · minuteur · pomodoro · musique · widget

**Nouveautés**: Première version.

---

## Deutsch (de-de)

**Produktname**: IsleBar

**Kurzbeschreibung**: Deine Taskleiste, lebendig. Sieh direkt im Suchfeld, ob Claude Code oder Codex arbeitet, dich braucht oder fertig ist – dazu Musik, Timer, Dateisuche und Downloads.

**Beschreibung**:
IsleBar verwandelt das Suchfeld der Windows-11-Taskleiste in eine lebendige „Insel".

Frag Claude Code oder Codex direkt aus der Taskleiste und mach etwas anderes: Das Feld zeigt dir, wann dein KI-Agent arbeitet, wann er deine Antwort braucht (orange) und wann er fertig ist (grün). Kein ständiger Blick ins Terminal mehr.

An derselben Stelle: der laufende Song mit Cover, der Timer, den du mit „25m" gestartet hast, der Download, auf den du wartest, deine Kopfhörer beim Verbinden. Mit Tab durchsuchst du sofort alle Dateien auf deinem PC – oder das Web.

Alles passiert dort, wo das Suchfeld schon war. Nichts überdeckt den Bildschirm, und beim Schließen von IsleBar ist alles wie vorher.

Kostenlos und Open Source. Heller und dunkler Modus. Zehn Sprachen. Standardmäßig privat: Nichts über dich verlässt deinen PC.

Voraussetzungen: Windows 11. Claude Code oder Codex für die KI-Funktionen, Everything (voidtools) für die Dateisuche. Musik, Timer, Downloads und Hinweise funktionieren auch ohne.

**Funktionen**:
- Claude Code oder Codex direkt aus der Taskleiste fragen
- Arbeitet · wartet · fertig auf einen Blick
- Musik mit Cover und Wiedergabesteuerung
- Timer, Pomodoro und Stoppuhr – „25m" eintippen
- Sofortige Dateisuche (Everything) und Websuche
- Browser-Downloads während sie laufen, und „fertig“, wenn sie abgeschlossen sind
- Hinweise zu Kopfhörern, Laden, Fokus und Kalender
- Heller und dunkler Modus, zehn Sprachen

**Suchbegriffe**: Taskleiste · Suchfeld · KI-Agent · Timer · Pomodoro · Musik · Widget

**Neuigkeiten**: Erste Version.

---

## Italiano (it-it)

**Nome del prodotto**: IsleBar

**Descrizione breve**: La tua barra delle applicazioni, viva. Vedi se Claude Code o Codex sta lavorando, ti aspetta o ha finito — oltre a musica, timer, ricerca file e download, nella casella di ricerca.

**Descrizione**:
IsleBar trasforma la casella di ricerca della barra delle applicazioni di Windows 11 in un'«isola» viva.

Chiedi a Claude Code o Codex direttamente dalla barra delle applicazioni e passa ad altro: la casella ti dice quando il tuo agente IA sta lavorando, quando aspetta la tua risposta (arancione) e quando ha finito (verde). Niente più occhi fissi sul terminale.

Nello stesso punto: il brano in riproduzione con la copertina, il timer avviato scrivendo «25m», il download che aspetti, le cuffie che si collegano. Premi Tab per cercare subito tutti i file del PC, o il Web.

Tutto accade dove c'era già la casella di ricerca. Nessuna finestra copre lo schermo, e chiudendo IsleBar tutto torna com'era.

Gratuito e open source. Modalità chiara e scura. Dieci lingue. Privato per impostazione predefinita: nulla di tuo lascia il PC.

Requisiti: Windows 11. Claude Code o Codex per le funzioni IA, Everything (voidtools) per la ricerca dei file. Musica, timer, download e notifiche funzionano anche senza.

**Funzionalità**:
- Chiedi a Claude Code o Codex dalla barra delle applicazioni
- In corso · in attesa · fatto a colpo d'occhio
- Musica con copertina e controlli di riproduzione
- Timer, pomodoro e cronometro — scrivi «25m»
- Ricerca file istantanea (Everything) e ricerca Web
- Download del browser in corso e avviso di completamento
- Notifiche di cuffie, ricarica, concentrazione e calendario
- Modalità chiara e scura, dieci lingue

**Termini di ricerca**: barra applicazioni · ricerca · agente IA · timer · pomodoro · musica · widget

**Novità**: Prima versione.

---

## Português (pt-br)

**Nome do produto**: IsleBar

**Descrição curta**: Sua barra de tarefas, viva. Veja se o Claude Code ou o Codex está trabalhando, precisa de você ou terminou — além de música, timers, busca de arquivos e downloads, na caixa de pesquisa.

**Descrição**:
O IsleBar transforma a caixa de pesquisa da barra de tarefas do Windows 11 em uma "ilha" viva.

Pergunte ao Claude Code ou ao Codex direto da barra de tarefas e vá fazer outra coisa: a caixa avisa quando seu agente de IA está trabalhando, quando precisa da sua resposta (laranja) e quando terminou (verde). Nada de ficar olhando o terminal.

No mesmo lugar: a música tocando com a capa do álbum, o timer que você iniciou digitando "25m", o download que você está esperando, seus fones conectando. Pressione Tab para pesquisar na hora todos os arquivos do PC, ou a web.

Tudo acontece onde a caixa de pesquisa já estava. Nenhuma janela cobre a tela, e tudo volta ao normal quando você fecha o IsleBar.

Grátis e de código aberto. Modos claro e escuro. Dez idiomas. Privado por padrão: nada sobre você sai do seu PC.

Requisitos: Windows 11. Claude Code ou Codex para os recursos de IA, Everything (voidtools) para a busca de arquivos. Música, timers, downloads e avisos funcionam sem eles.

**Recursos**:
- Pergunte ao Claude Code ou ao Codex pela barra de tarefas
- Trabalhando · aguardando · pronto de relance
- Música com capa e controles de reprodução
- Timers, pomodoro e cronômetro — digite "25m"
- Busca instantânea de arquivos (Everything) e na web
- Downloads do navegador em andamento e aviso de concluído
- Avisos de fones, carregamento, foco e agenda
- Modos claro e escuro, dez idiomas

**Termos de pesquisa**: barra de tarefas · pesquisa · agente de IA · timer · pomodoro · música · widget

**Novidades**: Primeira versão.

---

## Español (es-es)

**Nombre del producto**: IsleBar

**Descripción breve**: Tu barra de tareas, viva. Mira si Claude Code o Codex está trabajando, te necesita o ha terminado — además de música, temporizadores, búsqueda de archivos y descargas, en el cuadro de búsqueda.

**Descripción**:
IsleBar convierte el cuadro de búsqueda de la barra de tareas de Windows 11 en una «isla» viva.

Pregunta a Claude Code o Codex directamente desde la barra de tareas y dedícate a otra cosa: el cuadro te avisa cuando tu agente de IA está trabajando, cuando necesita tu respuesta (naranja) y cuando ha terminado (verde). Se acabó vigilar la terminal.

En el mismo sitio: la canción que suena con su portada, el temporizador que iniciaste escribiendo «25m», la descarga que esperas, tus auriculares al conectarse. Pulsa Tab para buscar al instante todos los archivos de tu PC, o la web.

Todo ocurre donde ya estaba el cuadro de búsqueda. Ninguna ventana tapa la pantalla, y al cerrar IsleBar todo vuelve a ser como antes.

Gratis y de código abierto. Modos claro y oscuro. Diez idiomas. Privado por defecto: nada sobre ti sale de tu PC.

Requisitos: Windows 11. Claude Code o Codex para las funciones de IA, Everything (voidtools) para buscar archivos. La música, los temporizadores, las descargas y los avisos funcionan sin ellos.

**Características**:
- Pregunta a Claude Code o Codex desde la barra de tareas
- Trabajando · esperando · listo de un vistazo
- Música con portada y controles de reproducción
- Temporizadores, pomodoro y cronómetro — escribe «25m»
- Búsqueda instantánea de archivos (Everything) y de la web
- Descargas del navegador en curso y aviso al terminar
- Avisos de auriculares, carga, concentración y calendario
- Modos claro y oscuro, diez idiomas

**Términos de búsqueda**: barra de tareas · búsqueda · agente de IA · temporizador · pomodoro · música · widget

**Novedades**: Primera versión.
