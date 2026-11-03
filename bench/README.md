# Benchmark de motores de fala

O `SpeechTranscriber` do macOS 26 da Apple contra o **Parakeet TDT 0.6B** da NVIDIA rodando no
Neural Engine, no mesmo áudio, avaliados do mesmo jeito.

Independente — não compila nem mexe no app Talk-to-Text.

```bash
cd bench
swift run bench record take1.wav          # fale e pressione RETURN
swift run bench run take1.wav             # transcreve com os dois e abre o painel
```

Para medir a precisão, escreva o que você realmente disse e passe como referência:

```bash
echo "the quick brown fox jumps over the lazy dog" > take1.txt
swift run bench run take1.wav --ref take1.txt
```

Flags: `--v2` (Parakeet só em inglês, maior recall), `--int4` (encoder menor e mais rápido).

Gera `take1-results.json` e `take1-dashboard.html`, e abre o painel.

---

## De onde vem o modelo Parakeet

O Parakeet é o modelo de reconhecimento de fala da NVIDIA, treinado no framework NeMo e
publicado como pesos PyTorch. Nesse formato ele não roda no Neural Engine.

A **Fluid Inference** é um projeto open source que converte modelos de áudio de ponta para
**CoreML** e os empacota num SDK Swift chamado **FluidAudio** — o mesmo que o WhisperKit da
Argmax faz com o Whisper. Os pesos convertidos estão publicados no Hugging Face:

| | |
|---|---|
| Repositório | [`FluidInference/parakeet-tdt-0.6b-v3-coreml`](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v3-coreml) |
| Modelo base | `nvidia/parakeet-tdt-0.6b-v3` |
| Licença | CC-BY-4.0 |
| Idiomas | 25 (inglês + europeus) |

Basta o `curl` para inspecioná-lo — a API do Hugging Face é pública e não exige autenticação:

```bash
REPO=FluidInference/parakeet-tdt-0.6b-v3-coreml

# metadados
curl -s "https://huggingface.co/api/models/$REPO" | jq '{id, downloads, likes, pipeline_tag}'

# todos os arquivos com tamanhos
curl -s "https://huggingface.co/api/models/$REPO/tree/main?recursive=true" \
  | jq -r '.[] | select(.type=="file") | "\(.lfs.size // .size)\t\(.path)"' \
  | sort -rn | head

# um arquivo, direto
curl -L -O "https://huggingface.co/$REPO/resolve/main/Encoder.mlmodelc/weights/weight.bin"
```

O repositório soma ~3 GB porque traz várias variantes da mesma rede — pacotes `.mlmodelc`
compilados, a forma-fonte `.mlpackage` e um encoder quantizado em INT4.

**Você não baixa tudo isso.** Medido numa primeira execução real, o FluidAudio baixou
exatamente quatro modelos compilados, somando **469 MB**, em
`~/Library/Application Support/FluidAudio/Models/parakeet-tdt-0.6b-v3/`:

| Componente | Papel | Pesos |
|---|---|---|
| `Encoder.mlmodelc` | modelo acústico (INT8) | 433 MB |
| `Decoder.mlmodelc` | decodificador do transducer | 23 MB |
| `JointDecisionv3.mlmodelc` | rede conjunta | 12 MB |
| `Preprocessor.mlmodelc` | áudio → features mel | <1 MB |

Repare que ele pega o `Preprocessor` pequeno em vez do `MelEncoder` de 595 MB, e o encoder INT8
em vez das fontes `.mlpackage` — por isso o download é um sexto do repositório. Não deduza o
tamanho do download pelo tamanho do repositório.

Você nunca chama nada disso à mão — `AsrModels.downloadAndLoad(version: .v3)` baixa e guarda em
cache. Para baixar antes ou trabalhar offline, a CLI oficial espelha o repositório inteiro:

```bash
pip install -U "huggingface_hub[cli]"
hf download FluidInference/parakeet-tdt-0.6b-v3-coreml
```

O cache fica em `~/.cache/huggingface/`; a cópia do FluidAudio em
`~/Library/Application Support/FluidAudio/Models/`. Apague qualquer um para forçar novo download.

---

## O que é medido

**Segundos de processamento** — tempo de relógio de "começar a transcrever" até o texto final.
O carregamento do modelo fica de fora, porque os dois não são comparáveis: os modelos da Apple
já estão residentes no SO, enquanto o Parakeet paga um carregamento único por processo. O tempo
de carga é informado à parte.

**RTF** — segundos de áudio processados por segundo de relógio. 100× significa que um trecho de
60 segundos é transcrito em 0,6 s. Quanto maior, mais rápido.

**WER / CER** — taxa de erro de palavras e de caracteres em relação à sua referência, por
distância de Levenshtein. Avaliado em texto *normalizado*: minúsculas, sem pontuação, espaços
colapsados. Isso importa — a Apple adiciona pontuação automaticamente, e contra uma referência
sem pontuação isso contaria como dezenas de erros fantasmas e tornaria a comparação inútil. O
CER está incluído porque o WER conta um quase-acerto ("Kubernetes" → "cubernetties") como uma
palavra totalmente errada, o que exagera o estrago em vocabulário técnico.

### O que isto *não* mede

Os dois motores rodam em **lote** aqui: o arquivo inteiro entra, o texto final sai. É o jeito
justo de comparar vazão, mas não é como o ditado é sentido.

O `SpeechTranscriber` é nativamente streaming — com `.volatileResults` ele mostra as palavras
enquanto você fala, então a latência percebida é quase zero independentemente do RTF. O caminho
em lote do Parakeet só começa depois que você para de falar. O FluidAudio oferece o
`SlidingWindowAsrManager` para streaming de verdade, que seria a comparação justa para latência
percebida, mas é outra medição e não está ligada aqui.

Então: **o RTF responde "qual é mais rápido para transcrição em massa", não "qual parece mais
ágil ao ditar".** Num app push-to-talk em que você solta a tecla e espera, a latência em lote é
próxima do que você sente — mas não leia estes números como a história completa.
