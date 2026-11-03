# Talk-to-Text

Ditado por voz *push-to-talk*, 100% local. Segure o atalho, fale, solte — o texto é
colado onde o cursor estiver. Nada sai do computador: sem nuvem, sem chave de API.

O foco do projeto é o **app para Windows** (pasta [`windows/`](windows/)). Também existe uma
versão para macOS na raiz do repositório, de onde o projeto nasceu.

---

## Recursos (Windows)

- **Ditado em qualquer app** — segure **Ctrl + Win** (configurável: uma ou duas teclas,
  segurar para falar ou apertar para alternar). Uma pílula na parte de baixo da tela mostra
  a gravação.
- **Transcrição local com Parakeet v3** (25 idiomas, incluindo português), rodando na CPU via
  sherpa-onnx.
- **Limpeza do ditado** — tira hesitações ("éé", "hã", palavras repetidas) na hora, sem IA.
- **Dicionário pessoal** — nomes e jargões que o reconhecimento costuma errar.
- **IA local (Qwen2.5 3B)** — organiza o texto ditado e executa comandos falados sobre o
  texto selecionado ("deixa mais formal").
- **Reuniões** — detecta reuniões (ex.: Microsoft Teams) com o microfone aberto, transcreve
  com separação de quem falou, lembra vozes já nomeadas e gera resumo com a IA local.
- **Arquivos de áudio** — transcreve gravações existentes, com player integrado.
- **Busca e chat** — encontre reuniões pelo que foi dito (ignora acentos) e faça perguntas
  sobre as transcrições.
- **Exportação** — Word, PDF, Markdown ou texto puro.

Os modelos são baixados sob demanda: Parakeet (~670 MB) na instalação, o modelo de resumo
(~2 GB) só quando você pede o primeiro resumo.

---

## Instalação (Windows)

Gere o instalador (requer o .NET SDK fixado em [`windows/global.json`](windows/global.json)
e o Inno Setup):

```powershell
winget install JRSoftware.InnoSetup
cd windows
.\installer\build-installer.ps1 -Version 0.2.0
```

O resultado fica em `windows\artifacts\installer\TalkToText-Setup-0.2.0.exe`. Ele instala
por usuário (sem UAC) em `%LOCALAPPDATA%\Programs\Talk-to-Text` e baixa o Parakeet para
`%LOCALAPPDATA%\TalkToText\models`.

O instalador **não é assinado**: no primeiro uso o SmartScreen mostra "O Windows protegeu
o computador" — clique em *Mais informações → Executar assim mesmo*.

Para baixar o modelo manualmente, veja [`docs/PARAKEET-WINDOWS.md`](docs/PARAKEET-WINDOWS.md).

---

## Desenvolvimento

```bash
cd windows
dotnet build TalkToText.sln --no-incremental -warnaserror
dotnet test  TalkToText.sln
```

Fora do Windows, use o filtro de solução, que omite a camada Win32 e roda o resto
(inclusive os testes de UI headless):

```bash
cd windows
dotnet test TalkToText.CrossPlatform.slnf -c Release
```

Arquitetura, decisões técnicas e o que está ou não verificado estão em
[`windows/README.md`](windows/README.md). Leia também o [`AGENTS.md`](AGENTS.md) antes de
mexer no código.

---

## Estrutura

```
windows/    App Windows — C# / .NET 10 / Avalonia (foco do projeto)
Sources/    App macOS — Swift 6 / SwiftUI
Tests/      Testes do app macOS
shared/     Vetores de teste do dicionário, compartilhados pelas duas plataformas
bench/      Benchmarks dos motores de transcrição (macOS)
docs/       Documentação: modelo no Windows, app macOS
```

O comportamento do dicionário é um contrato entre as duas plataformas:
[`shared/dictionary-test-vectors.json`](shared/dictionary-test-vectors.json). As duas
implementações rodam esses vetores; mudanças de comportamento começam por ali.

---

## macOS

A versão para macOS (ainda com o nome original, *Murmur YouTube*) requer macOS 26 e é
compilada com `make install`. Detalhes de permissões, assinatura e arquitetura em
[`docs/MACOS.md`](docs/MACOS.md).

---

## Créditos e licença

O Talk-to-Text começou a partir do [murmur-youtube](https://github.com/per-simmons/murmur-youtube),
de Pat Simmons. O tema visual do app Windows segue o do OpenWhispr (MIT).

Distribuído sob a licença [MIT](LICENSE).
