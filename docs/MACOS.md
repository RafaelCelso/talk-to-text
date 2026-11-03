# Talk-to-Text para macOS

Ditado *push-to-talk* para macOS. Segure uma tecla, fale, solte — o texto limpo cai no campo de
texto que estiver com o foco. Nativo e totalmente local.

**Status:** funciona, mas não é mais o foco do projeto (o foco é o [app Windows](../windows/README.md)).
Requer **macOS 26**. Não há Mac no fluxo de trabalho atual, então mudanças aqui não são
compiladas localmente; o CI só compila e testa o alvo do dicionário.

---

## Convivendo com outro app de ditado

O app foi feito para rodar ao lado de outras ferramentas de ditado sem colidir com elas, o que
não é automático no macOS:

- **Bundle ID `com.rafaelcelso.talktotext`** — o TCC associa as permissões de Acessibilidade e
  Microfone ao bundle ID, então conceder ou revogar uma permissão aqui não afeta nenhum outro
  app, e vice-versa.
- **Executável `TalkToText`** — sem espaços e específico o bastante para `pkill -x TalkToText`
  não atingir outro binário. O `Makefile` só mexe em `$(EXEC)`.
- **O atalho é configurável** (⌥ direito / fn / ⌘ direito) justamente porque outra ferramenta
  pode já usar a tecla que você escolheria primeiro. O event tap só inspeciona o próprio código
  de tecla e deixa todo o resto passar intacto.

Se você usa mais de um app de ditado, dê a cada um uma tecla diferente. Dois apps na mesma tecla
gravam juntos, e o texto injetado por um briga com o do outro.

---

## Começo rápido

```bash
make install     # compila, empacota, assina, copia para /Applications e abre
```

Depois conceda duas permissões — nenhuma é opcional, e nenhuma pode ser pedida em silêncio:

| Permissão | Onde | Para quê |
|---|---|---|
| **Acessibilidade** | Ajustes do Sistema ▸ Privacidade e Segurança ▸ Acessibilidade | O `CGEventTap` que vê o atalho, e a inserção de texto via AX |
| **Microfone** | Pedida no primeiro ditado | Captura de áudio |

Reabra o Talk-to-Text depois de conceder a Acessibilidade. Então segure **⌥ direito** e fale.

> Builds anteriores usavam outro nome e outro bundle ID. Depois de atualizar, as permissões
> precisam ser concedidas de novo, e o dicionário e o histórico ficam numa pasta nova de
> Application Support (`TalkToText`).

### Por que as permissões sobrevivem às recompilações

O TCC guarda um *requisito de assinatura* por entrada, não só um caminho. Uma assinatura ad-hoc
muda a cada build, então o binário recompilado deixa de satisfazer o requisito guardado — e o
sintoma é traiçoeiro: a chave de Acessibilidade **continua ligada** enquanto o app é tratado
como não confiável, e alternar a chave não muda nada porque o problema é a linha antiga.

Por isso o `Makefile` assina com um Developer ID estável (detectado via
`security find-identity`, com ad-hoc como alternativa). Com ele, recompilar e reinstalar mantém
as duas permissões sem pedir de novo.

Se uma permissão travar, redefina só aquela linha e adicione de novo — nunca alterne a chave:

```bash
tccutil reset Accessibility com.rafaelcelso.talktotext
tccutil reset Microphone   com.rafaelcelso.talktotext
```

Sempre passe o bundle ID. Um `tccutil reset Accessibility` sem ele apaga a permissão de
**todos** os apps da máquina. Depois feche os Ajustes do Sistema por completo (⌘Q) antes de
reabrir — esse painel guarda a lista em cache e mostraria a linha que você acabou de apagar.

> **Mantenha o build fora do iCloud.** `~/Desktop` e `~/Documents` podem ser sincronizados por
> file provider; o sincronizador pode materializar/desmaterializar arquivos dentro de um `.app`
> e corromper a assinatura. `make install` coloca a cópia em uso em `/Applications`.

Outros alvos: `make app` (só o pacote), `make run` (roda no lugar), `make clean`.

---

## Arquitetura

```
 segura tecla ─► HotkeyMonitor ──► DictationController ◄── Settings
                                    │
                         ┌──────────┼──────────┐
                         ▼          ▼          ▼
                  AudioCapture  HUDPanel   TranscriptionEngine
                         │                      │
                    (AudioChunk) ──ordenado──► AppleSpeechEngine / ParakeetEngine
                                                │
                                          (transcrição)
                                                ▼
                                          TextFormatter
                                                ▼
                                          TextInjector ─► app em foco
```

### Decisões que vale conhecer

**O HUD nunca pode pegar o foco.** `HUDPanel` é um `.nonactivatingPanel` com
`canBecomeKey == false`. É o detalhe estrutural do app inteiro: se o overlay virasse a janela
principal, o campo de texto do usuário perderia o foco e não sobraria onde injetar. Todo o resto
é substituível; isto não.

**O atalho precisa de um `CGEventTap`, não de `NSEvent`.** `fn` e a distinção entre
modificadores esquerdo/direito não aparecem via `NSEvent.addGlobalMonitorForEvents` nem pela API
de atalhos do Carbon. Um event tap de sessão é o único jeito de vê-los — por isso a permissão de
Acessibilidade é obrigatória, não um extra.

**A ordem do áudio é explícita.** `AudioCapture` entrega para um `AsyncStream` drenado por uma
única task. Criar uma `Task` por buffer seria mais simples e corromperia a transcrição em
silêncio, porque tasks não estruturadas não garantem ordem.

**Buffers são copiados, nunca emprestados.** O `AVAudioEngine` recicla o buffer que entrega ao
tap assim que o callback retorna. O `@unchecked Sendable` de `AudioChunk` só é seguro porque
`AudioCapture` sempre aloca memória nova antes de repassar.

**Duas costuras substituíveis.** `TranscriptionEngine` e `TextFormatter` são protocolos, para
que os dois componentes com mais chance de mudar possam mudar sem mexer no resto.

### Estrutura

```
Sources/TalkToTextDictionary/     correções e biasing (contrato compartilhado com o Windows)
Sources/TalkToText/
├── TalkToTextApp.swift           @main, AppDelegate, MenuBarExtra
├── Core/
│   ├── DictationController.swift   máquina de estados, liga tudo
│   ├── HotkeyMonitor.swift         CGEventTap em .flagsChanged
│   ├── AudioCapture.swift          tap do AVAudioEngine + conversão de formato + RMS
│   ├── TextInjector.swift          inserção via AX, alternativa pasteboard+⌘V
│   ├── EngineComparison.swift      modo de comparação entre motores
│   └── WisprTrigger.swift
├── Dictionary/
│   └── DictionaryStore.swift
├── Transcription/
│   ├── TranscriptionEngine.swift   protocolo + AudioChunk
│   ├── AppleSpeechEngine.swift     SpeechAnalyzer / SpeechTranscriber
│   ├── ParakeetEngine.swift        Parakeet via FluidAudio
│   └── WisprReader.swift
├── Formatting/
│   ├── TextFormatter.swift         protocolo + RuleBasedFormatter
│   └── FoundationModelFormatter.swift
├── UI/
│   ├── MainWindow.swift, SettingsWindow.swift, ComparisonWindow.swift
│   ├── HUDPanel.swift              painel flutuante que não ativa
│   ├── HUDView.swift               forma de onda + transcrição ao vivo
│   ├── DesignSystem.swift, Equipment.swift, DictionaryPanel.swift, DashboardHTML.swift
└── Support/
    └── Settings.swift, Permissions.swift, Log.swift, RunLog.swift
```

---

## Motor de fala

O padrão é o **`SpeechAnalyzer` / `SpeechTranscriber`** da Apple, novo no macOS 26: sem
dependência, sem modelo embutido, sem nuvem, streaming de verdade com `.volatileResults` para o
texto aparecer enquanto você ainda fala. O SO baixa e gerencia os modelos, então a primeira
execução num idioma pode pausar em `AssetInstallationRequest`.

A alternativa é o **Parakeet v3** via FluidAudio (CoreML no Neural Engine) — WER em inglês
mensuravelmente melhor, ~110× o tempo real, ~66 MB residentes. Veja o
[benchmark](../bench/README.md).

| | Apple SpeechTranscriber | Parakeet v3 (FluidAudio) | Whisper large-v3 (WhisperKit) |
|---|---|---|---|
| Dependência | nenhuma | SwiftPM | SwiftPM |
| Download do modelo | gerenciado pelo SO | ~600 MB | ~1,5 GB |
| Precisão em inglês | boa | a melhor | boa |
| Idiomas | muitos | 25 | 99 |
| Latência | baixa | ~80 ms | 200–500 ms |

---

## O que não está feito no macOS

1. **Modo comando.** Selecionar texto, segurar uma segunda tecla, dizer "deixa mais formal".
   Precisa ler `kAXSelectedTextAttribute` via AX mais uma ida e volta a um LLM. (Existe no
   Windows.)
2. **Configuração inicial.** Uma janela de primeiro uso que guie pelas duas permissões em vez de
   depender dos itens "Conceder…" do menu.
3. **Notarização.** Encerra o vaivém de redefinir o TCC e torna o app distribuível.

> `log` pode estar sombreado no shell — use `/usr/bin/log` explicitamente. Os logs do app usam o
> subsystem `com.rafaelcelso.talktotext`.
