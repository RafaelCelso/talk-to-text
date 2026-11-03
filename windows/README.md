# Talk-to-Text para Windows

Ditado *push-to-talk*, local, no Windows.

Segure **Ctrl + Win** (configurável: uma ou duas teclas, segurar para falar ou apertar para
alternar), fale, e o texto é colado onde o cursor estiver. Uma pílula no centro inferior da tela
mostra o início, o andamento e o fim da gravação. A UI segue o tema escuro open source (MIT) do
OpenWhispr; os tokens ficam em `src/TalkToText.App/Design/DesignTokens.cs`.

> **Status: roda em hardware real desde 2026-11-01** (Windows 11, microfone USB, português com
> Parakeet v3) — ditado no Bloco de Notas e no WhatsApp funcionou de ponta a ponta. Além do
> ditado, o app transcreve reuniões e arquivos de áudio, gera resumos e responde perguntas com
> uma IA local; veja o [`AGENTS.md`](../AGENTS.md) para os detalhes de cada recurso.

---

## Por que é uma reescrita, não um port

Quase toda camada do app macOS é específica da Apple:

| Camada | macOS | Windows |
|---|---|---|
| UI | SwiftUI | Avalonia |
| Captura de áudio | `AVAudioEngine` | WASAPI via NAudio |
| **Motor de fala padrão** | `SpeechAnalyzer` (vem com o macOS 26) | **não existe equivalente** |
| Parakeet | FluidAudio → CoreML | sherpa-onnx → ONNX Runtime |
| Atalho de teclado | `CGEventTap` | `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| Injeção de texto | API de Acessibilidade | `SendInput` |

A consequência que molda tudo: **o Windows não tem equivalente ao `SpeechAnalyzer` da Apple.**
No macOS o Parakeet é o upgrade opcional. No Windows ele é o único motor, e o app não consegue
transcrever até o modelo ser baixado — veja [`docs/PARAKEET-WINDOWS.md`](../docs/PARAKEET-WINDOWS.md).

O que *é* de fato compartilhado é o comportamento do dicionário, e ele é compartilhado como
contrato, não como código: [`shared/dictionary-test-vectors.json`](../shared/dictionary-test-vectors.json).
As duas implementações rodam esses vetores no CI. Mudar a semântica das correções começa por ali.

---

## Decisões, e por quê

**Avalonia, não WPF nem WinUI 3.** WPF não roda nem tem testes de UI fora do Windows. O teste
headless do Avalonia roda em qualquer SO em ~100 ms, incluindo entrada de teclado simulada e
captura real de pixels. A interoperabilidade Win32 não é afetada — hooks, `SendInput` e WASAPI
são P/Invoke, não código do framework de UI. O WinUI 3 foi descartado de cara: a própria
documentação da Microsoft se contradiz sobre publicar sem pacote em arquivo único, com bugs
abertos relatando um exe que não abre.

**.NET 10, não .NET 8.** O .NET 8 chega ao fim do suporte em **2026-11-10**.

**O atalho evita o Alt direito.** O Alt direito é o AltGr nos layouts alemão, polonês,
britânico, nórdicos e na maioria dos latino-americanos — é como esses usuários digitam `@`,
`€`, `\`, `|`. Usá-lo para o ditado quebraria a digitação básica de muita gente. O padrão é
**Ctrl + Win**, que não produz caractere em nenhum layout.

**O atalho é observado, nunca engolido.** O build macOS consome o Option direito porque no
macOS essa tecla digita caracteres. No Windows, suprimir não ganha nada e arrisca uma falha bem
pior: se o key-down é engolido mas o key-up escapa — um hook que expirou no meio do gesto, ou o
foco passando para uma janela elevada — o app de destino acha que o Ctrl ficou pressionado para
sempre.

**Inferência de fala só na CPU.** O sherpa-onnx não tem pacote para GPU; o DirectML está cinco
versões atrás e proíbe os formatos de tensor variáveis que este modelo exige; CUDA obrigaria
todo usuário a instalar um toolkit. Na CPU com pesos int8, a transcrição roda ~40× mais rápido
que o tempo real. (A IA local de resumos e chat usa a GPU via Vulkan.)

**Três versões fixadas que quebrariam na "latest":**

| Pacote | Fixado | Por quê |
|---|---|---|
| `NAudio` | **2.3.0** | 3.x exige .NET 9+ e não restaura |
| `Avalonia.Headless.XUnit` | **11.3.20** | 12.x exige xUnit **v3**, outra linha de pacotes |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Já inclui o ONNX Runtime; nunca referencie também `Microsoft.ML.OnnxRuntime` |

---

## Estrutura

```
windows/
├─ Directory.Build.props          análise rígida, aplicada a todo projeto
├─ Directory.Packages.props       versões centralizadas
├─ global.json                    versão do SDK
├─ installer/                     instalador Inno Setup
├─ src/
│  ├─ TalkToText.Dictionary/          correções + biasing                net10.0
│  ├─ TalkToText.Abstractions/        as interfaces de plataforma        net10.0
│  ├─ TalkToText.Core/                motor, reuniões, busca, armazenamento net10.0
│  ├─ TalkToText.Speech/              Parakeet via sherpa-onnx, IA local net10.0
│  ├─ TalkToText.Testing/             fakes das interfaces               net10.0
│  ├─ TalkToText.App/                 UI Avalonia                        net10.0
│  └─ TalkToText.Platform.Windows/    o ÚNICO código Win32               net10.0-windows
└─ tests/
   ├─ TalkToText.Dictionary.Tests/    os vetores compartilhados
   ├─ TalkToText.Core.Tests/          motor, áudio, reuniões, armazenamento
   └─ TalkToText.App.Tests/           UI Avalonia headless
```

**Só um projeto tem alvo `-windows`.** Todo o resto é neutro de plataforma, então o `CA1416`
transforma uma chamada Win32 acidental em erro de compilação — e, mais útil ainda, o app inteiro
compila, roda e é testado fora do Windows.

O `TalkToText.App` carrega a camada de plataforma **por reflexão** em vez de referenciá-la. Uma
referência direta arrastaria a UI para `net10.0-windows` e acabaria com o ciclo local. O
self-test publicado verifica que a reflexão funciona de dentro do pacote single-file, porque é
ali que esse arranjo falharia — em silêncio, no momento em que o usuário apertasse a tecla pela
primeira vez.

Manter a camada de plataforma sem lógica é deliberado: tudo que vive ali é código que o CI não
consegue exercitar. Novas tentativas, debounce, tratamento de troca de dispositivo — tudo isso
pertence aos projetos neutros, atrás de uma interface.

---

## Compilando

**No Windows** — tudo, incluindo a camada de plataforma:

```bash
cd windows
dotnet build TalkToText.sln --no-incremental -warnaserror
dotnet test  TalkToText.sln
```

**No macOS ou Linux** — use o filtro de solução. `TalkToText.Platform.Windows` tem alvo
`net10.0-windows` e não compila fora do Windows; o filtro o omite e todo o resto compila e testa
normalmente, incluindo a suíte de UI completa:

```bash
cd windows
dotnet test TalkToText.CrossPlatform.slnf -c Release
```

`--no-incremental` não é opcional no CI. O Roslyn não reemite avisos de analisador num build
incremental, então `-warnaserror` passaria com resultados em cache e não provaria nada.

---

## Instalador

```powershell
winget install JRSoftware.InnoSetup      # uma vez
cd windows
.\installer\build-installer.ps1 -Version 0.2.0
```

Gera `artifacts\installer\TalkToText-Setup-0.2.0.exe`. Ele publica o mesmo exe single-file
autocontido que o CI publica, roda `--selftest` nele e o empacota com
[`installer/TalkToText.iss`](installer/TalkToText.iss). O instalador:

- instala **por usuário** em `%LOCALAPPDATA%\Programs\Talk-to-Text` — sem prompt do UAC;
- baixa o **Parakeet v3** (~670 MB, 25 idiomas) para `%LOCALAPPDATA%\TalkToText\models`,
  conferido por SHA-256, e pula o download se já houver uma cópia completa;
- cria um atalho no Menu Iniciar e, opcionalmente, um ícone na área de trabalho e o início com o
  Windows;
- mantém o modelo e as configurações na desinstalação, para uma reinstalação aproveitá-los.

Se o download do modelo falhar durante a instalação, o app o baixa sozinho ao abrir, com
progresso na tela Início — o usuário não precisa rodar nada no terminal.

Ele **não é assinado**, então o SmartScreen mostra "O Windows protegeu o computador" em outras
máquinas — *Mais informações → Executar assim mesmo*.

---

## <a id="honesty"></a>Honestidade sobre o que está verificado

**Verificado no Windows, a cada push:** a suíte de testes inteira — vetores do dicionário,
núcleo (máquina de estados do ditado, fatiamento de áudio, reuniões, armazenamento) e UI
Avalonia headless. O CI então publica um executável autocontido, **o executa**, e o binário
confirma que o dicionário funciona, que o JSON gerado por código faz ida e volta, e que a camada
de plataforma Windows carrega e é construída de dentro do pacote.

**Verificado à mão em hardware real:** ditado com microfone USB em português no Bloco de Notas e
no WhatsApp.

**Divergências conhecidas entre os dois motores de regex**, medidas em 30 casos — 9 diferiram.
As duas que afetam este código estão tratadas: comparação sem diferenciar maiúsculas sensível à
cultura (resolvida com `CultureInvariant`) e diferença NFC/NFD (resolvida normalizando os dois
lados). Duas que *não* têm conserto são simplesmente evitadas: o ICU converte `ß` em `ss` e o
.NET não, e o `.` do .NET divide pares substitutos. Nenhuma é alcançável pelos padrões que este
código monta.

**Só pode ser verificado numa máquina real:**

- Injeção de texto num app em primeiro plano. Os runners têm área de trabalho interativa mas não
  conseguem assumir o primeiro plano.
- Um microfone real: negociação de formato, o bloqueio de privacidade do SO, desconectar no meio
  da captura.
- O hook de teclado de baixo nível disparando com um toque físico.
- O Parakeet transcrevendo fala real, e se ~2 GB de memória são toleráveis.

Tudo o que depende disso fica atrás de uma interface e é exercitado com fakes, então a lógica em
volta é testada. As ligações em si não.
