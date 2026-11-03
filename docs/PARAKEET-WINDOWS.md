# Obtendo o modelo de fala no Windows

O Windows **não tem equivalente ao `SpeechAnalyzer` da Apple**. No macOS esse framework vem com
o sistema, gerencia seus próprios modelos e não precisa de download. Não existe nada parecido no
Windows — então o Parakeet não é o upgrade opcional que é no macOS, é o *único* motor. O app não
consegue transcrever até esses arquivos estarem no disco.

> **Ninguém precisa fazer isto à mão.** O instalador baixa a v3 (com verificação SHA-256) e,
> se esse download falhar, o próprio app baixa o modelo ao abrir, com barra de progresso na tela
> Início — ou pelo botão "Baixar" em Configurações. Esta página serve para conferir uma
> instalação, instalar numa máquina sem internet ou entender o que está em jogo.

Esta página foi escrita para ser seguida por uma pessoa **ou entregue tal como está a um agente
de código**.

---

## O que você está baixando

**NVIDIA Parakeet TDT 0.6B**, convertido para ONNX e quantizado em int8, rodando via
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx).

| | |
|---|---|
| Tamanho do download | **~661 MB** |
| Memória em uso depois de carregado | **~2 GB** — veja [Memória](#memory) |
| Velocidade | ~40× o tempo real na CPU (uma fala de 10 s é transcrita em ~250 ms) |
| Rede | Necessária **uma vez**, para o download. A transcrição é totalmente offline. |
| Licença | Pesos do modelo **CC-BY-4.0**; sherpa-onnx **Apache-2.0**. Uso comercial permitido com atribuição — veja [Licença](#licence). |

Duas variantes. Escolha uma:

| Repositório | Idiomas | `tokens.txt` |
|---|---|---|
| `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` | **Só inglês** | 1025 entradas |
| `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | **25 idiomas** (inclui português) | 8193 entradas |

**Use a v3** — é a que o instalador e o app baixam, e a única que entende português. A v2 só
serve se você dita apenas em inglês; com as duas no disco o app usa a v3. Mesmo tamanho de
encoder, mesma velocidade; a v3 simplesmente tem um vocabulário maior.

Os exemplos abaixo usam a v3.

---

## Onde os arquivos ficam

```
%LOCALAPPDATA%\TalkToText\models\parakeet-v3\
    encoder.int8.onnx     ~652 MB
    decoder.int8.onnx     ~12 MB
    joiner.int8.onnx      ~6,4 MB
    tokens.txt            ~94 KB
```

O app procura aqui primeiro, depois ao lado do executável (`AppContext.BaseDirectory\models\`).
Configurações → Modelo mostra o caminho encontrado e se os arquivos estão lá.

> **Não coloque esses arquivos dentro da pasta do app se instalar em `Program Files`.** Esse
> local exige direitos de administrador para escrita, então o app não consegue baixar nem
> atualizar o modelo sozinho.

---

## Opção A — PowerShell, sem ferramentas extras *(recomendado)*

Nada para instalar. Cole isto no PowerShell:

```powershell
$dir = "$env:LOCALAPPDATA\TalkToText\models\parakeet-v3"
$base = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main"

New-Item -ItemType Directory -Force $dir | Out-Null

foreach ($f in "encoder.int8.onnx","decoder.int8.onnx","joiner.int8.onnx","tokens.txt") {
    Write-Host "Baixando $f ..."
    curl.exe -L --fail --progress-bar -o "$dir\$f" "$base/$f"
}

Get-ChildItem $dir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
```

> **`curl.exe`, com o `.exe`.** `curl` sozinho no PowerShell é um alias para
> `Invoke-WebRequest`, que é outro programa. E se usar `Invoke-WebRequest`, você **precisa**
> passar `-OutFile` — sem isso o PowerShell guarda os 650 MB inteiros na memória.

Saída esperada:

```
Name                MB
----                --
decoder.int8.onnx   11.3
encoder.int8.onnx   622.0
joiner.int8.onnx    6.1
tokens.txt          0.1
```

---

## Opção B — CLI do Hugging Face

O comando é **`hf`**. O `huggingface-cli` está obsoleto.

```powershell
pip install -U "huggingface_hub[cli]"

hf download csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 `
  --include "encoder.int8.onnx" "decoder.int8.onnx" "joiner.int8.onnx" "tokens.txt" `
  --local-dir "$env:LOCALAPPDATA\TalkToText\models\parakeet-v3"
```

**Não é preciso token do Hugging Face.** Esses repositórios são públicos. Se pedir login, há um
erro de digitação no nome do repositório.

---

## Opção C — git

```powershell
git lfs install
git clone https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8
```

Clona arquivos extras que você não precisa (pesos fp32, áudios de teste) — cerca de 3 GB. Tudo
bem se você já tem `git-lfs`; senão, use a Opção A.

---

## Conferindo se deu certo

```powershell
$dir = "$env:LOCALAPPDATA\TalkToText\models\parakeet-v3"
$expect = @{
  "encoder.int8.onnx" = 652184281
  "decoder.int8.onnx" = 11845275
  "joiner.int8.onnx"  = 6355277
  "tokens.txt"        = 93939
}
foreach ($k in $expect.Keys) {
    $p = Join-Path $dir $k
    if (-not (Test-Path $p)) { Write-Host "FALTANDO  $k" -ForegroundColor Red; continue }
    $got = (Get-Item $p).Length
    $ok = $got -eq $expect[$k]
    Write-Host ("{0,-20} {1,12:N0} bytes  {2}" -f $k, $got, $(if($ok){"OK"}else{"TAMANHO DIFERENTE"}))
}
```

**Um download truncado é a falha mais comum e não se anuncia** — um `encoder.int8.onnx` parcial
falha ao carregar o modelo com um erro opaco de parse de protobuf. Confira os tamanhos antes de
reportar um bug.

O `tokens.txt` deve ser texto puro, um par `token id` por linha, começando com:

```
<unk> 0
▁t 1
▁th 2
```

---

## <a id="memory"></a>Memória: leia antes de escolher uma máquina

**O modelo precisa de cerca de 2 GB de RAM residentes, mesmo para uma fala de dois segundos.**
São os pesos int8 mais a área de memória do runtime de inferência — não é vazamento e não é
específico deste app (o mesmo consumo aparece rodando o modelo pelo Python).

Pico de memória medido por duração do trecho:

| Áudio | Pico de RAM |
|---|---|
| 7 s | 2,0 GB |
| 60 s | 2,5 GB |
| 300 s | 4,3 GB |

**Numa máquina com 8 GB, o app vai ocupar um espaço perceptível.** Com 16 GB fica confortável.

### O limite de 400 segundos

O encoder exportado tem uma tabela fixa de posições relativas dimensionada para **5000
quadros**. A 80 ms por quadro isso dá **400 segundos**, e acima disso a inferência *falha* em
vez de degradar:

```
Non-zero status code returned while running Add node.
Attempting to broadcast an axis by a dimension other than 1. 126 by 5126
```

O app divide o ditado em segmentos de 30–60 segundos nos silêncios, e reuniões e arquivos de
áudio são cortados em falas pelo Silero VAD, então você não chega a isso no uso normal.

---

## Hardware e aceleração

**Só CPU. É deliberado, não uma limitação esquecida.**

- O sherpa-onnx **não tem pacote para GPU**. Configurar um provider CUDA volta para a CPU em
  silêncio.
- O runtime DirectML está cinco versões menores atrás da principal, proíbe inferência paralela
  e exige formatos de tensor fixos — o que este modelo, com entrada de áudio de tamanho
  variável, não pode oferecer.
- CUDA obrigaria todo usuário a instalar um toolkit CUDA compatível.

Nada disso compensa. Medido na CPU com pesos int8, a transcrição roda ~40× mais rápido que o
tempo real. Quatro threads é o ponto ideal — oito mediu *mais lento* que quatro.

(A IA local de resumos e chat é outra história: ela usa a GPU via Vulkan — veja o `AGENTS.md`.)

**Windows ARM64:** instale o build ARM64 do app. O build x64 roda em emulação e a transcrição
fica muito mais lenta.

---

## Solução de problemas

| Sintoma | Causa |
|---|---|
| "Modelo não instalado" | Use o botão "Baixar modelo de voz" na tela Início. Numa instalação manual, Configurações → Modelo mostra o caminho exato verificado. |
| Português sai como texto sem sentido | O app está usando a v2, só em inglês. A tela Início oferece baixar a v3. |
| Erro de protobuf / parse ao carregar | Download truncado. Rode de novo a conferência de tamanhos acima, ou apague a pasta e baixe pelo app. |
| Carrega, mas toda transcrição sai vazia | O Windows está bloqueando o microfone. Configurações → Privacidade e segurança → Microfone → **Permitir que aplicativos da área de trabalho acessem o microfone**. Com isso desligado o WASAPI devolve *silêncio*, não um erro. |
| Primeira transcrição muito lenta | Carregamento do modelo, ~2 s. Só na primeira fala depois de abrir o app. |
| Sempre lento | Você pode estar rodando o build x64 numa máquina ARM64. |
| Falha em áudio de mais de ~7 minutos sem pausas | O limite de 400 segundos acima. |

---

## <a id="licence"></a>Licença e atribuição

**Pesos do modelo: CC-BY-4.0.** Uso comercial é permitido. Se você redistribuir o modelo dentro
de um produto, precisa:

1. **Dar crédito à NVIDIA**, nomear o modelo e linkar tanto o model card quanto
   <https://creativecommons.org/licenses/by/4.0/>.
2. **Informar que ele foi modificado** — a quantização int8 e a exportação ONNX são
   modificações.
3. **Não acrescentar restrições.** Se sua EULA proíbe extrair arquivos empacotados, ela precisa
   abrir exceção para os pesos do modelo.

Um `THIRD-PARTY-NOTICES.txt` mais uma linha na janela Sobre resolvem isso.

O **sherpa-onnx** é Apache-2.0. O **ONNX Runtime** é MIT.

> Não é aconselhamento jurídico. CC-BY-4.0 é uma licença de conteúdo, não de software, e não
> concede patentes. Se for distribuir comercialmente, peça a um advogado para confirmar.

---

## Para um agente de código

O mínimo para reproduzir uma configuração funcionando:

```
Repo:    csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8   (HuggingFace, público, sem token)
Files:   encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, tokens.txt
Target:  %LOCALAPPDATA%\TalkToText\models\parakeet-v3\
NuGet:   org.k2fsa.sherpa.onnx 1.13.5
         org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.5   (ou .win-arm64)
```

Configuração que funciona de verdade — cada campo conferido numa transcrição rodando:

```csharp
var cfg = new OfflineRecognizerConfig();
cfg.FeatConfig.SampleRate  = 16000;
cfg.FeatConfig.FeatureDim  = 128;                  // NÃO o padrão 80
cfg.ModelConfig.Transducer.Encoder = $"{dir}/encoder.int8.onnx";
cfg.ModelConfig.Transducer.Decoder = $"{dir}/decoder.int8.onnx";
cfg.ModelConfig.Transducer.Joiner  = $"{dir}/joiner.int8.onnx";
cfg.ModelConfig.Tokens     = $"{dir}/tokens.txt";
cfg.ModelConfig.ModelType  = "nemo_transducer";    // OBRIGATÓRIO — sem isso o carregamento falha
cfg.ModelConfig.NumThreads = 4;
cfg.ModelConfig.Provider   = "cpu";
cfg.DecodingMethod         = "greedy_search";
```

Quatro coisas que custam uma hora cada se esquecidas:

1. **`ModelType = "nemo_transducer"` é obrigatório.** Sem ele o modelo não carrega.
2. **`FeatureDim = 128`**, não o padrão 80.
3. **Defina um `RuntimeIdentifier`** (`-r win-x64`). Sem ele, as bibliotecas nativas vão para
   `runtimes/<rid>/native/` e você recebe `DllNotFoundException` em tempo de execução.
4. **`WaveReader` não está no pacote NuGet** — é um auxiliar dos exemplos. Escreva seu próprio
   parser de WAV, ou passe amostras `float[]` em `[-1, 1]` diretamente.

O áudio precisa ser **16 kHz mono float32**. No Windows, o modo compartilhado do WASAPI reamostra
para você se pedir esse formato antes de abrir o dispositivo.
