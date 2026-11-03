# Trabalhando neste repositório

Leia isto antes de mudar qualquer coisa. Foi escrito para um agente de código que pega o
projeto do zero, e é basicamente uma lista de coisas que parecem erradas mas não são, e de
coisas que parecem certas e vão te morder.

---

## O que é isto

Ditado *push-to-talk*. Segure uma tecla, fale, solte, e o texto limpo é digitado onde estiver
o foco. Duas implementações independentes:

| | macOS | Windows |
|---|---|---|
| Linguagem | Swift 6 | C# / .NET 10 |
| UI | SwiftUI | Avalonia |
| Fala | Apple `SpeechAnalyzer`, ou Parakeet via FluidAudio | Parakeet via sherpa-onnx |
| Local | raiz do repositório | `windows/` |

**O app Windows é o foco do projeto e roda em hardware real** desde 2026-11-01 (Windows 11,
microfone USB, português com Parakeet v3): ditado no Bloco de Notas e no WhatsApp funcionou de
ponta a ponta. O CI compila, roda os testes, publica um executável single-file, executa-o no
Windows e confirma que a camada de plataforma carrega.

**O app macOS** funciona, mas não é mais o foco; mudanças nele não são compiladas localmente
(não há Mac no fluxo de trabalho atual) — só o alvo do dicionário roda no CI.

---

## A única regra que importa

**`shared/dictionary-test-vectors.json` é a especificação do comportamento das correções.**

As duas implementações rodam esse arquivo no CI. Se você mudar como as correções funcionam,
mude os vetores primeiro, veja os dois lados ficarem vermelhos e depois deixe-os verdes. Mudar
uma implementação para "consertar" um vetor sem mudar a outra é como as duas divergem em
silêncio.

```bash
swift test --filter VectorTests                         # lado macOS
cd windows && dotnet test TalkToText.CrossPlatform.slnf # lado Windows, roda em qualquer SO
```

O arquivo em `Tests/TalkToTextDictionaryTests/dictionary-test-vectors.json` é uma cópia, e o CI
falha se ela se afastar de `shared/`. Depois de editar o arquivo compartilhado:

```bash
cp shared/dictionary-test-vectors.json Tests/TalkToTextDictionaryTests/
```

---

## Coisas que parecem bugs e não são

**`dotnet build TalkToText.sln` falha fora do Windows** com `NETSDK1073`. Esperado —
`TalkToText.Platform.Windows` tem como alvo `net10.0-windows`. Use
`TalkToText.CrossPlatform.slnf`, que o omite; todo o resto, inclusive a suíte de UI inteira,
compila e testa em cerca de meio segundo.

**`swift build` falha com "input file was modified during the build."** Acontece quando o
repositório está numa pasta sincronizada pelo iCloud: o sincronizador mexe nos arquivos no meio
da compilação. **Sempre compile com `make`**, que usa `--scratch-path` fora da árvore
sincronizada. Um `swift build` puro também grava um `.build/` no iCloud, o que deixa as
compilações seguintes minutos mais lentas. Se aparecer esse erro, espere alguns segundos e
tente de novo.

**O modo de comparação (macOS) não digita nada.** De propósito — `Settings.compareMode` roda
todos os motores na mesma gravação e mostra os resultados lado a lado. Se os dois injetassem,
duas transcrições brigariam por um campo de texto. É o comportamento mais confuso do app.

**A coluna de tempo não compara coisas equivalentes.** Apple e Parakeet são cronometrados em
processamento local, com o relógio iniciado *depois* de carregar o modelo. O número do Wispr
Flow é o `e2eLatency` dele, que inclui ida e volta pela rede e a etapa de limpeza. Não os
apresente como um ranking único.

**`MainActor.assumeIsolated` derruba o processo.** Ele não verifica a afirmação, ele a impõe.
Use `await MainActor.run` em qualquer contexto fora do main actor. Isso já derrubou o app uma
vez.

**Mudar `@State` dentro do closure de desenho de um `Canvas` inunda o log e corrompe o
estado.** O medidor VU guarda a física do ponteiro num tipo de referência simples que a view só
segura, invisível ao grafo de estado do SwiftUI. Não "limpe" isso para `@State`.

---

## Design system (macOS)

`Sources/TalkToText/UI/DesignSystem.swift` define todos os tokens de cor, tamanho, raio,
duração e material. **Views não podem conter valores literais.** Se um componente precisa de um
número que não é token, crie o token em vez de escrever o valor direto.

A direção é a dos gravadores de campo dos anos 1980 — Sony TC-D5, Marantz PMD, Nakamichi,
Braun. Face prateada no modo claro, preta no escuro. Duas regras inegociáveis:

- **Vermelho significa gravando.** Nada mais no app é vermelho.
- **Âmbar e verde são só instrumentação** — medidores de nível, nunca elementos de interface.

Explicitamente descartados: neon, vaporwave, synthwave, gradientes roxo/rosa, texto brilhante,
letras cromadas, horizontes de grade. **Não há gradientes em lugar nenhum**; a profundidade vem
de painéis chapados, chanfros finos e textura escovada desenhada proceduralmente.

---

## Particularidades do macOS

**A assinatura de código é estrutural, não cosmética.** O TCC guarda um *requisito* de
assinatura por entrada, não só um caminho. Uma assinatura ad-hoc muda a cada build, então o
binário recompilado deixa de satisfazer o requisito guardado — e o sintoma engana: a chave de
Acessibilidade continua **ligada** enquanto o app é tratado como não confiável. O `Makefile`
detecta um Developer ID via `security find-identity`. Não troque isso por `--sign -`.

Se uma permissão travar, redefina só aquela linha — nunca alterne a chave, e nunca omita o
bundle ID:

```bash
tccutil reset Accessibility com.rafaelcelso.talktotext
```

Um `tccutil reset Accessibility` sem bundle ID apaga a permissão de todos os apps da máquina.
Depois feche os Ajustes do Sistema por completo (⌘Q) antes de reabrir; o painel de Privacidade
guarda a lista em cache.

**`log` pode estar sombreado no shell do usuário.** Use `/usr/bin/log` explicitamente.

**Não rode o `.app` de dentro da pasta do repositório** se ela for sincronizada pelo iCloud: o
sincronizador pode corromper a assinatura. `make install` coloca a cópia em uso em
`/Applications`.

---

## Particularidades do Windows

As particularidades abaixo custaram caro para descobrir, e várias foram aprendidas do jeito
difícil. Trate-as como estruturais. Detalhes completos em `windows/README.md` e
`docs/PARAKEET-WINDOWS.md`.

**Três versões fixadas que quebram em silêncio na "latest":**

| Pacote | Versão | Por quê |
|---|---|---|
| `NAudio` | 2.3.0 | 3.x exige .NET 9+ e não restaura |
| `Avalonia.Headless.XUnit` | 11.3.20 | 12.x exige xUnit **v3**, outra linha de pacotes |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Já inclui o ONNX Runtime — nunca referencie também `Microsoft.ML.OnnxRuntime` |

**Alt direito é AltGr** nos layouts alemão, polonês, britânico, nórdicos e na maioria dos
latino-americanos. Usar essa tecla para o ditado — e principalmente suprimi-la — impede esses
usuários de digitar `@`, `€`, `\`, `|`. O padrão é **Ctrl + Win** (uma ou duas teclas,
configurável), e o hook **observa sem engolir**: se o key-down for engolido e o key-up escapar,
o app de destino acha que o Ctrl ficou pressionado para sempre. O hook só reporta transições
brutas de tecla; qual combinação conta, segurar vs. alternar e "uma terceira tecla cancela"
ficam em `TalkToText.Core/Shortcut.cs`, onde são testados. As liberações de Win/Alt são
mascaradas com um `vkE8` injetado para o menu Iniciar não abrir.

**Nomes:** projetos `TalkToText.*`, executável `TalkToText.exe`, dados em
`%LOCALAPPDATA%\TalkToText`. A UI segue o tema escuro do OpenWhispr (MIT)
(`TalkToText.App/Design/DesignTokens.cs`), não o design system de gravador de campo do macOS — e
toda superfície é **vidro**: fosco branco translúcido (`GlassFill`, `GlassFillRaised`) com uma
borda iluminada no topo (`GlassRim`), sobre uma janela com desfoque acrílico. Use esses tokens,
não os cinzas antigos (`Raised`, `Card`), em qualquer card, linha ou botão novo. Os raios
continuam os do OpenWhispr (um teste os fixa).

**`SettingsData` usa setters, não `init`.** O serializador JSON gerado por código grava
`default` em toda propriedade `init` ausente, e um arquivo de configurações antigo chegou a
carregar sem atalho nenhum.

**A pílula de gravação nunca pode pegar o foco** — a transcrição é colada em quem tem o foco.
Ela é exibida com `ShowActivated = false` e marcada com `WS_EX_NOACTIVATE`.

**Transcrição de reuniões** (`TalkToText.Core/MeetingSession.cs`): o microfone é o usuário, o
loopback WASAPI (`SpeakerAudioCapture`) é todo o resto. As falas remotas são agrupadas por
impressões de voz do WeSpeaker (limiar 0,65 — medido: mesma voz 0,86–0,94, vozes diferentes
0,50–0,55; o 3D-Speaker CAM++ foi inutilizável nos mesmos trechos). Uma reunião é um app de
reunião **com o microfone aberto**, lido das **sessões de captura WASAPI ativas**. Não dos
valores `LastUsedTime` do `CapabilityAccessManager` no registro: no Windows 11 build 26300 eles
pararam de atualizar, e uma chamada do Teams em andamento passou despercebida. O registro é só
um plano B. Ler quem está falando no Teams via UI Automation é experimental e não foi conferido
numa chamada real.

**O loopback dos alto-falantes passa por `BufferedWaveProvider`, que precisa de
`ReadFully = false`.** Por padrão ele preenche o buffer vazio com silêncio em vez de devolver 0,
e o laço de drenagem em `WasapiAudioCapture.OnDataAvailable` nunca terminava: as outras pessoas
da chamada nunca eram transcritas (o áudio delas era empurrado por zeros) e a captura não
parava. O microfone recebe 16 kHz do SO e nunca passa por esse caminho, por isso só um lado
quebrava. `MeetingSession` verifica o token de parada a cada bloco e descarta os VADs sob um
lock, porque uma captura atrasada já chamou um VAD do sherpa-onnx descartado e derrubou o app.

**Parar uma reunião nunca pode esperar para sempre.** `MeetingSession.StopAsync` registra e
salva `EndedAt` antes de qualquer outra coisa, e dá 5 s para as fontes pararem: uma chamada do
Teams já terminou sem o stop retornar (provavelmente uma chamada de UI Automation na janela que
fechava), e a reunião continuou como ao vivo, com a duração contando. Reuniões interrompidas
porque o app foi encerrado são fechadas na inicialização por `MeetingStore.CloseUnfinished`, na
última mensagem.

**Arquivos de áudio** (página "Transcrever áudio", `AudioFilesView` +
`Core/AudioFileTranscriber.cs`): o arquivo é decodificado por
`Platform.Windows/AudioFileDecoder.cs` (NAudio `AudioFileReader`: WAV, MP3, e M4A/AAC/WMA via
Media Foundation) para 16 kHz mono, um segundo por vez, depois cortado pelo Silero VAD, agrupado
por impressões de voz do WeSpeaker e transcrito como uma reunião. Os resultados são
`MeetingTranscript`s com `SourceFile` preenchido, num repositório próprio (`audio-files`), e
reutilizam o `ChatView`. Anexar nunca inicia trabalho: Transcrever e "Resumir com IA" são botões
separados, ambos interrompíveis; uma transcrição interrompida é salva como "(parcial)". A
"Instrução para a IA" das Configurações (`SettingsData.AiInstruction`) entra no prompt de
sistema do resumo só para arquivos de áudio; ela não muda o que o Parakeet ouve — isso é papel
do Dicionário. A transcrição guarda o caminho completo do arquivo (`SourceFile`; as antigas só o
nome, daí o "Localizar arquivo") e o toca com `AudioPlayerBar` →
`Platform.Windows/AudioFilePlayer.cs` (NAudio `WaveOutEvent`, mono). A velocidade mantém o tom
via `Abstractions/TimeStretcher.cs` (WSOLA), que fica fora da camada Windows para ter testes
unitários; reamostragem simples transformaria fala em 2× em voz de esquilo. A barra de busca é
`Controls/PlaybackTimeline.cs`: o volume da gravação (`Core/AudioEnvelope.cs`, calculado em
segundo plano) desenhado com as barras e o brilho da pílula de gravação, animado por um timer
de quadros durante a reprodução. A velocidade fica atrás de um menu de engrenagem. **A
reprodução pertence ao app, não a uma página**: `PlaybackHub` (do `MainWindow`) guarda o único
arquivo tocando; cada `AudioPlayerBar` pede o player a ele a cada quadro e não é dono de nada,
então sair da transcrição não para o áudio (uma barra que descartava o player já derrubou o app
quando a página voltou). Enquanto há áudio ativo e a barra da transcrição está fora da tela,
o `MiniPlayer` flutua no canto inferior direito da janela; com a janela minimizada ou na
bandeja, `PlayerPillWindow` o mostra no centro inferior (acima da pílula de gravação quando as
duas aparecem). A janela da pílula é criada na inicialização — um teste monta os dois layouts,
porque um bug de layout ali já impediu o app de abrir. Durante a reprodução,
`ChatView.FollowPlayback` destaca a mensagem sendo ouvida e rola até ela; os balões são
rastreados por índice porque salvar um resumo recarrega a transcrição como objetos novos. A
seção "Dados citados" do resumo não é escrita pelo modelo (ele insistia em responder
"nenhum"): o app anexa cada linha com número, data ou prazo, citada literalmente com o horário.
Capturas de tela em scripts de teste usam `PrintWindow`, nunca cópia da tela: as janelas do
próprio usuário podem estar por cima. O player para quando sai da tela, então a página não pode
reconstruir a transcrição aberta a cada salvamento.

**Janela de reunião ao vivo**: enquanto uma reunião é transcrita e a janela principal está
minimizada ou na bandeja, `LiveTranscriptWindow` mostra o `ChatView` ao vivo no canto inferior
direito (sem foco, como as pílulas); ela recolhe para o ícone do app com um ponto vermelho.
Ligada por padrão, configurável nas Configurações (`SettingsData.ShowLiveTranscriptWindow`).
Consultada pelo `App` como a pílula do player.

**Página de Chat** (`ChatPage` + `Core/TranscriptAssistant.cs`): perguntas sobre reuniões e
arquivos de áudio, respondidas pelo mesmo modelo Qwen local. As transcrições são cortadas em
trechos (resumo primeiro, depois 12 mensagens cada) e escolhidas por palavras em comum — um nome
ou título na pergunta restringe a essas reuniões, "última" à mais recente do que sobrou — com
limite de ~2.800 tokens: um contexto completo de 8k levava três minutos na CPU, o limite traz
para menos de um minuto. O modelo é carregado por pergunta e liberado, e recusa enquanto um
resumo está sendo escrito (dois exigiriam ~5 GB). As conversas são salvas por aba
(`Core/ChatStore.cs`, pasta `chats`) e listadas ao lado do chat com a reunião ou gravação a que
se referem; repreencher o seletor não pode contar como o usuário escolhendo outro escopo
(`_opening`), senão voltar à página descartaria a conversa aberta.

**Busca e exportação** (`Core/TranscriptSearch.cs`, `Core/TranscriptExport.cs`): as listas de
Reuniões e de arquivos de áudio filtram enquanto você digita — toda palavra precisa aparecer em
algum lugar, ignorando acentos e maiúsculas — e mostram a linha onde foi dita. "Exportar" numa
transcrição gera Word (Office Open XML montado à mão, sem biblioteca; conferido abrindo no
Word), Markdown ou texto, conforme o tipo escolhido no diálogo de salvar. **As impressões de voz
ficam guardadas com cada participante** (`MeetingSpeaker.Voice`/`VoiceCount`, em reuniões ao
vivo e arquivos de áudio), então nomear alguém depois da reunião ainda permite "Lembrar esta
voz"; transcrições anteriores a isso não têm e a opção fica oculta para elas.

**A exportação em PDF se parece com o app** (`Views/TranscriptPdf.cs`): desenhada com SkiaSharp
no backend PDF do Skia — o fundo escuro, a fonte Inter carregada de
`avares://Avalonia.Fonts.Inter`, o card de resumo de vidro com tópicos em cores do espectro,
balões de chat com avatares — para o texto continuar selecionável. Pixels do app × 0,75 =
pontos. Os blocos (cabeçalho, card de resumo, balão) são montados primeiro e colocados inteiros
em páginas A4; um resumo ou mensagem maior que uma página continua em outro card ou balão. No
SkiaSharp 2.88 o `SKFont.MeasureText` recebe ids de glifo, não texto: meça por um `SKPaint`.
Confira um build publicado com `TalkToText.exe --export-pdf <caminho>` (reunião mais recente) e
veja as páginas com `artifacts/pdf-para-png.ps1`, que usa o renderizador de PDF do próprio
Windows. O diálogo de salvar do Windows 11 não expõe a caixa de nome do arquivo à UI Automation
pelo id clássico 1001, então scripts de teste não conseguem preenchê-la.

**O Chat é uma conversa** (`TranscriptAssistant.IsSmallTalk`): cumprimentos, agradecimentos e
perguntas sobre o assistente são respondidos sem ler transcrições; uma pergunta que não
compartilha nenhuma palavra com nenhuma reunião não recebe trechos (antes era completada com
trechos sem relação); uma continuação curta é buscada junto com a pergunta anterior. O Chat
pega emprestado o modelo já carregado do ditado (`LocalAiRewriter.AcquireAsync`), então não há
espera de carregamento entre mensagens nem duas cópias.

**Limpeza do ditado e modo comando.** `Core/SpeechCleanup.cs` tira "éé", "hã", "hum" e uma
palavra curta dita duas vezes, por regra e na hora (ligado por padrão) — nunca um "é" sozinho,
que é o verbo. "Revisar com IA" (`PolishWithAi`, desligado por padrão) passa cada ditado pelo
`ITextRewriter` (`App/LocalAiRewriter.cs`, o mesmo Qwen, mantido carregado 5 minutos após o
uso): ~5 s com o modelo carregado, ~12 s a frio no Ryzen 4600H; se for lento demais, falhar, ou
devolver algo muito mais curto ou longo do que foi dito, o ditado é digitado como estava. **O
modo comando** (desligado por padrão, Shift + Win) é um segundo `ShortcutDetector` no
`DictationEngine`: o que é dito é uma instrução, o `ISelectionReader`
(`Platform.Windows/ClipboardSelectionReader.cs`) copia a seleção com um Ctrl+C sintético
*depois que os modificadores do usuário foram soltos* (Ctrl+C com Shift pressionado é
Ctrl+Shift+C) e restaura a área de transferência, e o resultado da IA é colado sobre a seleção;
sem nada selecionado — detectado pelo número de sequência da área de transferência, não pelo
conteúdo — ele escreve o que foi pedido. Teste as partes à mão com `TalkToText.exe --try-ai` e
`--read-selection`. A configuração inicial tem uma etapa de microfone que mostra o nível e o que
foi ouvido, usando o atalho real.

**Configuração inicial** (`Views/OnboardingWindow.cs`): mostrada sobre a janela principal uma
vez, enquanto `SettingsData.OnboardingDone` for falso — numa instalação nova e, uma vez, em
arquivos de configuração anteriores a ela. Uma boas-vindas e depois cinco etapas, todas
puláveis: nome, segurar/alternar, atalho (o mesmo `ShortcutPicker` das Configurações), o
download da IA local (~2 GB, continua se o usuário seguir em frente) e a instrução para a IA.
Cada "Continuar" salva sua etapa na hora; terminar, pular tudo ou fechar a janela a marca como
concluída. Configurações → "Configuração inicial" a abre de novo.

**Resumos de reunião são só sob demanda** — o usuário pediu um botão "Gerar resumo", não um
resumo automático; não faça isso rodar depois de toda reunião. Eles vêm de um modelo local, sem
chave de API: Qwen2.5-3B-Instruct Q4_K_M (2,1 GB, baixado no primeiro resumo para
`%LOCALAPPDATA%\TalkToText\models\summary`) pelo backend de CPU do LLamaSharp, cerca de 6
tokens/s. Reuniões longas são resumidas em map-reduce (`TalkToText.Core/MeetingSummarizer.cs`).
**As DLLs nativas do llama.cpp precisam ficar soltas ao lado do exe** em
`runtimes\win-x64\native\<avx|avx2|avx512|noavx>\`: o LLamaSharp escolhe a variante de CPU pelo
caminho, o que não consegue fazer dentro do pacote single-file. O `TalkToText.App.csproj` as
mantém fora do pacote e as copia direto do pacote NuGet depois do publish (marcar os itens de
publish com `ExcludeFromSingleFile` as deixava em lugar nenhum), e descarta as cópias dos outros
SOs (73 MB). O instalador leva essa pasta; o self-test confere que ela carrega. **A IA roda na
placa de vídeo via Vulkan** (`LLamaSharp.Backend.Vulkan.Windows`, pasta
`runtimes\win-x64\native\vulkan`), escolhida uma vez na inicialização por
`SummaryModel.ConfigureBackend` a partir de `SettingsData.UseGpuForAi` (ligado por padrão); sem
driver Vulkan o LLamaSharp volta para o build de CPU, e um modelo que não cabe na placa é
recarregado na CPU. **O `ggml.dll` do pacote importa um `ggml-cpu.dll` que não vem junto** —
sem ele o Vulkan nunca carrega e nada avisa — então o publish copia o de AVX2, e o Vulkan só é
pedido em CPUs com AVX2. Medido com `TalkToText.exe --benchmark-ai cpu|gpu [batch]` num Ryzen 5
4600H (Radeon integrada), um prompt de Chat de 2.000 tokens: CPU 54 s (42 tokens/s lidos, 8,8
escritos), GPU 35 s (69 lidos, 9,8 escritos). Tamanho de batch entre 128 e 1024 não fez
diferença. Uma GPU integrada divide a memória do sistema, então a escrita quase não melhora;
uma placa dedicada ganharia muito mais.

**Nunca faça trabalho lento na thread do hook de teclado**, e nunca chame `SendInput` de dentro
dele. O Windows remove em silêncio um hook cujo callback demora ~1 s; a máscara do menu Iniciar
e a abertura do microfone são despachadas para o thread pool por isso.

**Tipos de plataforma criados por reflexão precisam de construtores públicos sem
ambiguidade.** Um `(bool)` público ao lado de um `(string?)` público fazia
`Activator.CreateInstance(type, [null])` lançar exceção; o self-test publicado pegou isso. Daí
`SpeakerAudioCapture` em vez de uma flag.

**Ícones precisam de entradas DIB abaixo de 256 px.** O leitor de ícones Win32 do Avalonia
falha com `.ico` só de PNG, e a janela principal então lança exceção no `Show()`.

**UI Automation não consegue injetar texto.** `TextPattern` é documentado como somente leitura
e `ValuePattern` substitui o campo inteiro em vez de inserir no cursor. `SendInput` é o caminho
principal, não um plano B.

**`TalkToText.App` carrega a camada de plataforma por reflexão, não por referência.** Uma
referência direta forçaria a UI para `net10.0-windows` e você perderia a capacidade de rodá-la
fora do Windows. Duas consequências que já morderam uma vez: o assembly fica invisível ao
`PublishSingleFile`, então é publicado como arquivo solto ao lado do exe *e* resolvido por um
handler explícito de `AssemblyLoadContext`; e o self-test publicado verifica isso, porque quando
quebra o app abre perfeitamente e depois não faz nada quando a tecla é pressionada.

**Mantenha `TalkToText.Platform.Windows` sem lógica.** Tudo que vive ali é código que o CI não
consegue exercitar. Novas tentativas, debounce e tratamento de troca de dispositivo pertencem
aos projetos neutros de plataforma, atrás de uma interface — esses têm alvo `net10.0` puro,
então o `CA1416` transforma qualquer chamada Win32 acidental em erro de compilação.

**Avisos são erros e os analisadores são rígidos de propósito.** `--no-incremental` é
obrigatório no CI: o Roslyn não reemite avisos de analisador num build em cache, então sem ele a
verificação não prova nada.

---

## Regex, se você mexer no dicionário

Os dois motores não são idênticos. Medido em 30 casos, **9 divergiram**. Dois afetam este código
e estão tratados — não remova nenhum:

- `RegexOptions.CultureInvariant` no lado C#, senão o `İ` turco casa com `i`.
- **Normalização NFC nos dois lados.** O macOS devolve strings decompostas, então sem ela um
  gatilho acentuado nunca dispara, em silêncio.

Mais dois não têm conserto e são simplesmente evitados: o ICU converte `ß` em `ss` e o .NET não;
o `.` do .NET divide pares substitutos. Fique dentro do subconjunto seguro — `\b`, `\d`, `\w`,
`\s`, classes de caracteres, quantificadores gulosos/preguiçosos, alternância, `(?<nome>…)`,
lookbehind de tamanho fixo, lookahead, `\p{L}`, e `$1`–`$9` nas substituições. Nada além disso.

---

## O que não está feito

1. **Modo comando** no macOS — selecionar texto, segurar uma segunda tecla, "deixa mais
   formal". (Feito no Windows; veja "Limpeza do ditado e modo comando".)
2. **Configuração inicial** no macOS — uma janela de primeiro uso que guie pelas permissões.
3. **Notarização** (macOS) e **assinatura de código** (Windows). Os dois apps não são assinados
   para distribuição, então usuários do Windows vão ver o SmartScreen.

## O que nenhum CI consegue verificar

Algumas coisas só se verificam numa máquina real, à mão:

- O texto injetado chegando ao app em primeiro plano — os runners têm área de trabalho
  interativa mas não conseguem assumir o primeiro plano.
- Um microfone real: negociação de formato, o bloqueio de privacidade do SO, desconectar no meio
  da captura.
- O hook de teclado disparando com um toque físico.
- O Parakeet transcrevendo fala real, e se ~2 GB residentes são toleráveis.
- Chamadas reais do Teams: detecção da reunião e leitura de quem está falando.

Tudo o que depende disso fica atrás de uma interface e é testado com fakes. As ligações em si
não. **Depois de mudanças nessas partes, comece com `--selftest` e depois um ditado curto no
Bloco de Notas.**
